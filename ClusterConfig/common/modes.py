#!/usr/bin/env python3
"""Optional, one-shot mode control. Never calls seed or reconciles during a switch."""
import argparse
import glob
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import subprocess
import sys
import time
import urllib.request

REPO = Path(__file__).resolve().parents[1]
ROOT = Path.home() / 'inference'
LLM = 'local-ai-inference.service'
MEDIA = 'local-ai-media.service'
from inventory import profiles, gateway, members


def media_backend(node):
    backend = profiles()[node].get('media_backend')
    require(backend in ('cuda', 'xpu'), 'No supported media backend configured for ' + node)
    return backend


def run(*args, check=True):
    return subprocess.run(list(map(str, args)), check=check, text=True, capture_output=True)


def require(ok, message):
    if not ok:
        raise RuntimeError(message)


def digest(path):
    h = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for block in iter(lambda: stream.read(8 * 1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()


def read(path):
    return json.loads(Path(path).read_text())


def write(path, value):
    from manage import atomic
    atomic(path, json.dumps(value, indent=2) + '\n')


def http(url, key=None):
    req = urllib.request.Request(url, headers={'Authorization': 'Bearer ' + key} if key else {})
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open(req, timeout=8) as response:
        return json.load(response)


def active(unit):
    state = run('systemctl', 'show', unit, '-p', 'ActiveState', '--value').stdout.strip()
    require(state in ('active', 'inactive', 'failed'), 'Unknown/transitional service state: ' + unit + ': ' + state)
    return state == 'active'


def idle(llm, media, endpoint, key):
    # Conservative: a loaded LLM may be generating; wait for its native idle TTL.
    # Never unload a potentially busy model to make this test pass.
    if llm:
        result = http(endpoint + '/running', key)
        require(isinstance(result, dict) and isinstance(result.get('running'), list),
                'Unrecognized llama-swap /running response; refusing switch')
        require(not result['running'], 'LLM loaded/busy: stop clients and wait for model TTL before switching')
    if media:
        result = http('http://127.0.0.1:8188/queue')
        require(isinstance(result, dict) and all(isinstance(result.get(k), list) for k in ('queue_running', 'queue_pending')),
                'Unknown media queue state; refusing switch')
        require(not result['queue_running'] and not result['queue_pending'], 'Media has running or queued jobs')


def gpu_free():
    devices = sorted(set(glob.glob('/dev/nvidia[0-9]*') + glob.glob('/dev/dri/renderD*')))
    require(devices, 'No GPU device files; cannot establish exclusive ownership')
    result = run('sudo', '-n', 'fuser', *devices, check=False)
    require(result.returncode == 1 and not result.stdout.strip() and not result.stderr.strip(),
            'GPU device is in use or ownership probe failed; inspect fuser as root')


def private_path(value):
    raw = Path(value).expanduser()
    require(raw.is_absolute(), 'Private storage must be an absolute path')
    path = raw.resolve()
    require(path.is_absolute() and path != Path.home() and path != Path('/'), 'Choose a dedicated private directory')
    require(not path.is_relative_to(REPO.parent.resolve()) and not REPO.resolve().is_relative_to(path),
            'Private media must be outside the repository')
    require(not any(x.lower() in ('onedrive', 'dropbox', 'google drive') for x in path.parts), 'Use non-synced private storage')
    return path


def unit_quote(value):
    text = str(value)
    require(not re.search(r'[\r\n%"\\]', text), 'Unsupported systemd path character')
    return '"' + text + '"'


def install_text(path, text):
    import tempfile
    with tempfile.TemporaryDirectory() as tmp:
        source = Path(tmp) / 'unit'
        source.write_text(text)
        run('sudo', 'install', '-D', '-m', '0644', source, path)


def interlock():
    """Lifetime flock is shared by both services, including manual/reboot starts."""
    source = Path('/etc/systemd/system') / LLM
    require(source.is_file() and 'local-ai-cluster' in source.read_text(), 'Provision General Purpose first')
    commands = re.findall(r'^ExecStart=(.+)$', source.read_text(), re.M)
    require(len(commands) == 1, 'Expected one managed inference ExecStart')
    drop = source.with_name(LLM + '.d') / '50-mode-ownership.conf'
    text = '# local-ai-cluster mode ownership\n[Service]\nExecStart=\nExecStart=/usr/bin/flock --no-fork -n ' + unit_quote(ROOT / '.accelerator.lock') + ' ' + commands[0] + '\n'
    if drop.exists():
        require(drop.read_text() == text, 'Existing ownership drop-in differs; review manually')
    else:
        install_text(drop, text)
    run('systemctl', '--version')
    run('sudo', 'systemctl', 'daemon-reload')


def manifest():
    import yaml
    return yaml.safe_load((REPO / 'config/profiles/media.yaml').read_text())


def enabled():
    write(ROOT / 'config/modes-enabled.json', {'preserve_installed_profiles': True})


def catalog_models(c):
    import yaml
    shared = yaml.safe_load((REPO / 'config/profiles/abliterated.yaml').read_text())['models']
    node = c.c['node']
    available = {**c.desired, **{node + '/' + name: spec for name, spec in shared.items()}}
    return {alias: available[alias] for mode in c.c['modes'].values()
            if mode.get('catalog') == 'abliterated' for alias in mode['models']}


def provision_catalog(node, catalog):
    import yaml
    from manage import Cluster, atomic
    args = argparse.Namespace(node=node, backend=None, dry_run=False)
    c = Cluster(args)
    require(c.role == 'worker', 'Language catalogs are provisioned on workers')
    c.service_owned()
    enabled()  # Block legacy seed/prune before the first profile download.
    dest = ROOT / 'config/catalogs'
    dest.mkdir(parents=True, exist_ok=True)
    require(c.native.is_file(), 'Provision General Purpose first')
    if catalog == 'abliterated':
        c.desired = catalog_models(c)
        c.c['resident_groups'] = []
    else:
        c.desired = {alias: c.desired[alias] for alias in c.c['modes']['general']['models']}
    rendered, _ = c.render_worker()
    atomic(dest / (catalog + '.yaml'), yaml.safe_dump(rendered, sort_keys=False))
    interlock()
    print('Catalog staged; no services started. Validate compatibility and model quality separately.')


def provision_media(node, storage):
    backend = media_backend(node)
    require(storage, '--storage is required for provision-media')
    base = private_path(storage)
    spec = manifest()
    runtime = base / ('comfy-' + spec['comfyui']['revision'])
    base.mkdir(parents=True, exist_ok=True, mode=0o700)
    require(base.stat().st_mode & 0o077 == 0, 'Private directory must have mode 700')
    enabled()
    if not runtime.exists():
        run('git', 'clone', '--no-checkout', spec['comfyui']['url'], runtime)
        run('git', '-C', runtime, 'checkout', '--detach', spec['comfyui']['revision'])
    require(run('git', '-C', runtime, 'rev-parse', 'HEAD').stdout.strip() == spec['comfyui']['revision'], 'ComfyUI revision mismatch')
    require(not run('git', '-C', runtime, 'status', '--porcelain', '--untracked-files=no').stdout.strip(), 'ComfyUI source has edits')
    env = base / 'venv'
    require(not env.exists(), 'Media venv already exists; inspect it rather than overwriting it')
    run('uv', 'venv', '--python', spec['python'], env)
    constraints = base / 'constraints.txt'
    constraints.write_text('\n'.join(k + '==' + v for k, v in spec['pytorch'].items()) + '\n')
    index = spec['indexes'][backend]
    run('uv', 'pip', 'install', '--python', env / 'bin/python', '--index-url', index,
        *[k + '==' + v for k, v in spec['pytorch'].items()])
    run('uv', 'pip', 'install', '--python', env / 'bin/python', '-c', constraints, '-r', runtime / 'requirements.txt')
    run('uv', 'pip', 'check', '--python', env / 'bin/python')
    (base / 'installed-packages.txt').write_text(run('uv', 'pip', 'freeze', '--python', env / 'bin/python').stdout)
    for name in ('input', 'output', 'temp', 'user', 'models', 'cache'):
        (base / name).mkdir(exist_ok=True, mode=0o700)
    # Model downloads are a separate explicit operation, after runtime smoke tests.
    command = [env / 'bin/python', runtime / 'main.py', '--listen', '127.0.0.1', '--port', '8188',
               '--base-directory', base, '--models-directory', '${MEDIA_MODELS}', '--disable-auto-launch', '--disable-all-custom-nodes',
               '--offline', '--use-pytorch-cross-attention', '--lowvram']
    if backend == 'cuda':
        command += ['--cuda-device', '0']
    unit = '# local-ai-cluster private media\n[Unit]\nDescription=Private local ComfyUI\n[Service]\n'
    unit += 'User=' + run('id', '-un').stdout.strip() + '\nUMask=0077\n'
    unit += 'WorkingDirectory=' + unit_quote(base) + '\n'
    unit += 'ExecStart=/usr/bin/flock --no-fork -n ' + unit_quote(ROOT / '.accelerator.lock') + ' ' + ' '.join(map(unit_quote, command)) + '\n'
    unit += 'EnvironmentFile=' + unit_quote(ROOT / 'config/media.env') + '\n'
    from manage import atomic
    atomic(ROOT / 'config/media.env', 'MEDIA_MODELS=' + unit_quote(base / 'models') + '\n')
    unit += 'Environment="HF_HUB_OFFLINE=1" "TRANSFORMERS_OFFLINE=1" "HF_HUB_DISABLE_TELEMETRY=1"\n'
    unit += 'Environment="XDG_CACHE_HOME=' + str(base / 'cache') + '"\n'
    unit += 'NoNewPrivileges=true\nPrivateTmp=true\nProtectSystem=strict\nReadWritePaths=' + unit_quote(base) + ' ' + unit_quote(ROOT) + '\n'
    unit += 'IPAddressDeny=any\nIPAddressAllow=localhost\nRestart=no\nTimeoutStopSec=120\n'
    path = Path('/etc/systemd/system') / MEDIA
    require(not path.exists(), 'Existing media service; review before replacement')
    install_text(path, unit)
    interlock()
    write(ROOT / 'config/media-install.json', {'node': node, 'base': str(base), 'runtime': str(runtime), 'manifest': digest(REPO / 'config/profiles/media.yaml'), 'unit': digest(path)})
    print('Media staged, not enabled or started. Run verify-media, then download-media explicitly.')


def verify_media(node):
    import yaml
    install = read(ROOT / 'config/media-install.json')
    require(install['node'] == node and install['manifest'] == digest(REPO / 'config/profiles/media.yaml'), 'Media installation is stale')
    require(not active(LLM) and not active(MEDIA), 'Stop idle services before accelerator validation')
    gpu_free()
    backend = media_backend(node)
    code = ('import torch; assert torch.' + backend + '.is_available(); '
            'x=torch.ones((64,64),device="' + backend + '"); '
            'assert (x@x).cpu()[0,0].item()==64; print(torch.' + backend + '.get_device_name(0))')
    python = Path(install['base']) / 'venv/bin/python'
    import fcntl
    with (ROOT / '.accelerator.lock').open('a') as owner:
        fcntl.flock(owner, fcntl.LOCK_EX | fcntl.LOCK_NB)
        name = run(python, '-c', code).stdout.strip()
    profile = yaml.safe_load((REPO / 'config' / (node + '.yaml')).read_text())
    require(re.search(profile['hardware']['device_regex'], name, re.I), 'Unexpected accelerator: ' + name)
    print(name)
    run(python, Path(install['runtime']) / 'main.py', '--help')
    write(ROOT / 'config/media-verified.json', dict(install, backend=backend, checked=time.time()))
    print('Runtime smoke test passed. Checkpoint/workflow output quality remains experimental.')


def validate_catalog(node, catalog, quiesced):
    """Explicit inference test, isolated from daily switching; no downloads."""
    import yaml
    from manage import Cluster, atomic, api, offload_counts
    require(profiles()[node]['role'] == 'worker' and quiesced, 'Worker validation requires stopped clients and --quiesced')
    require(not active(LLM) and not active(MEDIA), 'Switch off first; validation will not interrupt services')
    gpu_free()
    c = Cluster(argparse.Namespace(node=node, backend=None, dry_run=False, model=None))
    path = ROOT / 'config/catalogs' / (catalog + '.yaml')
    require(path.is_file(), 'Provision catalog first')
    if catalog == 'abliterated':
        c.desired = catalog_models(c)
    else:
        c.desired = {alias: c.desired[alias] for alias in c.c['modes']['general']['models']}
    original = c.native.read_bytes()
    outputs = {}
    try:
        atomic(c.native, path.read_bytes())
        run('sudo', 'systemctl', 'start', LLM)
        for attempt in range(30):
            try:
                c.discover()
                break
            except (OSError, RuntimeError):
                if attempt == 29:
                    raise
                time.sleep(1)
        c.validate()  # Real chat-template and structured tool tests.
        for alias in c.desired:
            counts = offload_counts(c.model_log(alias).read_text())
            require(counts and counts[0] == counts[1], 'Full GPU offload not demonstrated for ' + alias)
            prompt = c.desired[alias].get('quality_prompt') or yaml.safe_load(
                (REPO / 'config/profiles/abliterated.yaml').read_text())['quality_prompt']
            outputs[alias] = api(c.endpoint + '/v1/chat/completions', c.key('worker'), {
                'model': alias, 'messages': [{'role': 'user', 'content': prompt}],
                'max_tokens': 1024, 'temperature': 0.2}, timeout=600)
        write(path.with_suffix('.compatibility.json'), {'config_sha256': digest(path), 'compatibility_pass': True,
              'runtime': c.v, 'node': node, 'samples': outputs})
    finally:
        run('sudo', 'systemctl', 'stop', LLM)
        atomic(c.native, original)
    print('Compatibility passed; services left off. Review samples separately before enabling overlay.')


def provision_router(node):
    from manage import Cluster, atomic
    import yaml
    c = Cluster(argparse.Namespace(node=node, backend=None, dry_run=False))
    require(not c.service_active(), 'Stop the idle gateway and its clients before provisioning router routes')
    require(c.service_path().is_file() and c.active.is_file(), 'Provision original gateway first')
    enabled()  # Preserve all installed Mac model tags from legacy seed pruning.
    atomic(c.native, yaml.safe_dump(c.render_gateway(), sort_keys=False))
    print('Gateway routes staged. Use router-start when ready; no models downloaded or removed.')


def download_media(bundle):
    from manage import download
    install = read(ROOT / 'config/media-verified.json')
    require(install['manifest'] == digest(REPO / 'config/profiles/media.yaml'), 'Revalidate changed runtime first')
    base = private_path(install['base'])
    for item in manifest()['models']:
        if bundle not in item['bundles']:
            continue
        target = base / 'models' / item['destination']
        require(target.resolve().is_relative_to(base / 'models'), 'Unsafe model destination')
        download('https://huggingface.co/' + item['repo'] + '/resolve/' + item['revision'] + '/' + item['file'],
                 target, item['sha256'], item['size'], reserve=15 * 1024**3)
        view = base / 'bundles' / bundle / item['destination']
        view.parent.mkdir(parents=True, exist_ok=True)
        if not view.exists():
            view.symlink_to(target)
        require(view.resolve() == target.resolve(), 'Unexpected bundle link')
    print('Pinned media files verified. No workflow queued.')


def switch(node, mode, catalog, quiesced, profile=None):
    from manage import Cluster, atomic
    require(quiesced, 'Stop all submitting clients/tunnels first, then pass --quiesced; no force/busy override exists')
    require((ROOT / 'config/modes-enabled.json').is_file(), 'Run provision-catalog standard first')
    c = Cluster(argparse.Namespace(node=node, backend=None, dry_run=False))
    require(c.role == 'worker', 'Mac remains the LLM router; switch individual workers with --remote')
    llm, media = active(LLM), active(MEDIA)
    require(not (llm and media), 'Conflicting services active; inspect manually')
    if media:
        unit = Path('/etc/systemd/system') / MEDIA
        install = read(ROOT / 'config/media-install.json')
        require(install.get('node') == node and install.get('unit') == digest(unit),
                'Media unit ownership changed; inspect manually before stopping it')
    # Validate target before disturbing current service.
    config = ROOT / 'config/catalogs' / (catalog + '.yaml')
    selected = c.c['modes'][profile] if profile else None
    if mode == 'general':
        require(config.is_file(), 'Provision requested catalog first')
        import yaml
        prepared = yaml.safe_load(config.read_text())
        if selected:
            aliases = selected['models']
            require(aliases and set(aliases) <= prepared['models'].keys(), 'Provision missing models in selected catalog')
            prepared['models'] = {alias: prepared['models'][alias] for alias in aliases}
            if 'routing' in prepared:
                from manage import model_groups
                groups = [[a for a in group if a in aliases] for group in c.c.get('resident_groups', [])]
                prepared['routing']['router']['settings']['groups'] = model_groups(aliases, [g for g in groups if len(g) > 1])
        if catalog == 'abliterated':
            compatibility = read(config.with_suffix('.compatibility.json'))
            require(compatibility.get('config_sha256') == digest(config) and compatibility.get('compatibility_pass') is True and compatibility.get('node') == node and compatibility.get('runtime') == c.v,
                    'Run validate-catalog on this node with current runtime first')
            proof = read(ROOT / 'config/catalogs/abliterated-reviewed.json')
            require(proof.get('config_sha256') == digest(config) and proof.get('compatibility_pass') is True and proof.get('quality_pass') is True,
                    'Independent checkpoint compatibility and quality review required')
    if mode == 'video':
        backend = media_backend(node)
        proof = read(ROOT / 'config/media-verified.json')
        require(proof['node'] == node and proof['manifest'] == digest(REPO / 'config/profiles/media.yaml') and proof['unit'] == digest(Path('/etc/systemd/system') / MEDIA), 'Media validation missing/stale')
        if selected:
            weights = {item['destination']: item for item in manifest()['models']
                       if set(item['bundles']).intersection(selected['bundles'])}
            require(set(selected['models']) == weights.keys(), 'Mode models differ from selected media bundle')
            require(len(selected['bundles']) == 1, 'Select one media bundle per mode')
            view = Path(proof['base']) / 'bundles' / selected['bundles'][0]
            for name, item in weights.items():
                require((view / name).stat().st_size == item['size'], 'Provision media bundle first')
        if backend == 'xpu':
            review = read(ROOT / 'config/media-xpu-reviewed.json')
            require(review.get('manifest') == proof['manifest'] and review.get('node') == node
                    and review.get('checkpoint_pass') is True and review.get('workflow_pass') is True
                    and review.get('quality_pass') is True and review.get('evidence'),
                    'XPU requires recorded checkpoint/workflow compatibility and quality evidence, not just a tensor smoke test')
    idle(llm, media, c.endpoint, c.key('worker'))
    if llm:
        c.stop()
    if media:
        run('sudo', 'systemctl', 'stop', MEDIA)
    require(not active(LLM) and not active(MEDIA), 'Service did not stop; refusing to start target')
    # No automatic rollback: it could restart an accelerator owner after failure.
    write(ROOT / 'config/mode-state.json', {'mode': 'off', 'catalog': catalog, 'node': node})
    if mode != 'off':
        gpu_free()
        target = LLM if mode == 'general' else MEDIA
        try:
            if mode == 'general':
                atomic(c.native, yaml.safe_dump(prepared, sort_keys=False))
            elif selected:
                atomic(ROOT / 'config/media.env', 'MEDIA_MODELS=' + unit_quote(view) + '\n')
            run('sudo', 'systemctl', 'start', target)  # Never setup --start (that seeds!).
            for attempt in range(60):
                try:
                    require(active(target), 'Target service exited during startup')
                    if mode == 'general':
                        import yaml
                        expected = set(prepared['models'])
                        found = http(c.endpoint + '/v1/models', c.key('worker'))
                        require(expected == {x['id'] for x in found['data']}, 'Prepared catalog differs from advertised models')
                    else:
                        found = http('http://127.0.0.1:8188/system_stats')
                        backend = media_backend(node)
                        require(any(d.get('type') == backend for d in found.get('devices', [])), 'ComfyUI did not select intended GPU backend')
                    break
                except (OSError, ValueError):
                    if attempt == 59:
                        raise RuntimeError('Target readiness timed out; inspect status/journal')
                    time.sleep(1)
        except BaseException:
            run('sudo', 'systemctl', 'stop', target)
            raise
    write(ROOT / 'config/mode-state.json', {'mode': profile or mode, 'catalog': catalog, 'node': node})
    print(json.dumps({'node': node, 'mode': profile or mode, 'catalog': catalog}))


def remote(args):
    local = read(REPO / 'config/cluster.local.json')
    target = local['targets'][args.node]
    require(re.fullmatch(r'(?:[A-Za-z0-9_][A-Za-z0-9_.-]*@)?[A-Za-z0-9][A-Za-z0-9_.-]*', target), 'Invalid SSH target')
    repo = local['repo']
    require(repo.startswith(('/', '~/')) and not re.search(r'[\x00-\x1f]', repo), 'Invalid remote repo path')
    quoted = '"$HOME"/' + shlex.quote(repo[2:]) if repo.startswith('~/') else shlex.quote(repo)
    command = ['bash', 'common/setup.sh', '--modes', args.action, '--node', args.node, '--catalog', args.catalog]
    if getattr(args, 'profile', None):
        command += ['--profile', args.profile]
    if getattr(args, 'roles', False):
        command.append('--roles')
    if args.quiesced:
        command.append('--quiesced')
    if args.dry_run:
        command.append('--dry-run')
    if args.storage:
        command += ['--storage', args.storage]
    command += ['--bundle', args.bundle]
    cmd = 'cd ' + quoted + ' && ' + shlex.join(command)
    if args.dry_run:
        print('SSH (not executed): ' + target + ' ' + cmd)
        return
    result = subprocess.run(['ssh', '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes', '-o', 'ConnectTimeout=8', target, cmd])
    require(result.returncode == 0, 'Remote action failed; inspect node status before retrying')


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('action', choices=['inventory', 'status', 'general', 'video', 'off', 'provision-catalog', 'validate-catalog', 'provision-router', 'router-start', 'provision-media', 'verify-media', 'download-media'])
    nodes = profiles()
    p.add_argument('--node', choices=nodes)
    p.add_argument('--roles', action='store_true', help='Include roles in inventory output')
    p.add_argument('--catalog', choices=['standard', 'abliterated'], default='standard')
    p.add_argument('--profile', choices=['general', 'general-abliterated', 'video', 'video-abliterated'])
    p.add_argument('--remote', action='store_true', help='Use existing cluster.local.json SSH mappings; works on Mac or Windows')
    p.add_argument('--quiesced', action='store_true', help='Affirm all clients and tunnels have stopped submitting requests')
    p.add_argument('--storage', help='Dedicated private, non-synced directory outside repository')
    p.add_argument('--bundle', choices=sorted({bundle for item in manifest()['models'] for bundle in item['bundles']}), default='starter')
    p.add_argument('--dry-run', action='store_true')
    a = p.parse_args()
    if a.action == 'inventory':
        a.node = a.node or gateway(nodes)
        require(a.node == gateway(nodes), 'Inventory belongs to the gateway')
        if a.remote:
            remote(a)
        else:
            for name, role in members(nodes):
                print(name + '\t' + role if a.roles else name)
        return
    if not a.node:
        p.error('--node is required for this action')
    role = nodes[a.node]['role']
    if role == 'edge':
        require(a.action == 'status' or a.profile, 'Choose a cluster mode or status for the edge runtime')
        profile = nodes[a.node]
        models = [m['id'] for m in profile['edge_models'].values() if m.get('enabled', True)]
        if a.remote and not a.dry_run:
            remote(a)
            return
        print(f"{a.node}: {a.profile or 'status'} | {profile['runtime']} | {', '.join(models)}")
        if not a.dry_run:
            subprocess.run(['bash', str(REPO / 'common/setup.sh'), a.node, '--health'], check=True)
        return
    if a.profile:
        import yaml
        selection = yaml.safe_load((REPO / 'config' / (a.node + '.yaml')).read_text())['modes'][a.profile]
        a.action = {'llm': 'general', 'comfyui': 'video'}[selection['runtime']]
        a.catalog = selection.get('catalog', 'standard')
        print(a.node, a.profile, selection['runtime'], ', '.join(selection['models']), flush=True)
    if a.remote:
        remote(a)
        return
    if a.dry_run:
        print(json.dumps({'node': a.node, 'action': a.action, 'catalog': a.catalog, 'effects': 'none', 'switch_downloads': False, 'switch_deletions': False}))
        return
    if role == 'gateway':
        require(sys.platform == 'darwin' and a.action in ('status', 'provision-router', 'router-start'), 'Mac supports status/provision-router/router-start only; switch workers with --remote')
    else:
        require(sys.platform.startswith('linux'), 'Local actions require the Linux worker; use --remote from Mac/Windows')
    if a.action == 'status':
        if role == 'gateway':
            from manage import Cluster
            c = Cluster(argparse.Namespace(node=a.node, backend=None, dry_run=False))
            print(json.dumps({'router_active': c.service_active(), 'routes': sorted(c.c['routes'])}))
            return
        state = ROOT / 'config/mode-state.json'
        print(json.dumps({'recorded': read(state) if state.exists() else None, 'llm_active': active(LLM), 'media_active': active(MEDIA)}))
        return
    import fcntl
    ROOT.mkdir(exist_ok=True)
    with (ROOT / '.manage.lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        if a.action == 'provision-router':
            require(role == 'gateway', 'Router is on Mac only')
            provision_router(a.node)
        elif a.action == 'router-start':
            require(role == 'gateway', 'Router is on Mac only')
            from manage import Cluster
            Cluster(argparse.Namespace(node=a.node, backend=None, dry_run=False)).start()
        elif a.action == 'provision-catalog':
            provision_catalog(a.node, a.catalog)
        elif a.action == 'validate-catalog':
            validate_catalog(a.node, a.catalog, a.quiesced)
        elif a.action == 'provision-media':
            provision_media(a.node, a.storage)
        elif a.action == 'verify-media':
            verify_media(a.node)
        elif a.action == 'download-media':
            download_media(a.bundle)
        else:
            switch(a.node, a.action, a.catalog, a.quiesced, a.profile)


if __name__ == '__main__':
    try:
        main()
    except (RuntimeError, OSError, ValueError, KeyError, subprocess.CalledProcessError) as error:
        print('ERROR: ' + str(error), file=sys.stderr)
        sys.exit(1)
