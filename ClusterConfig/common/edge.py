#!/usr/bin/env python3
"""Manage edge inference with native Ollama or GenieX.
Edit the node YAML. Seed deletes unconfigured model tags via native CLI, then
validates inference. Vendor drivers/runtime remain. No legacy rollback.
Nano tests isolate runner sessions and wait for native unload. Reports are local.
"""
import json
import os
import platform
import re
import shutil
import subprocess
import time
import threading
import contextlib
import urllib.parse
from pathlib import Path

from manage import api, atomic, confirm, fail, run, say, valid_alias


def context_checks(result, code, ctx, cfg, before, after):
    n = result.get('prompt_tokens') or 0
    checks = {
        'prompt_fill': ctx*.70 <= n <= ctx*.85,
        'retrieval': code in result.get('text', ''),
        'uncached_prompt': (result.get('cached_prompt_tokens') or 0) <= n*.05,
        'allocated_context': (result.get('context_allocated') or 0) >= ctx,
        'generation_speed': result.get('generation_tps', 0) >= cfg['min_generation_tps'],
        'host_memory': after['host_free_mib'] >= cfg['min_host_free_mib'],
        'no_swapout': after['swapout_pages'] == before['swapout_pages'],
        'request_time': result['seconds'] <= cfg['max_request_seconds'],
    }
    return [name for name, ok in checks.items() if not ok]


@contextlib.contextmanager
def observe_memory(meminfo, interval=2):
    """Sample unified host RAM throughout an edge request; no vendor daemon changes."""
    samples = [meminfo()]
    errors = []
    stop = threading.Event()
    def sample():
        started=time.monotonic(); next_update=started+15
        while not stop.wait(interval):
            try:
                samples.append(meminfo())
                now=time.monotonic()
                if now>=next_update:
                    say(f'  Inference running: {now-started:.0f}s; host available={samples[-1]["host_free_mib"]:.0f} MiB')
                    next_update=now+15
            except (OSError, ValueError, KeyError) as exc:
                errors.append(str(exc)); return
    worker = threading.Thread(target=sample, daemon=True)
    worker.start()
    try:
        yield samples, errors
    finally:
        stop.set(); worker.join(timeout=5)
        samples.append(meminfo())


