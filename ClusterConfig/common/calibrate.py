#!/usr/bin/env python3
"""Isolated llama.cpp context calibration, invoked by setup-<node>.sh --calibrate.
Edit calibration/model settings in the node YAML. Tests real chat prompts at
~90% context, measures streaming latency, retrieval, memory and GPU offload.
Stages missing runtime/weights, pauses known GPU services and restores them.
Does not prune models or permanently change service configuration.
--apply edits only tested context/cache fields; run setup again to deploy them.
Reports and per-candidate logs remain in ~/inference/benchmarks. No daemon.
"""
import contextlib
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
import urllib.request
from pathlib import Path

import yaml
from manage import api, atomic, confirm, fail, model_command, model_id, owned, say, REPO

MIB = 2**20


def meminfo():
    fields = dict(re.findall(r'^(\w+):\s+(\d+) kB', Path('/proc/meminfo').read_text(), re.M))
    vm = dict(line.split() for line in Path('/proc/vmstat').read_text().splitlines())
    return {'host_free_mib': int(fields['MemAvailable']) / 1024,
            'swap_used_mib': (int(fields['SwapTotal']) - int(fields['SwapFree'])) / 1024,
            'swapout_pages': int(vm['pswpout'])}


def gpu_free(node, cfg):
    """Return actual observed free MiB; never substitute advertised capacity."""
    try:
        if node == 'gmini':
            for card in Path('/sys/class/drm').glob('card*/device'):
                if (card / 'vendor').read_text().strip() == '0x1002':
                    return (int((card / 'mem_info_gtt_total').read_text()) - int((card / 'mem_info_gtt_used').read_text())) / MIB
        if node == 'z820':
            cmd = ['nvidia-smi', '--query-gpu=name,memory.free', '--format=csv,noheader,nounits']
            out = subprocess.check_output(cmd, text=True, timeout=5)
            rows = [float(r.rsplit(',', 1)[1]) for r in out.splitlines() if 'A4500' in r]
            return min(rows) if rows else None
        if node == 'p520':
            device = str(cfg.get('intel_device', 0))
            cmd = ['xpu-smi', '--query-gpu=index,memory.free', '--format=csv,noheader,nounits']
            out = subprocess.check_output(cmd, text=True, timeout=5, stderr=subprocess.DEVNULL)
            for line in out.splitlines():
                fields = [f.strip() for f in line.split(',')]
                if len(fields) == 2 and fields[0] == device:
                    return float(fields[1])
    except (OSError, ValueError, subprocess.SubprocessError):
        pass
    return None


class Monitor:
    """Sample host and GPU memory, kill test process if safety floor is crossed."""
    def __init__(self, proc, node, cfg, baseline):
        self.proc, self.node, self.cfg = proc, node, cfg
        self.done = threading.Event()
        self.samples = []
        self.error = None
        self.baseline = baseline
        self.thread = threading.Thread(target=self.loop, daemon=True)

    def loop(self):
        while not self.done.is_set():
            try:
                m = meminfo()
                m['gpu_free_mib'] = gpu_free(self.node, self.cfg)
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
            self.done.wait(2)

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


