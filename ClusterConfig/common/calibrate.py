#!/usr/bin/env python3
"""Isolated llama.cpp context calibration, invoked by setup-<node>.sh --calibrate.
Edit calibration/model settings in the node YAML. Tests real chat prompts at
~75–90% per-slot context (room for reasoning), concurrent configured slots, measures streaming latency, retrieval, memory and GPU offload.
Stages missing runtime/weights, pauses known GPU services and restores them.
Does not prune models or permanently change service configuration.
--apply edits only tested context/cache fields; run setup again to deploy them.
Reports and per-candidate logs remain in ~/inference/benchmarks. No daemon.
"""
import contextlib
import concurrent.futures
import csv
import io
import hashlib
import json
import os
import platform
import random
import re
import secrets
import socket
import subprocess
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

import yaml
from manage import api, atomic, confirm, fail, model_command, model_id, owned, say, REPO, flash_mode, slot_count, offload_counts, http_error_detail

MIB = 2**20


def meminfo():
    fields = dict(re.findall(r'^(\w+):\s+(\d+) kB', Path('/proc/meminfo').read_text(), re.M))
    vm = dict(line.split() for line in Path('/proc/vmstat').read_text().splitlines())
    return {'host_free_mib': int(fields['MemAvailable']) / 1024,
            'swap_used_mib': (int(fields['SwapTotal']) - int(fields['SwapFree'])) / 1024,
            'swapout_pages': int(vm['pswpout'])}


def intel_memory_used(output):
    """Parse device-level metric 18 from xpu-smi dump (not utilization %)."""
    for i, line in enumerate(output.splitlines()):
        if 'GPU Memory Used (MiB)' not in line:
            continue
        rows = list(csv.DictReader(io.StringIO('\n'.join(output.splitlines()[i:]))))
        values = []
        for row in rows:
            row = {k.strip(): (v or '').strip() for k, v in row.items() if k is not None}
            if 'TileId' in row and row['TileId'] not in ('', '-1', 'N/A'):
                # Do not mistake a single tile's usage for device-wide free memory.
                continue
            if row.get('GPU Memory Used (MiB)'):
                values.append(float(row['GPU Memory Used (MiB)']))
        if len(values) == 1:
            return values[0]
    return None


def drm_memory_free(hw, region, root=Path('/sys/class/drm')):
    """Read only real DRM cards; skip connectors and incomplete device entries."""
    values = {}
    for card in sorted(root.glob('card*')):
        if not re.fullmatch(r'card\d+', card.name):
            continue
        device = card / 'device'
        try:
            if (device / 'vendor').read_text().strip() != hw['vendor']:
                continue
            if hw.get('pci_device') and (device / 'device').read_text().strip() != hw['pci_device']:
                continue
            total = int((device / f'mem_info_{region}_total').read_text())
            used = int((device / f'mem_info_{region}_used').read_text())
            if total > 0 and 0 <= used <= total:
                values[str(device.resolve())] = (total - used) / MIB
        except (OSError, ValueError, KeyError):
            continue
    # Conservative if more than one matching device is present.
    return min(values.values()) if values else None