class Edge:
    def __init__(self, c):
        self.c, self.a = c, c.a
        self.spec = c.c
        self.runtime = self.spec['runtime']
        if self.runtime not in ('ollama', 'geniex'):
            fail('Unknown edge runtime.')
        self.models = {a: m for a, m in self.spec['edge_models'].items() if m.get('enabled', True)}
        if not self.models or any(not valid_alias(a, c.a.node) for a in self.models):
            fail('Configure at least one valid server-qualified edge model.')
        self.endpoint = c.endpoint.rstrip('/')
        url = urllib.parse.urlsplit(self.endpoint)
        if url.scheme != 'http' or url.hostname not in ('localhost', '127.0.0.1', '::1') or url.path or not url.port:
            fail('Edge management requires a local HTTP endpoint.')
        self.port = url.port
        self.state = c.cfg / 'edge-models.json'
        self.unit = 'local-ai-geniex.service'
        self.unit_path = Path.home() / '.config/systemd/user' / self.unit
        self.dropin = Path('/etc/systemd/system/ollama.service.d/90-local-ai-cluster.conf')
        self.env = dict(os.environ, OLLAMA_HOST=self.endpoint)

    def selected(self):
        if not self.a.model:
            return self.models.items()
        chosen = [(a, m) for a, m in self.models.items() if self.a.model in (a, m['id'])]
        if not chosen:
            fail('Unknown configured edge model: ' + self.a.model)
        return chosen

    def cli(self):
        path = shutil.which(self.runtime)
        if not path:
            for folder in (Path.home()/'.local/bin', Path.home()/'.geniex/bin', Path.home()/'geniex/bin'):
                if (folder/self.runtime).is_file():
                    path = str(folder/self.runtime); break
        if not path:
            fail(f'{self.runtime} executable not found; run ordinary setup to install it.')
        return path

    def ids(self):
        data = api(self.endpoint + ('/api/tags' if self.runtime == 'ollama' else '/v1/models'))
        if self.runtime == 'ollama':
            return {m['name'] for m in data['models']}
        return {m['id'] for m in data['data']}

    def reachable(self):
        try:
            self.ids(); return True
        except (OSError, ValueError, KeyError):
            return False

    def wait(self):
        for _ in range(30):
            if self.reachable(): return
            time.sleep(2)
        fail(f'{self.runtime} API did not start at {self.endpoint}. Inspect service logs.')

    def install_missing(self):
        try:
            self.cli(); return
        except RuntimeError:
            if self.reachable():
                say('Existing runtime API available; not installing a duplicate CLI/server.')
                return
        self.c.cache.mkdir(parents=True, exist_ok=True)
        script = self.c.cache / (self.runtime + '-install.sh')
        if self.runtime == 'ollama':
            url = 'https://ollama.com/install.sh'
            run('curl', '-fL', '--retry', '3', '-o', script, url)
            run('sh', script, env=dict(os.environ, OLLAMA_VERSION=self.c.v['ollama']))
        else:
            # Official Arduino recipe, on a supported vendor OS. No GPU driver replacement.
            url = 'https://qaihub-public-assets.s3.us-west-2.amazonaws.com/qai-hub-geniex/install.sh'
            run('curl', '-fL', '--retry', '3', '-o', script, url)
            run('sh', script, '--version', self.c.v['geniex'])
        self.cli()

    def configure_ollama(self):
        # Existing systemd service is adopted; preserve its model store and other drop-ins.
        if run('systemctl', 'show', '-p', 'LoadState', '--value', 'ollama.service', capture=True, check=False).stdout.strip() != 'loaded':
            if self.reachable():
                say('External Ollama already reachable; preserve its service configuration.'); return
            fail('Ollama systemd unit is missing after installation.')
        bind = self.spec.get('bind', 'localhost')
        if not re.fullmatch(r'[A-Za-z0-9.:-]+', bind): fail('Invalid Ollama bind.')
        if ':' in bind and not bind.startswith('['): bind = '[' + bind + ']'
        ctx = int(self.spec.get('context', 4096))
        if ctx < 512: fail('Invalid edge context.')
        text = ('# local-ai-cluster\n[Service]\n'
                f'Environment="OLLAMA_HOST={bind}:{self.port}"\n'
                f'Environment="OLLAMA_CONTEXT_LENGTH={ctx}"\n'
                'Environment="OLLAMA_NUM_PARALLEL=1"\nEnvironment="OLLAMA_MAX_LOADED_MODELS=1"\n')
        if self.dropin.exists() and 'local-ai-cluster' not in self.dropin.read_text():
            fail('Refusing to overwrite an unowned Ollama drop-in.')
        if not self.dropin.exists() or self.dropin.read_text() != text:
            temp = self.c.cfg / 'ollama-service.conf'
            atomic(temp, text)
            run('sudo', 'install', '-D', '-m', '0644', temp, self.dropin)
            run('sudo', 'systemctl', 'daemon-reload')
            run('sudo', 'systemctl', 'restart', 'ollama.service')
        run('sudo', 'systemctl', 'enable', '--now', 'ollama.service')

    def configure_geniex(self):
        # Do not compete with a vendor/user service already listening on this endpoint.
        if self.reachable():
            say('Reusing running GenieX service. Existing vendor cache retained.'); return
        if self.unit_path.exists() and 'local-ai-cluster' not in self.unit_path.read_text():
            fail('Refusing to overwrite an unowned GenieX unit.')
        binary = self.cli()
        bind = self.spec.get('bind', 'localhost')
        if not re.fullmatch(r'[A-Za-z0-9.-]+', bind) or re.search(r'[\n\r%"\\]', binary):
            fail('Invalid GenieX service path/bind.')
        data = ('# local-ai-cluster\n[Unit]\nDescription=Local AI GenieX\nAfter=network-online.target\n'
                '[Service]\nType=simple\n'
                f'ExecStart="{binary}" serve --host {bind}:{self.port} --compute npu\n'
                'Restart=on-failure\nRestartSec=5\n[Install]\nWantedBy=default.target\n')
        atomic(self.unit_path, data, 0o644)
        run('systemctl', '--user', 'daemon-reload')
        run('sudo', 'loginctl', 'enable-linger', str(os.getuid()))
        run('systemctl', '--user', 'enable', '--now', self.unit)

    def start(self):
        if self.reachable(): return
        if self.runtime == 'ollama':
            run('sudo', 'systemctl', 'start', 'ollama.service')
        elif self.unit_path.exists() and 'local-ai-cluster' in self.unit_path.read_text():
            run('systemctl', '--user', 'start', self.unit)
        else:
            fail('External GenieX service is not reachable. Run setup to create a managed unit if no vendor service exists.')
        self.wait()

    def stop(self):
        if self.runtime == 'ollama':
            run('sudo', 'systemctl', 'stop', 'ollama.service')
        elif self.unit_path.exists() and 'local-ai-cluster' in self.unit_path.read_text():
            run('systemctl', '--user', 'stop', self.unit)
        else:
            fail('Existing GenieX service is externally managed; refusing to stop an unknown service.')

    def pull(self, model):
        run(self.cli(), 'pull', model, env=self.env)

    def sync(self, prune=False):
        self.c.prepare()
        self.install_missing()
        (self.configure_ollama if self.runtime == 'ollama' else self.configure_geniex)()
        self.wait()
        if self.spec.get('prune_native_models'):
            self.cleanup(validated=True)
        existing = self.ids()
        for alias, spec in self.models.items():
            model = spec['id']
            say(f'{"KEEP" if model in existing else "PULL"} {alias}: {model}')
            if model not in existing:
                self.pull(model)
        missing = self.wanted() - self.ids()
        if missing:
            fail(f'Models not advertised after pull: {sorted(missing)}. CLI and server may use different stores.')
        self.validate(all_models=True)
        if prune and not self.spec.get('prune_native_models'):
            self.cleanup(validated=True)
        else:
            self.record(self.wanted())
        say('Edge setup complete: configured models passed actual inference.')

    def wanted(self):
        return {m['id'] for m in self.models.values()}

    def previous(self):
        return set(json.loads(self.state.read_text())['models']) if self.state.exists() else set()

    def record(self, models):
        atomic(self.state, json.dumps({'models': sorted(models)}, indent=2))

    def cleanup(self, validated=False):
        # Only explicitly adopted or listed legacy tags; never rm arbitrary directories.
        retained = {m['id'] for m in self.spec['edge_models'].values()}
        explicit = set(self.spec.get('retire_models', [])) - self.wanted()
        old = (self.ids() - self.wanted()) if self.spec.get('prune_native_models') else (((self.previous() - retained) | explicit) & self.ids())
        if not old:
            if validated:
                self.record(self.wanted() if self.spec.get('prune_native_models') else self.previous() | self.wanted())
            say('No obsolete adopted or explicitly listed edge models.'); return
        if not validated and not self.spec.get('prune_native_models'):
            self.validate(all_models=True)
        say('REMOVE ' + ', '.join(sorted(old)))
        if not validated: confirm('Remove these obsolete model tags using the native runtime?', self.a.yes)
        for model in sorted(old):
            if self.runtime == 'ollama':
                self.unload({'id':model})
                run(self.cli(), 'rm', model, env=self.env)
            else:
                run(self.cli(), 'remove', model, env=self.env)
        if old & self.ids():
            fail('Some removed models are still advertised. Verify server cache ownership; no filesystem deletion attempted.')
        self.record((self.previous() - old) | self.wanted())
        say('Model cleanup complete.')

    def request(self, spec, prompt, context=None, tokens=256):
        model = spec['id']
        start = time.monotonic()
        timeout = int(self.spec.get('request_timeout', 300))
        if self.runtime == 'ollama':
            ctx = int(context or spec.get('context', self.spec.get('context', 4096)))
            body = {'model': model, 'prompt': prompt, 'stream': False, 'keep_alive': '2m',
                    'options': {'num_ctx': ctx, 'num_predict': tokens, 'temperature': 0}}
            if 'think' in spec: body['think'] = spec['think']
            data = api(self.endpoint + '/api/generate', payload=body, timeout=timeout)
            if data.get('error') or not data.get('done') or not data.get('response', '').strip() or not data.get('eval_count'):
                fail(f'{model}: incomplete or empty Ollama generation: {data.get("error", "no response")}')
            ps = api(self.endpoint + '/api/ps')['models']
            loaded = next((m for m in ps if m.get('name') == model or m.get('model') == model), {})
            if not loaded.get('size_vram', 0): fail(f'{model}: Ollama reported no GPU-resident weights.')
            return {'text': data['response'], 'prompt_tokens': data.get('prompt_eval_count'), 'generated_tokens': data['eval_count'],
                    'prompt_tps': data.get('prompt_eval_count', 0)*1e9/max(1,data.get('prompt_eval_duration', 0)),
                    'generation_tps': data['eval_count']*1e9/max(1,data.get('eval_duration', 0)),
                    'cached_prompt_tokens': data.get('prompt_eval_cached_count', 0),
                    'done_reason': data.get('done_reason'),
                    'load_seconds': data.get('load_duration', 0)/1e9, 'seconds': time.monotonic()-start,
                    'context_allocated': loaded.get('context_length'), 'gpu_bytes': loaded['size_vram'], 'loaded_bytes': loaded.get('size')}
        # GenieX uses the advertised vendor model ID. NPU placement for an existing server is not inferred from a chat response.
        body = {'model': model, 'messages': [{'role':'user','content':prompt}], 'stream':False, 'max_tokens':tokens, 'temperature':0}
        if 'think' in spec: body['enable_think'] = spec['think']
        if context is not None: body['nctx'] = context
        data = api(self.endpoint + '/v1/chat/completions', payload=body, timeout=timeout)
        choices = data.get('choices', [])
        text = choices[0].get('message', {}).get('content', '') if choices else ''
        if data.get('error') or not isinstance(text, str) or not text.strip():
            fail(f'{model}: GenieX returned no completion: {data.get("error", "empty response")}')
        seconds = time.monotonic()-start
        usage = data.get('usage') or {}
        return {'text':text, 'prompt_tokens':usage.get('prompt_tokens'), 'generated_tokens':usage.get('completion_tokens'),
                'seconds':seconds, 'end_to_end_tps':usage['completion_tokens']/seconds if usage.get('completion_tokens') else None,
                'accelerator_verified':False}

    def loaded_ids(self):
        rows = api(self.endpoint + '/api/ps')['models']
        ids = {m.get('name') or m.get('model') for m in rows}
        if None in ids:
            fail('Ollama returned a loaded model without an identity.')
        return ids

    def unload(self, spec):
        if self.runtime != 'ollama':
            return
        model = spec['id']
        if model not in self.loaded_ids():
            return
        say(f'UNLOAD {model}; waiting for runner release')
        api(self.endpoint + '/api/generate', payload={'model':model, 'keep_alive':0}, timeout=30)
        deadline = time.monotonic() + int(self.spec.get('unload_timeout_seconds', 60))
        while model in self.loaded_ids():
            if time.monotonic() >= deadline:
                fail(f'{model}: runner still resident after unload timeout; not starting another model.')
            time.sleep(1)
        say(f'RELEASED {model}')

    @contextlib.contextmanager
    def model_session(self, spec):
        """Cold isolated model session; do not stop unrelated runtime clients."""
        if self.runtime != 'ollama':
            yield
            return
        owned = {m['id'] for m in self.spec['edge_models'].values()} | self.previous()
        loaded = self.loaded_ids()
        unknown = loaded - owned
        if unknown:
            fail('Unmanaged Ollama models are active; refusing to interrupt: ' + ', '.join(sorted(unknown)))
        for model in sorted(loaded):
            self.unload({'id':model})
        if self.loaded_ids():
            fail('Ollama still has resident models; cannot start an isolated test.')
        try:
            yield
        except BaseException:
            try:
                self.unload(spec)
            except (OSError, RuntimeError, ValueError, KeyError) as cleanup:
                say(f'Runner cleanup also failed: {cleanup}')
            raise
        else:
            self.unload(spec)

    def validate(self, all_models=False):
        ids = self.ids()
        missing = self.wanted() - ids
        if missing: fail('Missing desired edge models: ' + ', '.join(sorted(missing)))
        rows = []
        for alias, spec in (self.models.items() if all_models else self.selected()):
            try:
                with self.model_session(spec):
                    result = self.request(spec, 'Reply with the word READY. Do not explain.', tokens=256)
                    if 'ready' not in result['text'].lower(): fail(f'{alias}: response did not contain READY.')
                rows.append(dict(result, alias=alias, passed=True))
                say('PASS ' + alias + ' ' + json.dumps({k:v for k,v in result.items() if k != 'text'}))
            except (OSError, RuntimeError, ValueError, KeyError) as exc:
                rows.append(dict(alias=alias, passed=False, error=str(exc)))
                say(f'FAIL {alias}: {exc}')
                if self.runtime == 'geniex' and ('-100301' in str(exc) or 'Plugin loading failed' in str(exc)):
                    say('GenieX plugin failed; existing vendor software and cached models preserved.')
                    try:
                        version = run(self.cli(), '--version', check=False, capture=True)
                        rows[-1]['runtime_version'] = (version.stdout or '') + (version.stderr or '')
                    except (OSError, RuntimeError):
                        pass
        self.report('validation', rows)
        if not all(r['passed'] for r in rows):
            fail('One or more configured edge models failed inference; seed incomplete; no rollback.')
        if self.runtime == 'geniex': say('Text inference verified; NPU placement not independently verified on the existing service.')

    def health(self):
        say(f'{self.a.node}: {self.runtime} {self.endpoint}')
        run('df','-h',Path.home(),check=False); run('free','-h',check=False)
        say('Advertised: ' + ', '.join(sorted(self.ids())))
        say('Desired: ' + ', '.join(self.models))
        missing = self.wanted() - self.ids()
        if missing: fail('Missing desired models: ' + ', '.join(sorted(missing)))
        if self.runtime == 'ollama':
            say('Loaded: ' + json.dumps(api(self.endpoint + '/api/ps')))
            if shutil.which('tegrastats'):
                try: run_result = subprocess.run(['tegrastats','--interval','1000'], capture_output=True, text=True, timeout=2)
                except subprocess.TimeoutExpired as e:
                    output = e.stdout or b''; say(output.decode(errors='replace') if isinstance(output,bytes) else output)
                else: say(run_result.stdout)
        else:
            say('GenieX reachable; health is discovery only. --validate executes generation.')

    def report(self, kind, rows):
        path = self.c.root/'benchmarks'/f'{self.a.node}-{kind}-{time.time_ns()}.json'
        atomic(path, json.dumps(rows, indent=2)); say('Report: ' + str(path))

    def benchmark(self):
        rows=[]
        for alias,spec in self.selected():
            with self.model_session(spec):
                for trial in range(3):
                    say(f'BENCHMARK {alias} trial {trial+1}/3')
                    result=self.request(spec, f'Run {trial}: Explain how soil moisture sensors automate irrigation in 200 words.', tokens=256)
                    rows.append(dict(result, alias=alias, trial=trial)); say(json.dumps({k:v for k,v in rows[-1].items() if k!='text'}))
        self.report('benchmark', rows)

    def calibrate(self):
        if self.a.cache_type:
            fail('Edge KV cache comparisons are not supported.')
        if self.runtime != 'ollama':
            if self.a.apply:
                fail('Cannot apply a context setting to a fixed QAIRT model bundle.')
            say('GenieX: context is fixed for QAIRT bundles. Running inference benchmark instead; no context recommendation.')
            return self.benchmark()
        from calibrate import meminfo, prompt_messages
        import yaml
        from manage import REPO
        profile = REPO/'config'/f'{self.a.node}.yaml'
        original = profile.read_text()
        cfg=self.spec['calibration']; rows=[]
        confirm('Calibration runs long prompts and may interrupt other inference. Continue?', self.a.yes)
        for alias,spec in self.selected():
            best=None
            for ctx in cfg['contexts']:
                if ctx > spec.get('target_context', max(cfg['contexts'])):
                    break
                passes=[]
                for depth in (.1,.5,.9):
                    r = None
                    try:
                        with self.model_session(spec):
                            before=meminfo()
                            count=max(10,int(ctx*.77/24))
                            # A short answer is enough to verify retrieval; reserve room for reasoning.
                            budget=min(512, max(128,ctx//8))
                            previous_tokens = None
                            for attempt in range(5):
                                messages,code=prompt_messages(count,depth,time.time_ns())
                                prompt=messages[0]['content'].replace('alone on the first line, then explain in about 100 words how you found it.', 'alone. No explanation.')
                                say(f'{alias} context={ctx} depth={depth} attempt={attempt+1}: processing {count} records')
                                with observe_memory(meminfo) as (samples, errors):
                                    r=self.request(spec,prompt,context=ctx,tokens=budget)
                                n=r.get('prompt_tokens') or 0
                                say(f'  measured tokens={n}, allocated={r.get("context_allocated")}, generation={r["generation_tps"]:.2f} t/s, seconds={r["seconds"]:.1f}')
                                worst = {'host_free_mib':min(x['host_free_mib'] for x in samples),
                                         'swapout_pages':max(x['swapout_pages'] for x in samples)}
                                if errors: fail('Memory telemetry error: ' + '; '.join(errors))
                                if worst['host_free_mib'] < cfg['min_host_free_mib']: fail(f'Host available memory {worst["host_free_mib"]:.0f} MiB below floor {cfg["min_host_free_mib"]} MiB')
                                if worst['swapout_pages'] > before['swapout_pages']: fail(f'Swap-out increased by {worst["swapout_pages"]-before["swapout_pages"]} pages')
                                if ctx*.70 <= n <= ctx*.85: break
                                if not n: break
                                if previous_tokens == n:
                                    say('  Prompt token count did not increase; possible truncation/accounting limit. Rejecting this fill target.')
                                    break
                                previous_tokens = n
                                # Even a truncated count near the context cap must shrink and retry.
                                adjusted=max(1,int(count*ctx*.77/n))
                                if adjusted==count: adjusted += 1 if n < ctx*.70 else -1
                                count=max(1,adjusted)
                            after=worst
                            reasons=context_checks(r,code,ctx,cfg,before,after)
                            passed=not reasons
                        rows.append(dict(r,alias=alias,context=ctx,depth=depth,passed=passed,failed_checks=reasons,host_free_mib=after['host_free_mib'],memory_samples=len(samples)))
                        if reasons: say('  Failed checks: ' + ', '.join(reasons))
                    except (RuntimeError,OSError,ValueError,KeyError,TypeError) as e:
                        passed=False; rows.append(dict(alias=alias,context=ctx,depth=depth,passed=False,error=str(e)))
                        say('  Error: ' + str(e))
                    passes.append(passed); say(f'{alias} context={ctx} depth={depth}: {"PASS" if passed else "FAIL"}')
                    if not passed: break
                if not all(passes) or len(passes)!=3:
                    # A low-context retrieval failure need not prove all larger contexts fail.
                    # Stop on explicit memory/load failure; otherwise try next context once.
                    last=rows[-1]
                    if last.get('error') or any(x in last.get('failed_checks',[]) for x in ('host_memory','no_swapout','request_time','allocated_context')):
                        break
                    continue
                best=ctx
            rows.append({'alias':alias,'recommended_context':best}); say(f'{alias}: recommended context {best or "none"}')
        self.report('context', rows)
        if any(r.get('recommended_context',1) is None for r in rows): fail('No passing context for one or more models.')
        if self.a.apply:
            if profile.read_text() != original: fail('Profile changed during calibration; review report before applying.')
            # Replace only scalar context values, preserving comments and other fields.
            from yaml import compose
            doc = dict((k.value, v) for k,v in compose(original).value)
            specs = dict((k.value,v) for k,v in doc['edge_models'].value)
            edits=[]
            for r in rows:
                if 'recommended_context' not in r: continue
                node = dict((k.value,v) for k,v in specs[r['alias']].value).get('context')
                if node is None: fail('Add a context field to the model profile before using --apply.')
                edits.append((node.start_mark.index,node.end_mark.index,str(r['recommended_context'])))
            for start,end,value in sorted(edits,reverse=True): original=original[:start]+value+original[end:]
            atomic(profile, original, 0o644)
            say('Updated measured model contexts in source YAML; rerun setup. Commit the reviewed profile.')

    def dispatch(self):
        action=self.a.action
        if action=='health': self.health()
        elif action=='validate': self.validate()
        elif action=='benchmark': self.benchmark()
        elif action=='calibrate': self.calibrate()
        elif action in ('install','models','migrate','seed'):
            self.c.plan(); confirm('Reconcile edge runtime and delete all unconfigured native model tags?', self.a.yes)
            self.sync(prune=True)
        elif action=='cleanup': self.cleanup()
        elif action=='start': self.start()
        elif action=='stop': self.stop()
        elif action=='reset':
            confirm('Reconcile edge runtime and restart managed service (preserve weights)?',self.a.yes)
            if self.runtime=='ollama' or self.unit_path.exists(): self.stop()
            self.sync()
        elif action in ('purge','uninstall'):
            fail('Vendor runtime uninstall/purge is not supported; use --cleanup for adopted models.')
        else: fail('Unsupported edge action: '+action)