def sized_prompt(base, key, ctx, fraction, seed):
    target = int(ctx * .90)
    count = max(10, target // 25)
    for _ in range(5):
        messages, code = prompt_messages(count, fraction, seed)
        prompt = api(base + '/apply-template', key, {'messages': messages}, 60)['prompt']
        n = len(api(base + '/tokenize', key, {'content': prompt, 'add_special': True, 'parse_special': True}, 60)['tokens'])
        if target * .97 <= n <= target * 1.02 and n + 512 < ctx:
            return messages, code, n
        count = max(1, int(count * target / max(n, 1)))
    fail('Could not create a correctly sized prompt.')


def stream_chat(base, key, alias, messages, timeout):
    req = urllib.request.Request(base + '/v1/chat/completions', headers={'Authorization': 'Bearer ' + key, 'Content-Type': 'application/json'}, data=json.dumps({'model': alias, 'messages': messages, 'max_tokens': 512, 'temperature': 0, 'stream': True, 'stream_options': {'include_usage': True}, 'cache_prompt': False}).encode())
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
                if delta.get('content') or delta.get('reasoning_content'):
                    if first is None:
                        first = time.monotonic() - start
                    content.append(delta.get('content') or '')
    if not finished or first is None or not timings or not usage:
        fail('Incomplete stream, usage or timings; cannot certify result')
    return ''.join(content), timings, usage, first


def trial_pass(trial, ctx, cfg):
    return (trial['correct'] and .85 * ctx <= trial['prompt_tokens'] <= .95 * ctx
            and trial['prompt_tokens'] + trial['completion_tokens'] <= ctx
            and trial['processed_tokens'] >= .95 * trial['prompt_tokens']
            and trial['completion_tokens'] >= 16 and trial['generation_tps'] >= cfg['min_generation_tps']
            and trial['ttft_seconds'] <= cfg['max_ttft_seconds'])


def candidate(cluster, alias, spec, ctx, cache_k, cache_v, flash, cfg, folder, records, reportdir):
    label = f'{ctx}-{cache_k}-{cache_v}-{flash}'
    log = reportdir / (label + '.log')
    result = {'context': ctx, 'cache_type_k': cache_k, 'cache_type_v': cache_v, 'flash_attention': flash,
              'passed': False, 'trials': [], 'log': log.name}
    # Reserve an ephemeral loopback port; API key protects the brief test server.
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0)); port = s.getsockname()[1]
    key = 'sk-' + secrets.token_urlsafe(32)
    server = cluster.locate(cluster.runtime, 'llama-server')
    tuned = dict(spec, context=ctx, cache_type_k=cache_k, cache_type_v=cache_v, flash_attention=flash)
    cmd = model_command(server, folder, records, alias, tuned, port)
    # Pass key via environment, not report or process arguments.
    env = dict(cluster.runtime_env(), LLAMA_API_KEY=key)
    base = f'http://127.0.0.1:{port}'
    say(f'CALIBRATE {alias} {ctx} {cache_k}/{cache_v}')
    baseline = meminfo()
    free = gpu_free(cluster.a.node, cfg)
    if free is None or free < cfg['min_gpu_free_mib'] or baseline['host_free_mib'] < cfg['min_host_free_gib'] * 1024:
        result['error'] = 'Missing GPU telemetry or insufficient baseline memory headroom; no load attempted'
        say('FAIL: ' + result['error'])
        return result
    with log.open('w') as f:
        proc = subprocess.Popen(cmd, env=env, stdout=f, stderr=subprocess.STDOUT)
        mon = Monitor(proc, cluster.a.node, cfg, baseline)
        mon.thread.start()
        try:
            deadline = time.monotonic() + cfg['request_timeout_seconds']
            while time.monotonic() < deadline:
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
            actual_ctx = int(props['default_generation_settings']['n_ctx'])
            result['actual_context'] = actual_ctx
            if actual_ctx != ctx:
                fail(f'Actual context {actual_ctx} differs from requested {ctx}')
            logtext = log.read_text(errors='replace')
            hits = re.findall(r'offloaded\s+(\d+)\s*/\s*(\d+)\s+layers\s+to\s+GPU', logtext, re.I)
            if not hits:
                fail('No GPU offload evidence')
            off, total = map(int, hits[-1]); result['gpu_layers'] = [off, total]
            if not total or off / total < float(spec.get('min_gpu_fraction', 1.0)):
                fail('GPU offload below profile threshold')
            # Verify the cache precision was actually used (not silently overridden).
            cache_hits = re.findall(r'\b([KV])\s*\(([^)]+)\)', logtext)
            result['cache_log_types'] = cache_hits
            if ('K', cache_k) not in cache_hits or ('V', cache_v) not in cache_hits:
                fail('Requested KV cache precision not confirmed by runtime log')
            for i in range(int(cfg['trials'])):
                if mon.error:
                    fail(mon.error)
                fraction = (.1, .5, .9)[i % 3]
                messages, code, expected_n = sized_prompt(base, key, ctx, fraction, 1024 + i + ctx)
                content, timing, usage, ttft = stream_chat(base, key, alias, messages, cfg['request_timeout_seconds'])
                trial = {'position': fraction, 'correct': content.strip().split('\n', 1)[0].strip() == code,
                         'tokenizer_count': expected_n, 'prompt_tokens': int(usage['prompt_tokens']),
                         'completion_tokens': int(usage['completion_tokens']), 'processed_tokens': int(timing['prompt_n']),
                         'prompt_tps': float(timing['prompt_per_second']), 'generation_tps': float(timing['predicted_per_second']),
                         'ttft_seconds': round(ttft, 3)}
                trial['passed'] = trial_pass(trial, ctx, cfg)
                result['trials'].append(trial)
                say(str(trial))
            if not all(t['passed'] for t in result['trials']):
                fail('Retrieval, context fill, latency or throughput threshold not met')
        except (RuntimeError, OSError, ValueError, KeyError, IndexError, TypeError) as e:
            result['error'] = str(e)
        finally:
            mon.done.set(); mon.thread.join(timeout=7)
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
    say('PASS' if result['passed'] else 'FAIL: ' + result.get('error', 'unknown'))
    return result