def native_gpu_free(node, cfg):
    """Observed device-wide free MiB; fail closed on unavailable telemetry.
    Hardware settings come from YAML. Per-process DRM fdinfo is not global free VRAM.
    """
    hw = cfg.get('hardware', {})
    mode = hw.get('telemetry')
    try:
        if mode in ('amd_gtt', 'intel_xpu'):
            region = 'gtt' if mode == 'amd_gtt' else 'vram'
            free = drm_memory_free(hw, region)
            if free is not None:
                return free
        if mode == 'nvidia_smi':
            cmd = ['nvidia-smi', '--query-gpu=name,memory.free', '--format=csv,noheader,nounits']
            out = subprocess.check_output(cmd, text=True, timeout=5)
            rows = [float(r.rsplit(',', 1)[1]) for r in out.splitlines() if re.search(hw['gpu_name_regex'], r.rsplit(',', 1)[0], re.I)]
            return min(rows) if rows else None
        if mode == 'intel_xpu':
            device = str(hw.get('intel_device', 0))
            # Query actual board size once; do not use marketed capacity as a measurement.
            total = cfg.get('_intel_total_mib')
            if total is None:
                out = subprocess.check_output(['xpu-smi', 'discovery', '-d', device], text=True, timeout=10, stderr=subprocess.DEVNULL)
                if not re.search(hw['device_regex'], out, re.I):
                    return None
                match = re.search(r'Memory Physical Size\s*[:|]\s*([\d.]+)\s*MiB', out, re.I)
                if not match:
                    return None
                total = cfg['_intel_total_mib'] = float(match[1])
            out = subprocess.check_output(['xpu-smi', 'dump', '-d', device, '-m', '18', '-n', '1'], text=True, timeout=10, stderr=subprocess.DEVNULL)
            used = intel_memory_used(out)
            if used is not None and 0 <= used <= total:
                return total - used
    except (OSError, KeyError, ValueError, subprocess.SubprocessError):
        pass
    return None


def backend_memory_free(output, device_regex):
    """Parse one matching accelerator's live backend-reported budget (MiB)."""
    rows = []
    for line in output.splitlines():
        hit = re.match(r'^\s*(?:Vulkan|CUDA|ROCm|HIP|SYCL)[^:]*:\s*(.*?)\s*\(([\d.]+) MiB,\s*([\d.]+) MiB free\)\s*$', line, re.I)
        if hit and re.search(device_regex, hit[1], re.I):
            total, free = float(hit[2]), float(hit[3])
            if total > 0 and 0 <= free <= total:
                rows.append(free)
            else:
                return None
    return rows[0] if len(rows) == 1 else None


def device_list(server, env):
    result = subprocess.run([str(server), '--list-devices'], env=env, text=True,
                            capture_output=True, timeout=12, check=True)
    return result.stdout + '\n' + result.stderr


def backend_probe(server, env, device_regex):
    try:
        return backend_memory_free(device_list(server, env), device_regex)
    except (OSError, ValueError, subprocess.SubprocessError):
        return None


def gpu_free(node, cfg, fallback=None):
    value = native_gpu_free(node, cfg)
    if value is not None:
        return value
    return fallback() if fallback else None


class Monitor:
    """Sample host and GPU memory, kill test process if safety floor is crossed."""
    def __init__(self, proc, node, cfg, baseline, fallback=None):
        self.proc, self.node, self.cfg = proc, node, cfg
        self.done = threading.Event()
        self.samples = []
        self.error = None
        self.baseline = baseline
        self.fallback = fallback
        self.thread = threading.Thread(target=self.loop, daemon=True)

    def loop(self):
        while not self.done.is_set():
            try:
                m = meminfo()
                m['gpu_free_mib'] = gpu_free(self.node, self.cfg, self.fallback)
                self.samples.append(m)
                if m['host_free_mib'] < self.cfg['min_host_free_gib'] * 1024:
                    self.error = 'Host memory safety floor crossed'
                elif m['swapout_pages'] > self.baseline['swapout_pages']:
                    self.error = 'System swap-out observed'
                elif m['gpu_free_mib'] is None:
                    self.error = 'GPU memory telemetry lost'
                elif m['gpu_free_mib'] < self.cfg['min_gpu_free_mib']:
                    self.error = 'GPU/GTT memory safety floor crossed'
            except (OSError, KeyError, ValueError) as e:
                self.error = 'Memory telemetry failed: ' + str(e)
            if self.error:
                with contextlib.suppress(ProcessLookupError):
                    self.proc.terminate()
                return
            self.done.wait(5 if self.fallback else 2)

    def summary(self):
        gpu = [s['gpu_free_mib'] for s in self.samples if s['gpu_free_mib'] is not None]
        return {'samples': len(self.samples), 'gpu_samples': len(gpu),
                'min_host_free_mib': min((s['host_free_mib'] for s in self.samples), default=None),
                'min_gpu_free_mib': min(gpu, default=None),
                'swapout_pages': max((s['swapout_pages'] - self.baseline['swapout_pages'] for s in self.samples), default=0),
                'error': self.error}


