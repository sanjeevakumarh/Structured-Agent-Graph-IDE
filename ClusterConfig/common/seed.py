#!/usr/bin/env python3
"""Clean desired-state seed, called by setup scripts. No rollback or old service.
Review legacy_cleanup in each node YAML: those dedicated directories are deleted
before new downloads. Keep only verified enabled weights and authentication keys.
Never remove OS packages, drivers, partitions, or unrelated application paths.
"""
import json
import os
import re
import shutil
import time
from pathlib import Path
from manage import (atomic, confirm, fail, model_sources, owned,
                    remove_owned, run, say, SERVICE, RELEASE, api)


def safe_tree(path):
    """Reject symlink roots, nested mounts (including Linux bind mounts)."""
    if not path.is_absolute() or path.resolve() != path or path.is_symlink():
        fail('Unsafe cleanup path: ' + str(path))
    if path.exists() and (not path.is_dir() or os.path.ismount(path)):
        fail('Cleanup target is not a regular directory: ' + str(path))
    mountinfo = Path('/proc/self/mountinfo')
    if mountinfo.is_file():
        for line in mountinfo.read_text().splitlines():
            mount = Path(re.sub(r'\\([0-7]{3})', lambda m: chr(int(m[1], 8)), line.split()[4]))
            if mount == path or mount.is_relative_to(path):
                fail('Cleanup target contains a mount: ' + str(mount))


def cleanup_plan(c):
    """Only explicitly inventoried inference-only roots; not arbitrary home paths."""
    home = Path.home().resolve()
    rules = c.c.get('legacy_cleanup', {})
    allowed = {home / n for n in ('models','llama-rocm','llama-vulkan-gmini','llama-vulkan')}
    allowed |= {Path('/usr/share/ollama/.ollama/models'), Path('/usr/local/lib/ollama')}
    dirs = [Path(x).expanduser() for x in rules.get('directories', [])]
    files = [Path(x).expanduser() for x in rules.get('files', [])]
    services = rules.get('services', [])
    for path in dirs:
        if path not in allowed:
            fail('Directory outside inventoried inference allowlist: ' + str(path))
        # System store may not be readable to the login user: check with elevated
        # access below. Mounts are visible independently of file permissions.
        safe_tree(path)
    for path in files:
        if path not in {home/'llama-swap.yaml',Path('/usr/local/bin/ollama'),Path('/usr/local/bin/llama-swap')}:
            fail('File outside inventoried inference allowlist: ' + str(path))
        if path.exists() and path.is_dir() and not path.is_symlink():
            fail('Expected file, not directory: ' + str(path))
    if not set(services) <= {'ollama.service','llama-swap.service'}:
        fail('Unexpected legacy unit name')
    return dirs, files, services


def retire_unit(unit):
    """Permanently retire old inference unit. Mask prevents accidental restart."""
    state = run('systemctl','show','-p','LoadState','--value',unit,check=False,capture=True).stdout.strip()
    if state not in ('not-found','masked',''):
        run('sudo','systemctl','disable','--now',unit)
    if run('systemctl','is-active','--quiet',unit,check=False,capture=True).returncode == 0:
        fail('Legacy service still running: ' + unit)
    path = Path('/etc/systemd/system') / unit
    if not (path.is_symlink() and path.resolve() == Path('/dev/null')):
        run('sudo','rm','-f','--',path)
    drop = path.with_name(unit+'.d')
    safe_tree(drop)
    if drop.exists(): run('sudo','rm','-rf','--one-file-system','--',drop)
    run('sudo','systemctl','daemon-reload')
    run('sudo','systemctl','mask',unit)
    say('RETIRED ' + unit)


def remove_tree(path):
    safe_tree(path)
    if path.is_relative_to(Path.home().resolve()):
        if path.exists():
            say('REMOVE ' + str(path)); shutil.rmtree(path)
    else:
        # Exact roots were checked by cleanup_plan. Do not follow symlinks.
        say('REMOVE ' + str(path))
        run('sudo','rm','-rf','--one-file-system','--',path)


def clear_generated(c, first):
    """Never read prior config for serving. Preserve only keys and current cache."""
    for path in (c.native, c.active, c.native.with_suffix('.previous.yaml'),
                 c.cfg/'migration-complete.json', c.cfg/'seed-complete.json'):
        path.unlink(missing_ok=True)
    for path in c.cfg.glob('*.native.log'):
        path.unlink(missing_ok=True)
    # First seed re-extracts pinned binaries rather than trusting old installs.
    keep = {c.runtime.name,c.swap.name} if c.role == 'worker' else {c.venv.name}
    for path in c.bin.iterdir():
        if owned(path) and (first or path.name not in keep):
            safe_tree(path); remove_owned(path)
    for path in c.obsolete():
        safe_tree(path); say('REMOVE obsolete managed ' + str(path)); remove_owned(path)