def choose(results):
    """Largest passing context; prefer higher precision at equal capacity."""
    precision = {'f16': 3, 'q8_0': 2, 'q4_0': 1}
    good = [r for r in results if r['passed']]
    if not good:
        return None
    r = max(good, key=lambda x: (x['context'], precision.get(x['cache_type_k'], 0)))
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
    cfg = c.c['calibration']
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
            report = {'model': alias, 'backend': c.backend, 'engine': c.v['llama_cpp'], 'kernel': platform.release(), 'device_list': subprocess.run([str(c.locate(c.runtime, 'llama-server')), '--list-devices'], env=c.runtime_env(), text=True, capture_output=True, timeout=30).stdout,
                      'weights': records, 'runtime_args': spec.get('args', []), 'settings': cfg, 'runtime': str(c.runtime), 'profile_sha256': hashlib.sha256(original.encode()).hexdigest(), 'results': []}
            results = report['results']
            def test(ctx, k, v, flash):
                r = candidate(c, alias, spec, ctx, k, v, flash, cfg, folder, records, reportdir)
                results.append(r)
                atomic(reportdir / 'report.json', json.dumps(report, indent=2))
                return r
            if migration:
                r = test(int(spec['context']), spec.get('cache_type_k', 'f16'), spec.get('cache_type_v', 'f16'), spec.get('flash_attention', 'auto'))
                if not r['passed']:
                    fail('Migration context check failed; old assets retained. Report: ' + str(reportdir))
            else:
                native = None
                goal = int(spec.get('target_context', spec['context']))
                contexts = sorted(set(map(int, cfg['contexts'])) | {int(spec['context']), goal})
                types = [c.a.cache_type] if c.a.cache_type else cfg['cache_types']
                # Test Q8 first; F16 for comparison or fallback, Q4 only if goal not met.
                for cache in types:
                    if cache == 'q4_0' and not c.a.cache_type and any(r['passed'] and r['context'] >= goal for r in results):
                        continue
                    for ctx in contexts:
                        if ctx < 4096 or (native is not None and ctx > native):
                            continue
                        r = test(ctx, cache, cache, 'auto' if cache == 'f16' else 'on')
                        native = r.get('native_context', native)
                        if not r['passed']:
                            # Do not stress larger contexts after memory, load or quality failure.
                            break
                rec = choose(results)
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