def prompt_messages(count, fraction, seed):
    """Deterministic varied synthetic records, with one exact answer at chosen depth."""
    rng = random.Random(seed)
    code = 'K' + hashlib.sha256(str(seed).encode()).hexdigest()[:14].upper()
    lines = [f'Record {i:06d}: component {rng.randrange(100000, 999999)}; status {rng.choice(["stable", "pending", "verified", "review"])}; ticket {rng.randrange(100000,999999)}.' for i in range(count)]
    lines.insert(int(count * fraction), 'SPECIAL AUDIT RECORD: verification code = ' + code)
    text = ('Read these synthetic inventory records. Find the SPECIAL AUDIT RECORD.\n' + '\n'.join(lines) +
            '\nReturn its verification code alone on the first line, then explain in about 100 words how you found it. Do not guess.')
    return [{'role': 'user', 'content': text}], code


def output_budget(ctx):
    # Reserve reasoning/output capacity without filling the whole window.
    return min(4096, max(512, ctx // 4 - 128))


def prompt_target(ctx, budget):
    return min(int(ctx * .90), ctx - budget - 128)


def sized_prompt(base, key, ctx, fraction, seed, budget=None, test_request=None):
    budget = output_budget(ctx) if budget is None else budget
    target = prompt_target(ctx, budget)
    count = max(10, target // 25)
    for _ in range(5):
        messages, code = prompt_messages(count, fraction, seed)
        prompt = api(base + '/apply-template', key, {'messages': messages, **(test_request or {})}, 60)['prompt']
        n = len(api(base + '/tokenize', key, {'content': prompt, 'add_special': True, 'parse_special': True}, 60)['tokens'])
        if target * .97 <= n <= target * 1.02 and n + budget < ctx:
            return messages, code, n
        count = max(1, int(count * target / max(n, 1)))
    fail('Could not create a correctly sized prompt.')


def stream_chat(base, key, alias, messages, timeout, budget=4096, test_request=None):
    req = urllib.request.Request(base + '/v1/chat/completions', headers={'Authorization': 'Bearer ' + key, 'Content-Type': 'application/json'}, data=json.dumps({'model': alias, 'messages': messages, 'max_tokens': budget, 'temperature': 0, 'stream': True, 'stream_options': {'include_usage': True}, 'cache_prompt': False, **(test_request or {})}).encode())
    start = time.monotonic()
    first = None
    content, timings, usage = [], {}, {}
    finished = False
    with urllib.request.urlopen(req, timeout=timeout) as res:
        for line in res:
            if time.monotonic() - start > timeout:
                fail('Request exceeded calibration timeout')
            if not line.startswith(b'data:'):
                continue
            raw = line[5:].strip()
            if raw == b'[DONE]':
                finished = True
                break
            d = json.loads(raw)
            if d.get('error'):
                fail(str(d['error']))
            timings = d.get('timings') or timings
            usage = d.get('usage') or usage
            for c in d.get('choices', []):
                delta = c.get('delta', {})
                if delta.get('content'):
                    if first is None:
                        first = time.monotonic() - start
                    content.append(delta.get('content') or '')
    if not finished or first is None or not timings or not usage:
        fail('Incomplete stream, usage or timings; cannot certify result')
    return ''.join(content), timings, usage, first


def trial_pass(trial, ctx, cfg):
    target = trial.get('target_tokens', int(ctx * .90))
    return (trial['correct'] and .95 * target <= trial['prompt_tokens'] <= min(.95 * ctx, 1.05 * target)
            and trial['prompt_tokens'] + trial['completion_tokens'] <= ctx
            and trial['processed_tokens'] >= .95 * trial['prompt_tokens']
            and trial['completion_tokens'] >= 16 and trial['generation_tps'] >= cfg['min_generation_tps']
            and trial['ttft_seconds'] <= cfg['max_ttft_seconds'])


def candidate(cluster, alias, spec, ctx, cache_k, cache_v, flash, cfg, folder, records, reportdir):
    label = f'{ctx}-{cache_k}-{cache_v}-{flash}'
    log = reportdir / (label + '.log')
    native_log = reportdir / (label + '-native.log')
    result = {'context': ctx, 'cache_type_k': cache_k, 'cache_type_v': cache_v, 'flash_attention': flash,
              'passed': False, 'trials': [], 'log': log.name, 'native_log': native_log.name, 'parallel': slot_count(spec)}
    # Reserve an ephemeral loopback port; API key protects the brief test server.
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0)); port = s.getsockname()[1]
    key = 'sk-' + secrets.token_urlsafe(32)
    server = cluster.locate(cluster.runtime, 'llama-server')
    tuned = dict(spec, context=ctx, cache_type_k=cache_k, cache_type_v=cache_v, flash_attention=flash)
    cmd = model_command(server, folder, records, alias, tuned, port) + ['--log-file', str(native_log)]
    # Pass key via environment, not report or process arguments.
    env = dict(cluster.runtime_env(), LLAMA_API_KEY=key)
    base = f'http://127.0.0.1:{port}'
    say(f'CALIBRATE {alias} {ctx} {cache_k}/{cache_v}')
    baseline = meminfo()
    # On Intel Xe and AMD UMA, vendor sysfs/counters may not expose free memory.
    # Query the selected engine for its live budget, never cache a startup value.
    fallback = None
    if cfg.get('hardware', {}).get('telemetry') in ('intel_xpu', 'amd_gtt'):
        fallback = lambda: backend_probe(server, cluster.runtime_env(), cfg['hardware']['device_regex'])
    free = gpu_free(cluster.a.node, cfg)
    source = cfg.get('hardware', {}).get('telemetry')
    if free is None and fallback:
        free = fallback()
        source = 'backend_memory_budget' if free is not None else source
    result['baseline'] = dict(baseline, gpu_free_mib=free, telemetry=source)
    say(f'BASELINE: host available={baseline["host_free_mib"]:.0f} MiB; GPU/GTT free={free} MiB; telemetry={result["baseline"]["telemetry"]}')
    if free is None or free < cfg['min_gpu_free_mib'] or baseline['host_free_mib'] < cfg['min_host_free_gib'] * 1024:
        result['error'] = ('GPU telemetry unavailable' if free is None else 'Insufficient GPU/GTT headroom' if free < cfg['min_gpu_free_mib'] else 'Insufficient host memory headroom') + '; no load attempted'
        if free is None and cfg.get('hardware', {}).get('telemetry') == 'amd_gtt':
            result['telemetry_paths'] = [str(p) for p in Path('/sys/class/drm').glob('card*/device/mem_info_gtt_*')]
            say('AMD GTT paths: ' + str(result['telemetry_paths']))
        say('FAIL: ' + result['error'])
        return result
    with log.open('w') as f:
        proc = subprocess.Popen(cmd, env=env, stdout=f, stderr=subprocess.STDOUT)
        mon = Monitor(proc, cluster.a.node, cfg, baseline, fallback)
        mon.thread.start()
        try:
            started = time.monotonic(); next_update = started + 15
            deadline = started + cfg['request_timeout_seconds']
            while time.monotonic() < deadline:
                now = time.monotonic()
                if now >= next_update:
                    say(f'  Loading model: {now-started:.0f}s')
                    next_update = now + 15
                if proc.poll() is not None or mon.error:
                    fail(mon.error or 'Server exited during model load')
                try:
                    data = api(base + '/v1/models', key, timeout=3)
                    if data.get('data'):
                        break
                except (OSError, ValueError):
                    pass
                time.sleep(2)
            else:
                fail('Model load timeout')
            native = int(data['data'][0]['meta']['n_ctx_train'])
            result['native_context'] = native
            if ctx > native:
                fail('Candidate exceeds model native context')
            props = api(base + '/props', key, timeout=10)
            slots = api(base + '/slots', key, timeout=10)
            parallel = slot_count(spec)
            actual_contexts = [int(slot['n_ctx']) for slot in slots]
            result['actual_contexts'] = actual_contexts
            if len(slots) != parallel or any(n != ctx for n in actual_contexts):
                fail(f'Actual slot contexts {actual_contexts} differ from requested {parallel} × {ctx}')
            # Native logging is asynchronous; wait briefly for startup messages to flush.
            for _ in range(10):
                logtext = (native_log.read_text(errors='replace') if native_log.is_file() else '') + '\n' + log.read_text(errors='replace')
                counts = offload_counts(logtext)
                if counts:
                    break
                if proc.poll() is not None or mon.error:
                    break
                time.sleep(.5)
            if not counts:
                fail('No GPU offload evidence in ' + str(log))
            off, total = counts; result['gpu_layers'] = [off, total]
            if not 0 < off <= total or off / total < float(spec.get('min_gpu_fraction', 1.0)):
                fail('GPU offload below profile threshold')
            # Verify the cache precision was actually used (not silently overridden).
            cache_hits = re.findall(r'\b([KV])\s*\(([^)]+)\)', logtext)
            result['cache_log_types'] = cache_hits
            if ('K', cache_k) not in cache_hits or ('V', cache_v) not in cache_hits:
                fail('Requested KV cache precision not confirmed by runtime log')
            budget = output_budget(ctx)
            test_request = spec.get('test_request', {})
            result['output_budget'] = budget
            result['target_tokens'] = prompt_target(ctx, budget)
            result['test_request'] = test_request
            # Fill every configured slot concurrently, with independent needles.
            # Monitor /slots so sequential queueing cannot masquerade as parallel success.
            for i in range(int(cfg['trials'])):
                if mon.error:
                    fail(mon.error)
                say(f'Trial {i+1}/{cfg["trials"]}: preparing {parallel} long prompt(s) at context {ctx}')
                batch = []
                for j in range(parallel):
                    fraction = (.1, .5, .9)[(i + j) % 3]
                    messages, code, expected_n = sized_prompt(base, key, ctx, fraction, 1024 + i * parallel + j + ctx, budget, test_request)
                    batch.append((fraction, messages, code, expected_n))
                def execute(item):
                    fraction, messages, code, expected_n = item
                    content, timing, usage, ttft = stream_chat(base, key, alias, messages, cfg['request_timeout_seconds'], budget, test_request)
                    trial = {'position': fraction, 'correct': content.strip().split('\n', 1)[0].strip() == code,
                             'tokenizer_count': expected_n, 'target_tokens': prompt_target(ctx, budget), 'prompt_tokens': int(usage['prompt_tokens']),
                             'completion_tokens': int(usage['completion_tokens']), 'processed_tokens': int(timing['prompt_n']),
                             'prompt_tps': float(timing['prompt_per_second']), 'generation_tps': float(timing['predicted_per_second']),
                             'ttft_seconds': round(ttft, 3)}
                    trial['passed'] = trial_pass(trial, ctx, cfg)
                    return trial
                with concurrent.futures.ThreadPoolExecutor(max_workers=parallel) as pool:
                    futures = [pool.submit(execute, item) for item in batch]
                    peak = 0
                    started = time.monotonic(); next_update = started + 15
                    while not all(f.done() for f in futures):
                        now = time.monotonic()
                        if now >= next_update:
                            say(f'  Inference running: {now-started:.0f}s; {sum(f.done() for f in futures)}/{parallel} request(s) complete')
                            next_update = now + 15
                        if parallel > 1:
                            try:
                                states = api(base + '/slots', key, timeout=3)
                                peak = max(peak, sum(bool(slot['is_processing']) for slot in states))
                            except (OSError, ValueError, KeyError):
                                pass
                        time.sleep(.2)
                    for f in futures:
                        trial = f.result()
                        result['trials'].append(trial)
                        say(str(trial))
                    if parallel > 1 and peak < parallel:
                        fail(f'Observed only {peak}/{parallel} concurrent slots; no parallel recommendation')
            if not all(t['passed'] for t in result['trials']):
                fail('Retrieval, context fill, latency or throughput threshold not met')
        except urllib.error.HTTPError as e:
            result['error'] = f'HTTP {e.code}: {http_error_detail(e, key)}'
        except (RuntimeError, OSError, ValueError, KeyError, IndexError, TypeError) as e:
            result['error'] = str(e)
        finally:
            mon.done.set(); mon.thread.join(timeout=25)
            result['memory'] = mon.summary()
            if proc.poll() is None:
                proc.terminate()
                try:
                    proc.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    proc.kill(); proc.wait()
        if not result.get('error'):
            memory = result['memory']
            if memory['error'] or memory['samples'] < 2 or memory['gpu_samples'] != memory['samples']:
                result['error'] = memory['error'] or 'Incomplete GPU/host telemetry; no automatic recommendation'
            elif memory['min_host_free_mib'] < cfg['min_host_free_gib'] * 1024 or memory['min_gpu_free_mib'] < cfg['min_gpu_free_mib'] or memory['swapout_pages'] > 0:
                result['error'] = 'Memory safety threshold not met'
            else:
                result['passed'] = True
    if not result['passed']:
        # Keep the full per-candidate log on disk and surface a short bounded tail.
        if log.exists():
            text = (native_log.read_text(errors='replace') if native_log.is_file() else '') + '\n' + log.read_text(errors='replace')
            lines = text.replace(key, '[REDACTED]').splitlines()
            result['log_tail'] = '\n'.join(lines[-20:])[-5000:]
            say('Model log: ' + str(log))
            if result['log_tail']:
                say(result['log_tail'])
    say('PASS' if result['passed'] else 'FAIL: ' + result.get('error', 'unknown'))
    return result


def choose(results, target=None):
    """Prefer highest precision meeting target; otherwise largest passing context."""
    precision = {'f16': 3, 'q8_0': 2, 'q4_0': 1}
    good = [r for r in results if r['passed']]
    if not good:
        return None
    reached = [r for r in good if target is not None and r['context'] >= target]
    if reached:
        r = max(reached, key=lambda x: (min(precision.get(x['cache_type_k'], 0), precision.get(x['cache_type_v'], 0)), -x['context']))
    else:
        r = max(good, key=lambda x: (x['context'], min(precision.get(x['cache_type_k'], 0), precision.get(x['cache_type_v'], 0))))
    return {k: r[k] for k in ('context', 'cache_type_k', 'cache_type_v', 'flash_attention')}


def update_yaml(path, recommendations, original):
    """Replace existing scalar fields only, keeping comments and layout intact."""
    text = path.read_text()
    if text != original:
        fail('Profile changed during calibration; review report and apply manually')
    root = yaml.compose(text)
    def member(node, name):
        return next((v for k, v in node.value if k.value == name), None)
    models = member(root, 'models')
    edits = []
    for alias, rec in recommendations.items():
        model = member(models, alias)
        for key, val in rec.items():
            scalar = member(model, key)
            if not isinstance(scalar, yaml.ScalarNode):
                fail('Missing editable YAML scalar: ' + alias + '/' + key)
            edits.append((scalar.start_mark.index, scalar.end_mark.index, json.dumps(val)))
    for start, end, value in sorted(edits, reverse=True):
        text = text[:start] + value + text[end:]
    actual = yaml.safe_load(text)['models']
    if any(actual[a][k] != v for a, rec in recommendations.items() for k, v in rec.items()):
        fail('Resulting YAML does not match recommendation')
    atomic(path, text, mode=0o644)


def calibrate_cluster(c, migration=False):
    if c.role != 'worker':
        fail('Calibration requires a managed llama.cpp worker; Mac and edge vendor runtimes are not supported')
    c.prepare(); c.service_owned()
    cfg = dict(c.c['calibration'], hardware=c.c['hardware'])
    if not 3 <= int(cfg['trials']) <= 9:
        fail('Calibration trials must be 3–9')
    if cfg['min_host_free_gib'] < 2 or cfg['min_gpu_free_mib'] < 256:
        fail('Unsafe memory floors')
    selected = [c.a.model] if c.a.model and not migration else list(c.desired)
    if any(a not in c.desired for a in selected):
        fail('Unknown or disabled model')
    if not migration:
        confirm('Calibration temporarily stops inference and can take hours. Continue?', c.a.yes)
    profile = REPO / 'config' / (c.a.node + '.yaml')
    original = profile.read_text()
    recommendations = {}
    if not migration:
        c.install_worker()
        for alias in selected:
            c.sync_model(alias, c.desired[alias])
    with c.isolated_gpu():
        for alias in selected:
            spec = c.desired[alias]
            folder = c.models / model_id(spec)
            if not owned(folder) or not (folder / 'weights.json').exists():
                fail('Run setup/--models first to stage verified weights: ' + alias)
            records = json.loads((folder / 'weights.json').read_text())
            reportdir = c.root / 'benchmarks' / f'{alias.replace("/", "--")}-{c.backend}-{time.strftime("%Y%m%d-%H%M%S")}-{secrets.token_hex(2)}'
            reportdir.mkdir(parents=True)
            report = {'model': alias, 'backend': c.backend, 'engine': c.v['llama_cpp'], 'kernel': platform.release(), 'device_list': device_list(c.locate(c.runtime, 'llama-server'), c.runtime_env()),
                      'weights': records, 'runtime_args': spec.get('args', []), 'settings': cfg, 'runtime': str(c.runtime), 'profile_sha256': hashlib.sha256(original.encode()).hexdigest(), 'results': []}
            results = report['results']
            def test(ctx, k, v, flash):
                r = candidate(c, alias, spec, ctx, k, v, flash, cfg, folder, records, reportdir)
                results.append(r)
                atomic(reportdir / 'report.json', json.dumps(report, indent=2))
                return r
            if migration:
                r = test(int(spec['context']), spec.get('cache_type_k', 'f16'), spec.get('cache_type_v', 'f16'), flash_mode(spec.get('flash_attention', 'auto')))
                if not r['passed']:
                    fail('Migration context check failed; old assets retained. Report: ' + str(reportdir))
            else:
                native = None
                goal = int(spec.get('target_context', spec['context']))
                contexts = sorted(x for x in set(map(int, cfg['contexts'])) | {int(spec['context']), goal} if x <= goal)
                types = [c.a.cache_type] if c.a.cache_type else cfg['cache_types']
                # Stop at target. Try precision in descending order, then fall back.
                for cache in types:
                    if not c.a.cache_type and any(r['passed'] and r['context'] >= goal for r in results):
                        break
                    for ctx in contexts:
                        if ctx < 4096 or (native is not None and ctx > native):
                            continue
                        r = test(ctx, cache, cache, 'auto' if cache == 'f16' else 'on')
                        native = r.get('native_context', native)
                        if not r['passed']:
                            # Do not stress larger contexts after memory, load or quality failure.
                            break
                rec = choose(results, goal)
                if rec:
                    recommendations[alias] = rec
                    report['recommendation'] = rec
                    atomic(reportdir / 'recommendation.yaml', yaml.safe_dump({alias: rec}, sort_keys=False))
                else:
                    report['recommendation'] = None
            atomic(reportdir / 'report.json', json.dumps(report, indent=2))
            say('Report: ' + str(reportdir))
        if not migration:
            if len(recommendations) != len(selected):
                fail('One or more models have no passing candidate; inspect reports. No YAML changes.')
            say(yaml.safe_dump(recommendations, sort_keys=False))
            if c.a.apply:
                if c.backend != c.c['backend']:
                    fail('Backend override differs from YAML. Review report and set backend manually before applying.')
                confirm('Update tested context/cache fields in repository YAML? Deploy separately.', c.a.yes)
                update_yaml(profile, recommendations, original)
                say('YAML updated; run setup to deploy, then commit if satisfied.')