def retire_gateway(c):
    label = c.c.get('legacy_launch_agent')
    if label and label != 'local.litellm': fail('Unexpected old gateway label')
    if label:
        domain = f'gui/{os.getuid()}'
        # GUI agent may have no current session: still remove its on-disk plist.
        if run('launchctl','print',domain+'/'+label,check=False,capture=True).returncode == 0:
            run('launchctl','bootout',domain+'/'+label)
        path = Path.home()/'Library/LaunchAgents'/f'{label}.plist'
        path.unlink(missing_ok=True)
        # Also handle a login agent loaded in the user domain.
        domain = f'user/{os.getuid()}'
        if run('launchctl','print',domain+'/'+label,check=False,capture=True).returncode == 0:
            run('launchctl','bootout',domain+'/'+label)
        old = Path.home()/'.local/share/uv/tools/litellm'
        if old.is_dir():
            safe_tree(old); run('uv','tool','uninstall','litellm')
    # Native Ollama remains the embedding runtime. Drop every unconfigured tag.
    if c.c.get('prune_native_models'):
        base = c.c['nodes'][c.a.node]['url'].rstrip('/').removesuffix('/v1')
        desired = {r['model'] for r in c.c['routes'].values() if r['node']==c.a.node}
        existing = {r['name'] for r in api(base+'/api/tags')['models']}
        for tag in sorted(existing-desired):
            say('REMOVE unused Mac model '+tag)
            api(base+'/api/generate',payload={'model':tag,'keep_alive':0},timeout=30) if tag in {r['name'] for r in api(base+'/api/ps')['models']} else None
            run('ollama','rm',tag,env=dict(os.environ,OLLAMA_HOST=base))
        if {r['name'] for r in api(base+'/api/tags')['models']} - desired:
            fail('Native model catalog still contains unused tags')


def deploy(c):
    c.prepare(); c.service_owned()
    first = not (c.cfg/'seed-complete.json').is_file() or getattr(c.a,'action','install') == 'seed'
    c.plan()
    # Authenticate and verify model sources before irreversible retirement.
    c.key('worker')
    if c.role=='gateway': c.key('gateway')
    dirs, files, units = cleanup_plan(c) if c.role=='worker' else ([],[],[])
    if c.role=='worker':
        for path in [c.root,c.cfg,c.bin,c.models,c.cache]: safe_tree(path)
        c._sources = {a: model_sources(m,c.store) for a,m in c.desired.items()}
    confirm('Retire old inference services/weights, deploy only YAML catalog; NO ROLLBACK?', c.a.yes)
    # Stop managed and old inference before touching any files. No rollback to
    # stale configurations, even if a download or native model startup fails.
    c.stop()
    # Prevent old generated service from being restarted on reboot after failure.
    if c.service_path().exists():
        if c.role=='worker': run('sudo','systemctl','disable',SERVICE)
        run('sudo','rm','-f','--',c.service_path())
        if c.role=='worker': run('sudo','systemctl','daemon-reload')
    clear_generated(c,first)
    if c.role=='worker':
        for unit in units: retire_unit(unit)
        # Preserve only exact hash matches before deleting legacy stores. No
        # network weights are downloaded in this adoption phase.
        c._adopt_only=True
        try:
            for alias,spec in c.desired.items(): c.sync_model(alias,spec)
        finally: c._adopt_only=False
        for path in dirs: remove_tree(path)
        for path in files:
            if path.is_relative_to(Path.home()): path.unlink(missing_ok=True)
            else: run('sudo','rm','-f','--',path)
        c.store=None
    else:
        retire_gateway(c)
    # New service is always rendered from the current repository. No old native
    # YAML remains that a failed run or subsequent --start could resurrect.
    try:
        c.sync(install=True)
    except BaseException:
        say('Seed incomplete; old assets retired. Correct YAML/runtime and rerun setup.')
        raise
    atomic(c.cfg/'seed-complete.json',json.dumps({'release':RELEASE,'completed':time.time(),'models':sorted(c.desired)}))
    say('Seed/reconcile complete. No legacy rollback.'); run('df','-h',Path.home(),check=False)
