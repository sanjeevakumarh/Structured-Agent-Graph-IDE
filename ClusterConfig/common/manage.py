#!/usr/bin/env python3
"""Shared deployment logic (not a daemon). Edit config/<node>.yaml, not this file.
Uses native systemd/launchd and llama-swap/LiteLLM configs. Downloads verified
release/model files into ~/inference; seed retires explicitly inventoried old stores.
Run a setup-<node>.sh --help for commands. Dry-run never calls service managers
or downloads model/runtime artifacts. The launcher may initialize uv's cache.
"""
import argparse
import contextlib
import datetime
import fcntl
import hashlib
import json
import math
import os
import platform
import plistlib
import re
import secrets
import shlex
import shutil
import socket
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

import yaml

REPO = Path(__file__).resolve().parents[1]
RELEASE = '0.11.1'
MARK = '.local-ai-cluster'
SERVICE = 'local-ai-inference.service'
LABEL = 'local.cluster.gateway'
SHA = re.compile(r'^[0-9a-f]{64}$')
SAFE = re.compile(r'^[A-Za-z0-9_.-]+$')


def valid_alias(alias, node):
    parts = alias.split('/')
    return len(parts) == 2 and parts[0] == node and bool(SAFE.fullmatch(parts[1])) and parts[1] not in ('.', '..')


def gateway_routes(config, profiles):
    """One model catalog per worker; disabled/offline nodes do not block startup."""
    from inventory import profiles as load_profiles, routes_for
    inventory = config['nodes']
    if isinstance(inventory, list):
        inventory = routes_for(config, load_profiles(profiles))
    routes = {}
    for node, n in inventory.items():
        if not n.get('enabled', False):
            continue
        url = n['url'].rstrip('/')
        if not url.endswith('/v1'):
            fail('Route URL must end in /v1: ' + node)
        if not SAFE.fullmatch(node):
            fail('Invalid node name: ' + node)
        profile = config if node == config['node'] else yaml.safe_load((profiles / (node + '.yaml')).read_text())
        if profile['role'] == 'worker':
            models = {a: {'model': a} for a, m in profile['models'].items() if m.get('enabled', True)}
            overlays = config.get('catalog_overlays', [])
            if not isinstance(overlays, list) or set(overlays) - {'abliterated'}:
                fail('Unknown optional language catalog overlay')
            for overlay in overlays:
                extra = yaml.safe_load((profiles / 'profiles' / (overlay + '.yaml')).read_text())
                for name in extra['models']:
                    alias = node + '/' + name
                    selected = any(alias in mode['models'] for mode in profile.get('modes', {}).values())
                    if not selected:
                        continue
                    if alias in models:
                        fail('Overlay must not replace standard alias: ' + alias)
                    models[alias] = {'model': alias}
        elif profile['role'] == 'gateway':
            models = config.get('local_models', {})
        elif profile['role'] == 'edge':
            models = {a: {'model': m['id']} for a, m in profile['edge_models'].items() if m.get('enabled', True)}
            if not models:
                fail('Enabled edge node requires explicit model IDs: ' + node)
        else:
            fail('Unknown gateway node: ' + node)
        for alias, model in models.items():
            if not valid_alias(alias, node):
                fail('Invalid server-qualified route: ' + alias)
            routes[alias] = dict(model, url=url, node=node, external=profile['role'] != 'worker')
    for alias, target in config.get('aliases', {}).items():
        if not SAFE.fullmatch(alias) or target not in routes or alias in routes:
            fail('Invalid optional alias: ' + alias)
        routes[alias] = dict(routes[target])
    if not routes:
        fail('No gateway routes enabled.')
    return routes


def flash_mode(value):
    # PyYAML 1.1 parses bare on/off as booleans.
    if isinstance(value, bool):
        return 'on' if value else 'off'
    if value not in ('on', 'off', 'auto'):
        fail('flash_attention must be on, off or auto')
    return value


def slot_count(spec):
    value = spec.get('parallel', 1)
    if isinstance(value, bool) or not isinstance(value, int) or not 1 <= value <= 8:
        fail('parallel must be an integer 1–8')
    return value


def model_groups(desired, groups):
    """Native llama-swap groups: co-resident members, exclusive across groups."""
    result, seen = {}, set()
    for i, members in enumerate(groups):
        if not isinstance(members, list) or len(members) < 2 or len(set(members)) != len(members):
            fail('resident_groups requires lists of distinct enabled aliases')
        if not set(members) <= set(desired) or seen.intersection(members):
            fail('Unknown or duplicate resident group model')
        seen.update(members)
        result[f'resident-{i}'] = {'swap': False, 'exclusive': True, 'members': members}
    for i, alias in enumerate(sorted(set(desired) - seen)):
        result[f'single-{i}'] = {'swap': True, 'exclusive': True, 'members': [alias]}
    return result


def offload_counts(logs):
    """Parse actual llama.cpp layer assignment, stripping terminal escape codes."""
    text = re.sub(r'\x1b\[[0-?]*[ -/]*[@-~]', '', logs)
    hits = re.findall(r'offloaded\s+(\d+)\s*/\s*(\d+)\s+layers\s+to\s+GPU', text, re.I)
    return tuple(map(int, hits[-1])) if hits else None


def model_command(server, folder, records, alias, spec, port, host='127.0.0.1'):
    """Identical model arguments for serving and isolated calibration."""
    main = [r['name'] for r in records if 'mmproj' not in r['name'].lower()]
    if not main:
        fail('No main model file: ' + alias)
    if len(main) > 1 or re.search(r'-\d{5}-of-\d{5}\.gguf$', main[0]):
        match = re.fullmatch(r'(.*)-00001-of-(\d{5})\.gguf', main[0])
        if not match or main != [f'{match[1]}-{i:05d}-of-{match[2]}.gguf' for i in range(1, int(match[2]) + 1)]:
            fail('Incomplete or unordered split GGUF: ' + alias)
    args = [str(server), '--model', str(folder / main[0]), '--host', host, '--port', str(port),
            '--alias', alias, '--n-gpu-layers', '999', '--ctx-size', str(int(spec['context']) * slot_count(spec)),
            '--parallel', str(slot_count(spec)), '--kv-unified', '--kv-unified-per-slot', str(int(spec['context'])),
            '--jinja', '--flash-attn', flash_mode(spec.get('flash_attention', 'auto')),
            '--no-context-shift', '--fit', 'off',
            '--cache-type-k', spec.get('cache_type_k', 'f16'), '--cache-type-v', spec.get('cache_type_v', 'f16')]
    proj = [r['name'] for r in records if 'mmproj' in r['name'].lower()]
    if proj:
        args += ['--mmproj', str(folder / proj[0])]
    # Keep validation evidence visible even with inherited logging defaults.
    extra = [str(x) for x in spec.get('args', [])]
    if '--log-disable' in extra:
        fail('Model logging cannot be disabled: GPU validation needs evidence.')
    return args + extra + ['--log-verbosity', '4', '--log-colors', 'off', '--no-log-jsonl']


def run(*args, check=True, capture=False, env=None):
    return subprocess.run([str(x) for x in args], check=check, text=True,
                          capture_output=capture, env=env)


def fail(message):
    raise RuntimeError(message)


def say(message):
    print(message, flush=True)


def confirm(message, yes=False):
    if yes:
        say(message + ' [--yes]')
        return
    if not sys.stdin.isatty() or input(message + ' [y/N] ').strip().lower() != 'y':
        fail('Not approved; no further changes made.')


def sha(path):
    h = hashlib.sha256()
    with path.open('rb') as f:
        for chunk in iter(lambda: f.read(8 * 1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()


def atomic(path, data, mode=0o600):
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=path.parent, prefix='.tmp-')
    try:
        os.fchmod(fd, mode)
        with os.fdopen(fd, 'wb') as f:
            f.write(data if isinstance(data, bytes) else data.encode())
        os.replace(tmp, path)
    finally:
        if os.path.exists(tmp):
            os.unlink(tmp)


def get_bytes(url, headers=None):
    req = urllib.request.Request(url, headers={'User-Agent': 'local-ai-cluster/' + RELEASE, **(headers or {})})
    with urllib.request.urlopen(req, timeout=30) as response:
        return response.read()


def get_json(url, headers=None):
    return json.loads(get_bytes(url, headers))


def http_error_detail(error, key=None):
    """Preserve HTTP status and a bounded server error, never the bearer key."""
    try:
        body = error.read(4096).decode('utf-8', errors='replace')
    except (OSError, ValueError):
        body = ''
    if key:
        body = body.replace(key, '[REDACTED]')
    return f'{error.reason}: {body}' if body else str(error.reason)


def api(url, key=None, payload=None, timeout=10):
    headers = {'Content-Type': 'application/json'}
    if key:
        headers['Authorization'] = 'Bearer ' + key
    req = urllib.request.Request(url, headers=headers,
                                 data=json.dumps(payload).encode() if payload is not None else None)
    try:
        # Cluster APIs are private/direct endpoints. Never send their bearer keys
        # via an inherited HTTP proxy (macOS can also inherit system proxies).
        with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(req, timeout=timeout) as r:
            return json.load(r)
    except urllib.error.HTTPError as e:
        detail = http_error_detail(e, key)
        # Keep status available to gateway offline/authentication handling.
        raise urllib.error.HTTPError(e.url, e.code, detail, e.headers, None) from None


class DegradedRoutes(RuntimeError):
    """Gateway discovery passed but a required backend is unavailable or stale."""


class DiskSpaceError(RuntimeError):
    """Insufficient storage for an explicitly requested artifact."""


def download(url, dest, digest, size=None, reserve=0):
    """Resume a partial file; publish only after SHA-256 and length validation."""
    if not SHA.fullmatch(digest):
        fail('Invalid or missing SHA-256 for ' + str(dest))
    if dest.is_file() and (size is None or dest.stat().st_size == size) and sha(dest) == digest:
        return
    part = dest.with_name(dest.name + '.partial')
    dest.parent.mkdir(parents=True, exist_ok=True)
    if dest.is_symlink() or part.is_symlink():
        fail('Symlink in download path: ' + str(dest))
    if part.is_file() and size is not None and part.stat().st_size >= size:
        if part.stat().st_size == size and sha(part) == digest:
            os.replace(part, dest); return
        part.unlink()
    needed = max(0, (size or 0) - (part.stat().st_size if part.exists() else 0))
    if shutil.disk_usage(dest.parent).free < needed + reserve:
        raise DiskSpaceError(f'Insufficient space for {dest.name} (including configured reserve).')
    if part.is_file() and (size is None or part.stat().st_size == size) and sha(part) == digest:
        os.replace(part, dest)
        return
    say(f'DOWNLOAD {dest.name}')
    run('curl', '--fail', '--location', '--retry', '3', '--connect-timeout', '20',
        '--continue-at', '-', '--output', part, url)
    if (size is not None and part.stat().st_size != size) or sha(part) != digest:
        part.unlink()
        fail('Download checksum/size mismatch: ' + dest.name)
    os.replace(part, dest)


def owned(path):
    marker = path / MARK
    return (path.is_dir() and not path.is_symlink() and marker.is_file()
            and not marker.is_symlink() and marker.read_text() == "local-ai-cluster\n")


def remove_owned(path):
    if not owned(path):
        fail('Refusing to remove unmarked directory: ' + str(path))
    shutil.rmtree(path)


def model_id(spec):
    # Ignore local reuse paths; the pinned source identifies a weight set.
    src = {k: v for k, v in spec['source'].items() if k != 'reuse'}
    label = src.get('ollama', src.get('repo', '')).split('/')[-1].replace(':', '-')
    label = re.sub(r'[^A-Za-z0-9_.-]', '-', label)[:65]
    return label + '-' + hashlib.sha256(json.dumps(src, sort_keys=True).encode()).hexdigest()[:12]


def model_sources(spec, store):
    """Return exact file names, sizes, hashes, URLs and local reuse candidates."""
    src = spec['source']
    if 'ollama' in src:
        fail('Worker models require pinned upstream Hugging Face GGUFs; raw Ollama layers are unsupported.')
    repo, rev = src['repo'], src['revision']
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repo) or not re.fullmatch(r'[0-9a-f]{40}', rev):
        fail('Hugging Face source requires owner/repo and full commit hash.')
    info = get_json(f'https://huggingface.co/api/models/{repo}/revision/{rev}?blobs=true')
    if info.get('sha') != rev:
        fail('Unexpected Hugging Face revision.')
    siblings = {x['rfilename']: x for x in info['siblings']}
    result = []
    for filename in src['files']:
        p = Path(filename)
        if p.is_absolute() or '..' in p.parts or not filename.endswith('.gguf'):
            fail('Invalid GGUF path: ' + filename)
        meta = siblings.get(filename, {}).get('lfs', {})
        dg, size = meta.get('sha256'), meta.get('size')
        if not dg or not size or not SHA.fullmatch(dg):
            fail('No pinned LFS hash/size for ' + filename)
        candidates = [store / 'blobs' / ('sha256-' + dg)] if store else []
        if src.get('reuse'):
            candidates.insert(0, Path(src['reuse']).expanduser() / filename)
        result.append((filename, int(size), dg,
                       f'https://huggingface.co/{repo}/resolve/{rev}/{urllib.parse.quote(filename)}', candidates))
    if not result:
        fail('Model files list cannot be empty.')
    return result


def check_gguf(path):
    with path.open('rb') as f:
        if f.read(4) != b'GGUF':
            fail('Not a GGUF file: ' + str(path))


class Cluster:
    def __init__(self, args):
        self.a = args
        self.c = yaml.safe_load((REPO / 'config' / f'{args.node}.yaml').read_text())
        self.v = yaml.safe_load((REPO / 'config/versions.yaml').read_text())
        self.root = Path.home() / 'inference'
        self.role = self.c['role']
        self.cfg = self.root / 'config'
        self.cache = self.root / 'cache'
        self.models = self.root / 'models'
        self.bin = self.root / 'bin'
        self.backend = args.backend or self.c.get('backend', '')
        self.runtime = self.bin / f'llama-{self.v["llama_cpp"]}-{self.backend}'
        self.swap = self.bin / f'llama-swap-{self.v["llama_swap"]}'
        self.venv = self.bin / f'litellm-{self.v["litellm"]}'
        self.active = self.cfg / 'active.json'
        self.native = self.cfg / ('litellm.yaml' if self.role == 'gateway' else 'llama-swap.yaml')
        self.desired = {k: v for k, v in self.c.get('models', {}).items() if v.get('enabled', True)}
        self.store = Path(self.c['ollama_store']).expanduser() if self.c.get('ollama_store') else None
        if self.c['node'] != args.node:
            fail('Profile node mismatch.')
        if any(not valid_alias(k, args.node) for k in self.c.get('models', {})):
            fail('Model IDs must be node/model-name with safe characters.')
        if self.role == 'gateway':
            from inventory import profiles, routes_for
            self.c['nodes'] = routes_for(self.c, profiles())
            self.c['routes'] = gateway_routes(self.c, REPO / 'config')
        if self.role == 'worker' and self.backend not in ('vulkan', 'cuda', 'cuda-native', 'rocm', 'sycl'):
            fail('Unsupported backend: ' + self.backend)
        if self.role == 'worker' and self.backend not in self.c['hardware']['backends']:
            fail('Backend not supported by this node profile.')
        if self.role == 'worker':
            model_groups(self.desired, self.c.get('resident_groups', []))
            for spec in self.desired.values():
                slot_count(spec); flash_mode(spec.get('flash_attention', 'auto'))
        if self.role == 'worker' and not self.desired:
            fail('Enable at least one worker model.')
        self.gateway_tailnet = self.role == 'gateway' and self.c.get('bind') == 'tailnet'
        self.public_endpoint = None
        if self.role != 'edge':
            if self.c['bind'] == 'tailnet':
                ip = 'tailnet'
                if not getattr(args, 'dry_run', False):
                    ip = self.tailscale('ip', '-4', capture=True).stdout.strip()
                    import ipaddress
                    if ipaddress.ip_address(ip) not in ipaddress.ip_network('100.64.0.0/10'):
                        fail('Unexpected tailnet IPv4 address')
                if self.gateway_tailnet:
                    # macOS GUI Tailscale may not route self-addressed connections.
                    # Serve exposes the authenticated local API only to the tailnet.
                    self.public_endpoint = f'http://{ip}:{self.c["port"]}'
                    self.c['bind'] = '127.0.0.1'
                else:
                    self.c['bind'] = ip
            self.endpoint = f'http://{self.c["bind"]}:{self.c["port"]}'
        else:
            self.endpoint = self.c['endpoint']

    def tailscale(self, *args, capture=False):
        cli = shutil.which('tailscale') or '/Applications/Tailscale.app/Contents/MacOS/Tailscale'
        return run(cli, *args, capture=capture, env=dict(os.environ, TAILSCALE_BE_CLI='1'))

    def gateway_serve_config(self):
        data = json.loads(self.tailscale('serve', 'status', '--json', capture=True).stdout)
        if data is None:
            return {}
        if not isinstance(data, dict):
            fail('Unexpected Tailscale Serve status format.')
        return data

    def gateway_forward(self, port):
        """Check the requested port only; never replace unrelated Serve/Funnel rules."""
        status = self.gateway_serve_config()
        configs = [status, *(status.get('Foreground') or {}).values()]
        target = f'127.0.0.1:{port}'
        found = None
        for i, config in enumerate(configs):
            for address, enabled in (config.get('AllowFunnel') or {}).items():
                if enabled and address.rsplit(':', 1)[-1] == str(port):
                    fail(f'Tailscale Funnel enabled on port {port}; refusing public exposure.')
            handler = (config.get('TCP') or {}).get(str(port))
            if handler is None:
                continue
            if i or handler != {'TCPForward': target}:
                # Zero/default optional fields can appear in status JSON.
                if i or handler.get('TCPForward') != target or any(v for k,v in handler.items() if k != 'TCPForward'):
                    fail(f'Tailscale Serve port {port} already has a different handler.')
            found = handler
        return found

    def configure_gateway_serve(self):
        if not self.gateway_tailnet:
            return
        port = int(self.c['port'])
        marker = self.cfg / 'gateway-serve.json'
        existing = self.gateway_forward(port)
        # Record the managed port before making a change, so failure cleanup
        # can remove a partially installed forward without touching other ports.
        atomic(marker, json.dumps({'port':port}))
        if not existing:
            self.tailscale('serve', '--bg', f'--tcp={port}', f'tcp://127.0.0.1:{port}')
        if not self.gateway_forward(port):
            fail('Tailscale Serve did not install the expected private TCP forward.')
        say('Tailnet gateway configured: ' + self.public_endpoint + '/v1 (remote reachability requires a peer test).')

    def remove_gateway_serve(self):
        marker = self.cfg / 'gateway-serve.json'
        if not marker.is_file():
            return
        port = int(json.loads(marker.read_text())['port'])
        if not 1 <= port <= 65535:
            fail('Invalid managed Tailscale Serve port.')
        if self.gateway_forward(port):
            self.tailscale('serve', f'--tcp={port}', 'off')
        marker.unlink()

    def host_check(self):
        if os.geteuid() == 0:
            fail('Run as normal login user, not root.')
        if self.role == 'gateway' and platform.system() != 'Darwin':
            fail('Mac Mini profile requires macOS.')
        if self.role != 'gateway' and platform.system() != 'Linux':
            fail('This profile requires Linux.')
        if self.role == 'worker' and platform.machine() != 'x86_64':
            fail('This worker profile requires x86_64.')
        # Guard against running a hardware profile on the wrong workstation.
        if self.role == 'worker':
            vendor = self.c['hardware']['vendor']
            cards = list(Path('/sys/class/drm').glob('card*/device/vendor'))
            pci = self.c['hardware'].get('pci_device')
            if not any(x.read_text().strip() == vendor and (not pci or (x.parent / 'device').read_text().strip() == pci) for x in cards if x.is_file()):
                fail(f'Expected GPU vendor {vendor} not visible; install/verify vendor driver first.')

    def check_layout(self):
        for p in (self.root, self.cfg, self.cache, self.models, self.bin, self.root / 'benchmarks', self.root / 'logs'):
            if p.is_symlink():
                fail('Managed path must not be a symlink: ' + str(p))

    def prepare(self):
        self.host_check()
        self.check_layout()
        # Only owned files under these directories are removed. Existing ~/inference is allowed.
        for p in (self.root, self.cfg, self.cache, self.models, self.bin, self.root / 'benchmarks'):
            p.mkdir(parents=True, exist_ok=True)
        self.cfg.chmod(0o700)
        # Prevent overwriting unowned configurations from previous experiments.
        if not owned(self.cfg):
            if (self.cfg / MARK).exists() or self.native.exists() or self.active.exists():
                fail('Existing configuration is unowned; back it up and move it before adoption.')
            atomic(self.cfg / MARK, 'local-ai-cluster\n')

    def key(self, name):
        p = self.cfg / (name + '.key')
        if not p.is_file() or p.is_symlink() or p.stat().st_mode & 0o077:
            fail(f'Create/copy {p} with chmod 600 (see README).')
        value = p.read_text().strip()
        if not re.fullmatch(r'sk-[A-Za-z0-9_-]{32,}', value):
            fail('Invalid key format: ' + str(p))
        return value

    def init_keys(self):
        if self.role != 'gateway':
            fail('Generate keys on Mac Mini only.')
        self.prepare()
        for n in ('worker', 'gateway'):
            path = self.cfg / (n + '.key')
            if not path.exists():
                atomic(path, 'sk-' + secrets.token_urlsafe(36) + '\n')
            self.key(n)
            say('Key ready: ' + str(path))

    def plan(self, migration=False):
        say(f'{self.a.node}: {self.role}; {self.endpoint}; root={self.root}')
        if self.public_endpoint:
            say('Tailnet exposure: ' + self.public_endpoint + '/v1 via Tailscale Serve')
        if self.role == 'worker':
            say(f'Runtime: {self.v["llama_cpp"]} {self.backend}; llama-swap {self.v["llama_swap"]}')
            for alias, m in self.desired.items():
                folder = self.models / model_id(m)
                say(f'{"KEEP" if owned(folder) and (folder / "weights.json").exists() else "SYNC"} {alias}: {m["source"]} (ctx {m["context"]})')
            for p in self.obsolete():
                say('PRUNE CANDIDATE ' + str(p))
            say('Legacy inference services/stores will be retired before downloads.')
        elif self.role == 'gateway':
            say(f'LiteLLM {self.v["litellm"]}; routes:')
            for k, v in self.c['routes'].items():
                say(f'  {k} -> {v["url"]} / {v["model"]}')
        else:
            say(f'Runtime: {self.c["runtime"]}; desired models:')
            for alias, spec in self.c['edge_models'].items():
                if spec.get('enabled', True): say(f'  {alias}: {spec["id"]}')
            say('Explicit legacy retirement: ' + str(self.c.get('retire_models', [])))
        if getattr(self.a, 'action', 'install') in ('seed', 'migrate', 'install', 'models', 'start', 'reset'):
            say('Destructive seed/reconcile: remove old inference assets; no rollback.')
            for item in self.c.get('legacy_cleanup', {}).get('directories', []):
                say('  REMOVE legacy directory: ' + item)
            for item in self.c.get('legacy_cleanup', {}).get('services', []):
                say('  RETIRE legacy service: ' + item)
            if self.role in ('gateway', 'edge'):
                say('  Remove unused native model tags as configured in YAML.')
        say('No partition, boot, driver or unrelated application changes.')

    def require_single_profile(self):
        if (self.cfg / 'modes-enabled.json').exists():
            fail('Optional modes preserve all installed profiles. Legacy seed/cleanup/reset is blocked; use common/modes.py. Do not remove the marker to bypass retention.')

    def obsolete(self):
        # Optional profiles retain every installed model, including disabled ones.
        if (self.cfg / 'modes-enabled.json').exists():
            return []
        keep = {model_id(m) for m in self.desired.values()}
        return sorted(p for p in self.models.glob('*') if owned(p) and p.name not in keep)

    def locate(self, folder, binary):
        hits = [p for p in folder.rglob(binary) if p.is_file() and os.access(p, os.X_OK)]
        if len(hits) != 1:
            fail(f'Expected exactly one executable {binary} in {folder}; found {len(hits)}.')
        return hits[0]

    def asset(self, project, tag, filename, digest, size=None):
        if not SAFE.fullmatch(filename) or not SAFE.fullmatch(tag):
            fail('Unsafe release name.')
        dest = self.cache / filename
        download(f'https://github.com/{project}/releases/download/{tag}/{filename}', dest, digest, size)
        return dest

    def install_archive(self, target, archives, binary):
        if target.exists():
            if not owned(target):
                fail('Existing runtime is not managed: ' + str(target))
            return self.locate(target, binary)
        with tempfile.TemporaryDirectory(prefix='.stage-', dir=self.bin) as tmp:
            stage = Path(tmp) / 'runtime'
            stage.mkdir()
            for a in archives:
                with tarfile.open(a, 'r:*') as tf:
                    if not hasattr(tarfile, 'data_filter'):
                        fail('Python lacks safe tar extraction; update Python or use uv Python 3.12.')
                    tf.extractall(stage, filter='data')
            self.locate(stage, binary)
            atomic(stage / MARK, 'local-ai-cluster\n')
            stage.rename(target)
        return self.locate(target, binary)

    def native_build(self):
        if owned(self.runtime):
            return self.locate(self.runtime, 'llama-server')
        if self.runtime.exists():
            fail('Unowned runtime: ' + str(self.runtime))
        for tool in ('git', 'cmake', 'c++'):
            if not shutil.which(tool):
                fail(f'Missing {tool}; install build-essential cmake git before native build.')
        if self.backend == 'cuda-native':
            nvcc = shutil.which('nvcc') or '/usr/local/cuda/bin/nvcc'
            if not Path(nvcc).is_file():
                fail('CUDA toolkit nvcc missing. Install a compatible vendor CUDA toolkit (not driver bundle).')
            flags = ['-DGGML_CUDA=ON', '-DCMAKE_CUDA_ARCHITECTURES=' + str(self.c['hardware']['cuda_arch']), '-DCMAKE_CUDA_COMPILER=' + nvcc]
        else:
            hip = Path('/opt/rocm/bin/hipcc')
            if not hip.is_file():
                fail('ROCm toolkit missing; install vendor-supported ROCm for gfx1151 first.')
            flags = ['-DGGML_HIP=ON', '-DGPU_TARGETS=' + self.c['hardware']['rocm_target'], '-DCMAKE_PREFIX_PATH=/opt/rocm',
                     '-DCMAKE_HIP_COMPILER=/opt/rocm/llvm/bin/clang++']
        with tempfile.TemporaryDirectory(prefix='.build-', dir=self.cache) as tmp:
            source = Path(tmp) / 'src'
            build = Path(tmp) / 'build'
            run('git', 'clone', '--depth', '1', '--branch', self.v['llama_cpp'], 'https://github.com/ggml-org/llama.cpp.git', source)
            run('cmake', '-S', source, '-B', build, '-DCMAKE_BUILD_TYPE=Release', '-DGGML_NATIVE=ON',
                '-DLLAMA_BUILD_TESTS=OFF', '-DLLAMA_BUILD_EXAMPLES=ON', '-DLLAMA_BUILD_SERVER=ON', *flags)
            run('cmake', '--build', build, '--config', 'Release', '--parallel', str(min(os.cpu_count() or 4, 12)),
                '--target', 'llama-server', 'llama-bench')
            # Copy colocated shared libraries and programs as a directory; retain relative symlinks.
            stage = self.bin / (self.runtime.name + '.stage')
            if stage.exists():
                fail('Inspect and remove interrupted build stage manually: ' + str(stage))
            shutil.copytree(build / 'bin', stage, symlinks=True)
            atomic(stage / MARK, 'local-ai-cluster\n')
            stage.rename(self.runtime)
        return self.locate(self.runtime, 'llama-server')

    def runtime_env(self):
        # CUDA runtime and executable archives may have different top-level folders.
        # Keep shared libraries with their pinned binary, not on the system library path.
        dirs = sorted({str(p.parent) for p in self.runtime.rglob('*.so*') if p.is_file()})
        if self.backend == 'rocm':
            dirs.extend(['/opt/rocm/lib', '/opt/rocm/lib64'])
        env = dict(os.environ)
        env.pop('LD_LIBRARY_PATH', None)
        if dirs:
            env['LD_LIBRARY_PATH'] = ':'.join(dirs)
        return env

    def install_worker(self):
        if self.backend in ('cuda-native', 'rocm'):
            server = self.native_build()
        elif owned(self.runtime):
            server = self.locate(self.runtime, 'llama-server')
        else:
            archives = [self.asset('ggml-org/llama.cpp', self.v['llama_cpp'], a['file'], a['sha256'])
                        for a in self.v['assets'][self.backend]]
            server = self.install_archive(self.runtime, archives, 'llama-server')
        try:
            run(server, '--version', env=self.runtime_env())
            r = run(server, '--list-devices', capture=True, env=self.runtime_env())
            say(r.stdout + r.stderr)
            # CPU-only detection is not acceptable for a GPU profile.
            expect = self.c['hardware']['device_regex']
            if not re.search(expect, r.stdout + r.stderr, re.I):
                fail('Expected GPU missing from llama.cpp device list.')
        except (subprocess.CalledProcessError, OSError) as e:
            fail(f'Runtime failed ({e}); check driver/library compatibility and the backends listed in the node profile.')
        if not owned(self.swap):
            tag = self.v['llama_swap']
            metadata = self.cache / f'llama-swap-{tag}-release.json'
            if metadata.is_file():
                release = json.loads(metadata.read_text())
            else:
                release = get_json(f'https://api.github.com/repos/mostlygeek/llama-swap/releases/tags/{tag}')
                atomic(metadata, json.dumps(release))
            hits = [a for a in release['assets'] if re.search(r'linux[-_]amd64|linux[-_]x86_64', a['name'], re.I) and a['name'].endswith(('.tar.gz', '.tgz'))]
            if len(hits) != 1:
                fail('Could not identify one Linux amd64 llama-swap release archive; inspect release assets.')
            a = hits[0]
            digest = (a.get('digest') or '').removeprefix('sha256:')
            archive = self.asset('mostlygeek/llama-swap', tag, a['name'], digest, a['size'])
            self.install_archive(self.swap, [archive], 'llama-swap')
        run(self.locate(self.swap, 'llama-swap'), '--help', check=False, capture=True)

    def sync_model(self, alias, spec):
        folder = self.models / model_id(spec)
        receipt = folder / 'weights.json'
        if folder.exists() and not owned(folder):
            fail('Model directory unowned: ' + str(folder))
        # Seed verifies every existing weight against the pinned upstream manifest.
        verified = getattr(self, '_verified', {})
        if alias in verified:
            return verified[alias]
        sources = getattr(self, '_sources', {}).get(alias)
        if sources is None:
            sources = model_sources(spec, self.store)
        expected = [(name, size, digest) for name, size, digest, _, _ in sources]
        if not folder.exists():
            folder.mkdir()
            atomic(folder / MARK, 'local-ai-cluster\n')
        records = []
        for filename, size, digest, url, candidates in sources:
            dest = folder / filename
            dest.parent.mkdir(parents=True, exist_ok=True)
            if dest.is_symlink() or any(x.is_symlink() for x in dest.parents if x.is_relative_to(folder)):
                fail('Refusing symlink in managed weight path: ' + str(dest))
            good = dest.is_file() and dest.stat().st_size == size and sha(dest) == digest
            if not good:
                if dest.exists():
                    if not dest.is_file(): fail('Unexpected managed weight path: ' + str(dest))
                    say('REMOVE incompatible or corrupt weight ' + str(dest)); dest.unlink()
                for source in candidates:
                    try:
                        if source.is_symlink() or not source.is_file() or source.stat().st_size != size or sha(source) != digest:
                            continue
                        check_gguf(source)
                        os.link(source, dest)
                        say('REUSE exact upstream hash ' + str(source))
                        break
                    except OSError:
                        continue
                else:
                    if getattr(self, '_adopt_only', False):
                        continue
                    download(url, dest, digest, size, self.c.get('reserve_gib', 15) * 2**30)
            check_gguf(dest)
            records.append({'name': filename, 'size': size, 'sha256': digest, 'mtime': dest.stat().st_mtime_ns})
        if len(records) == len(expected):
            atomic(receipt, json.dumps(records, indent=2))
            say('VERIFIED ' + alias)
            self._verified = getattr(self, '_verified', {})
            self._verified[alias] = (folder, records)
        elif not getattr(self, '_adopt_only', False):
            fail('Incomplete model: ' + alias)
        return folder, records

    def render_worker(self):
        server = self.locate(self.runtime, 'llama-server')
        key = self.key('worker')
        # v277 defaults to proxy-only logging: GPU offload evidence lives upstream.
        config = {'healthCheckTimeout': 600, 'logLevel': 'info', 'logToStdout': 'both',
                  'captureBuffer': 0, 'apiKeys': [key], 'startPort': 18081, 'models': {},
                  'performance': {'disabled': bool(self.c.get('disable_swap_monitor', False))}}
        active = []
        for alias, spec in self.desired.items():
            folder, rec = self.sync_model(alias, spec)
            active.append(folder.name)
            args = model_command(server, folder, rec, alias, spec, '${PORT}') + ['--log-file', str(self.model_log(alias))]
            libs = self.runtime_env().get('LD_LIBRARY_PATH')
            if libs:
                args = ['/usr/bin/env', 'LD_LIBRARY_PATH=' + libs, *args]
            config['models'][alias] = {'cmd': shlex.join(args).replace("'${PORT}'", '${PORT}'),
                                       'ttl': int(spec.get('ttl', 600)), 'checkEndpoint': '/health'}
        if self.c.get('resident_groups'):
            config['routing'] = {'router': {'use': 'group', 'settings': {'groups': model_groups(self.desired, self.c['resident_groups'])}}}
        return config, active

    def install_gateway(self):
        self.key('gateway')
        self.key('worker')
        if owned(self.venv) and (self.venv / 'bin/litellm').is_file():
            return
        if self.venv.exists():
            fail('Existing unowned/incomplete LiteLLM environment; inspect before retrying.')
        if not shutil.which('uv'):
            fail('Install uv first (brew install uv).')
        # venv must be created at its final path; scripts embed absolute paths.
        run('uv', 'venv', '--python', self.v['python'], self.venv)
        atomic(self.venv / MARK, 'local-ai-cluster\n')
        try:
            run('uv', 'pip', 'install', '--python', self.venv / 'bin/python', f'litellm[proxy]=={self.v["litellm"]}')
        except BaseException:
            remove_owned(self.venv)
            raise

    def apply_routes(self):
        """Apply Mac routing YAML using only the installed local gateway."""
        if self.role != 'gateway' or not self.service_path().is_file() or not self.native.is_file():
            fail('Apply routes on an installed Mac gateway.')
        self.service_owned()
        old = self.native.read_bytes()
        new = self.render_gateway()
        if yaml.safe_load(old) == new:
            self.start()
            return
        loaded = self.gateway_loaded(privileged=True)
        self.gateway_bootout()
        try:
            atomic(self.native, yaml.safe_dump(new, sort_keys=False))
            self.start()
        except BaseException:
            self.gateway_bootout()
            atomic(self.native, old)
            if loaded:
                self.start()
            raise

    def render_gateway(self):
        routes = []
        for alias, r in self.c['routes'].items():
            is_embedding = r.get('embedding', False)
            params = {'model': 'openai/' + r['model'], 'api_base': r['url'].rstrip('/') + ('/v1' if is_embedding and not r['url'].endswith('/v1') else '')}
            params['api_key'] = 'local-external' if r.get('external') else self.key('worker')
            item = {'model_name': alias, 'litellm_params': params}
            if is_embedding:
                item['model_info'] = {'mode': 'embedding'}
            routes.append(item)
        return {'model_list': routes, 'router_settings': {'num_retries': 0},
                'general_settings': {'master_key': self.key('gateway')},
                'litellm_settings': {'request_timeout': 1800, 'drop_params': False}}

    def service_path(self):
        return Path('/Library/LaunchDaemons/' + LABEL + '.plist') if self.role == 'gateway' else Path('/etc/systemd/system/' + SERVICE)

    def service_active(self):
        if self.role == 'edge':
            return False
        if self.role == 'gateway':
            return self.gateway_loaded()
        return run('systemctl', 'is-active', '--quiet', SERVICE, check=False, capture=True).returncode == 0

    def legacy_exists(self):
        name = self.c.get('legacy_service')
        return bool(name and run('systemctl', 'show', '-p', 'LoadState', '--value', name, check=False, capture=True).stdout.strip() == 'loaded')

    def legacy_active(self):
        if self.role != 'worker': return False
        units = self.c.get('legacy_cleanup', {}).get('services', [])
        return any(run('systemctl','is-active','--quiet',unit,check=False,capture=True).returncode == 0 for unit in units)

    def legacy_unit(self):
        expected = self.c.get('legacy_service')
        if expected and (not SAFE.fullmatch(expected) or not expected.endswith('.service') or expected == SERVICE):
            fail('Invalid legacy service; inspect node YAML.')
        return expected

    def migration_pending(self):
        return self.role != 'edge' and (not (self.cfg / 'seed-complete.json').is_file() or self.legacy_active())

    @contextlib.contextmanager
    def isolated_gpu(self):
        """Pause only the current managed service for benchmarks; never restore legacy."""
        if self.legacy_active():
            fail('Legacy service is active; run seed setup before calibration.')
        managed = self.service_active()
        try:
            if managed:
                say('PAUSE ' + SERVICE); self.stop()
            if self.service_active(): fail('Could not isolate managed GPU service.')
            yield
        finally:
            if managed:
                say('RESUME current managed service')
                run('sudo', 'systemctl', 'start', SERVICE)

    def reconcile(self, install=True):
        self.require_single_profile()
        from seed import deploy
        return deploy(self)

    def gateway_loaded(self, privileged=False):
        command = ['launchctl', 'print', 'system/' + LABEL]
        if privileged:
            command.insert(0, 'sudo')
        return run(*command, check=False, capture=True).returncode == 0

    def gateway_bootout(self):
        """Wait for launchd to remove our job before replacing its plist."""
        if not self.gateway_loaded(privileged=True):
            return
        result = run('sudo', 'launchctl', 'bootout', 'system/' + LABEL,
                     check=False, capture=True)
        for _ in range(20):
            if not self.gateway_loaded(privileged=True):
                return
            time.sleep(0.5)
        fail('Managed gateway remains loaded after bootout: ' + (result.stderr or result.stdout).strip())

    def gateway_bootstrap(self):
        """Bootstrap only an unloaded job; tolerate a concurrently loaded own job."""
        self.service_owned()
        if self.gateway_loaded(privileged=True):
            return
        run('plutil', '-lint', self.service_path())
        run('sudo', 'launchctl', 'enable', 'system/' + LABEL)
        for attempt in range(2):
            result = run('sudo', 'launchctl', 'bootstrap', 'system', self.service_path(),
                         check=False, capture=True)
            if result.returncode == 0 or self.gateway_loaded(privileged=True):
                return
            if attempt == 0:
                time.sleep(2)
                if self.gateway_loaded(privileged=True):
                    return
        fail(f'launchd bootstrap failed ({result.returncode}): ' + (result.stderr or result.stdout).strip())

    def gateway_failure_log(self, error):
        """Preserve original failure and local diagnostics."""
        try:
            folder = self.root / 'benchmarks'
            folder.mkdir(exist_ok=True)
            path = folder / f'gateway-failure-{time.strftime("%Y%m%d-%H%M%S")}-{secrets.token_hex(2)}.log'
            status = run('launchctl', 'print', 'system/' + LABEL, check=False, capture=True)
            log = self.root / 'logs/gateway.log'
            tail = ''
            if log.is_file():
                with log.open('rb') as f:
                    f.seek(max(0, log.stat().st_size - 32768))
                    tail = f.read(32768).decode('utf-8', errors='replace')
            text = f'Cluster release {RELEASE}\nOriginal failure: {error}\n\n{status.stdout}\n{status.stderr}\n{tail}'
            for name in ('gateway', 'worker'):
                with contextlib.suppress(OSError, RuntimeError):
                    text = text.replace(self.key(name), '[REDACTED]')
            atomic(path, text, mode=0o600)
            say('Gateway diagnostics (local, review before sharing): ' + str(path))
        except Exception as e:
            say('Could not save gateway diagnostics: ' + str(e))

    def stop(self):
        if self.role == 'edge':
            fail('Edge service is vendor-managed; no stop action.')
        self.service_owned()
        if self.role == 'gateway':
            self.gateway_bootout()
            self.remove_gateway_serve()
        elif self.service_path().exists():
            run('sudo', 'systemctl', 'stop', SERVICE)

    def service_owned(self):
        p = self.service_path()
        if p.exists() and 'local-ai-cluster' not in p.read_text():
            fail('Existing service name is not owned by this project: ' + str(p))

    def install_service(self):
        self.service_owned()
        if self.role == 'gateway':
            command = [str(self.venv / 'bin/litellm'), '--config', str(self.native), '--host', self.c['bind'], '--port', str(self.c['port'])]
            logs = self.root / 'logs'
            logs.mkdir(exist_ok=True)
            plist = {'Label': LABEL, 'ProgramArguments': command, 'UserName': os.environ.get('USER') or run('id', '-un', capture=True).stdout.strip(),
                     'WorkingDirectory': str(self.root), 'RunAtLoad': True, 'KeepAlive': True,
                     'EnvironmentVariables': {'HOME': str(Path.home()), 'PATH': '/opt/homebrew/bin:/usr/bin:/bin',
                                              'NO_PROXY': '*', 'no_proxy': '*'},
                     'StandardOutPath': str(logs / 'gateway.log'), 'StandardErrorPath': str(logs / 'gateway.log'),
                     'ProcessType': 'Background', 'ThrottleInterval': 10, 'Comment': 'local-ai-cluster'}
            data = plistlib.dumps(plist)
        else:
            command = [str(self.locate(self.swap, 'llama-swap')), '-config', str(self.native), '-listen', f'{self.c["bind"]}:{self.c["port"]}']
            # Systemd is not a shell; restrict paths used in unit to avoid specifier expansion.
            if any(re.search(r'[\n\r%"\\]', x) for x in command):
                fail('Unsupported special character in systemd command path.')
            cmd = ' '.join('"' + x + '"' for x in command)
            user = run('id', '-un', capture=True).stdout.strip()
            data = f'''# local-ai-cluster\n[Unit]\nDescription=Local AI inference (llama-swap)\nAfter=network-online.target tailscaled.service\nWants=network-online.target\n\n[Service]\nUser={user}\nWorkingDirectory={self.root}\nExecStart={cmd}\nRestart=on-failure\nRestartSec=10\nTimeoutStopSec=120\nNoNewPrivileges=true\nPrivateTmp=true\nProtectSystem=strict\nReadWritePaths="{self.root}"\nEnvironment="XDG_CACHE_HOME={self.cache}"\n\n[Install]\nWantedBy=multi-user.target\n'''
        with tempfile.TemporaryDirectory() as tmp:
            p = Path(tmp) / self.service_path().name
            p.write_bytes(data if isinstance(data, bytes) else data.encode())
            if self.role == 'gateway':
                run('plutil', '-lint', p)
                run('sudo', 'install', '-o', 'root', '-g', 'wheel', '-m', '0644', p, self.service_path())
            else:
                run('sudo', 'install', '-m', '0644', p, self.service_path())
        if self.role == 'worker':
            run('sudo', 'systemctl', 'daemon-reload')

    def start(self):
        self.host_check()
        self.service_owned()
        if self.role == 'edge':
            fail('Edge service is vendor-managed.')
        if not self.service_path().exists() or not self.native.exists() or not self.active.exists():
            fail('Run --install first.')
        state = json.loads(self.active.read_text())
        if state.get('endpoint') != self.endpoint:
            fail('Endpoint changed in YAML; run --install to regenerate the service first.')
        if self.legacy_active():
            fail('Legacy GPU service became active during cutover; rerun setup to reconcile.')
        loaded = self.gateway_loaded(privileged=True) if self.role == 'gateway' else self.service_active()
        if not loaded:
            # A stopped launchd job can leave children briefly holding its port.
            attempts = 15
            for attempt in range(attempts):
                with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
                    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                    try:
                        s.bind((self.c['bind'], int(self.c['port'])))
                        break
                    except OSError as e:
                        if e.errno not in (98, 48):
                            raise
                        if attempt + 1 == attempts:
                            if shutil.which('ss'):
                                run('ss', '-ltnp', f'sport = :{self.c["port"]}', check=False)
                            elif shutil.which('lsof'):
                                run('lsof', '-nP', f'-iTCP:{self.c["port"]}', '-sTCP:LISTEN', check=False)
                            fail(f'Port {self.c["port"]} already in use on {self.c["bind"]}; not stopping an unknown listener.')
                time.sleep(1)
        if self.role == 'gateway':
            self.gateway_bootstrap()
        else:
            run('sudo', 'systemctl', 'enable', '--now', SERVICE)
        self.wait_ready()

    def wait_ready(self, endpoint=None):
        endpoint = endpoint or self.endpoint
        key = self.key('gateway' if self.role == 'gateway' else 'worker')
        last_error = None
        for _ in range(30):
            try:
                api(endpoint + '/v1/models', key, timeout=2)
                say('API reachable: ' + endpoint + '/v1; inference validation pending.')
                return
            except urllib.error.HTTPError as e:
                if e.code in (401, 403):
                    fail(f'Service authentication failed at {endpoint}: HTTP {e.code}')
                last_error = e
            except (OSError, ValueError) as e:
                last_error = e
            time.sleep(2)
        fail(f'Service did not return a model list at {endpoint}: {last_error}; inspect native logs.')

    def sync(self, install=False, stage_only=False):
        """Generate only current YAML. No backups, no legacy fallback."""
        self.prepare(); self.service_owned()
        if self.role == 'gateway':
            self.pull_ollama(); self.install_gateway()
            config, active = self.render_gateway(), []
        else:
            if install or not owned(self.runtime) or not owned(self.swap): self.install_worker()
            config, active = self.render_worker()
        native_text = yaml.safe_dump(config, sort_keys=False)
        state_text = json.dumps({'service_schema': 11, 'models': active, 'backend': self.backend,
                                 'endpoint': self.endpoint, 'runtimes': [self.runtime.name, self.swap.name] if self.role == 'worker' else [self.venv.name]}, indent=2)
        since = datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S.%f')
        try:
            if self.service_active(): self.stop()
            self.native.with_suffix('.previous.yaml').unlink(missing_ok=True)
            atomic(self.native, native_text); atomic(self.active, state_text)
            self.install_service(); self.start()
            if self.role == 'worker': self.smoke_all()
            else:
                self.validate_gateway()
                self.configure_gateway_serve()
        except BaseException as error:
            say('Deployment FAILED (no rollback): ' + str(error))
            if self.role == 'gateway': self.gateway_failure_log(error)
            else: self.save_failure_log(since, error)
            # Do not leave a failed replacement serving misleading model lists.
            with contextlib.suppress(Exception): self.stop()
            if self.role == 'worker':
                with contextlib.suppress(Exception): run('sudo','systemctl','disable',SERVICE)
            raise


    def pull_ollama(self):
        # One desired catalog: derive tags from local routes, no duplicated list.
        models = {r['model'] for r in self.c['routes'].values() if r['node'] == self.a.node}
        if not models:
            return
        base = self.c['nodes'][self.a.node]['url'].rstrip('/').removesuffix('/v1')
        try:
            existing = {m['name'] for m in api(base + '/api/tags')['models']}
        except (OSError, ValueError, KeyError) as e:
            fail(f'Local Ollama unavailable: {e}. Start the installed Mac Ollama app/service.')
        for model in sorted(models):
            if model in existing:
                say('KEEP local Ollama ' + model)
            else:
                if not shutil.which('ollama'):
                    fail('Ollama CLI missing; install vendor Ollama.')
                run('ollama', 'pull', model, env=dict(os.environ, OLLAMA_HOST=base))

    def validate_gateway(self):
        # Offline workers are allowed, but local routes and online routes must pass
        # through LiteLLM itself. Auth/protocol errors are not treated as offline.
        self.discover()
        old = self.a.model
        passed, skipped = [], []
        availability = {}
        try:
            for alias, route in self.c['routes'].items():
                if old and alias != old:
                    continue
                url = route['url']
                if url not in availability:
                    try:
                        result = api(url.rstrip('/') + '/models', None if route.get('external') else self.key('worker'), timeout=4)
                        availability[url] = {m['id'] for m in result['data']}
                    except urllib.error.HTTPError as e:
                        fail(f'Upstream HTTP failure {url}: {e.code} {e.reason}; check authentication/configuration.')
                    except (OSError, TimeoutError) as e:
                        if old or route['node'] == self.a.node:
                            fail(f'Required route unavailable: {alias}: {e}')
                        availability[url] = None
                        say(f'Worker unavailable: {route["node"]}: {e}')
                ids = availability[url]
                if ids is None:
                    skipped.append(alias)
                    continue
                if route['model'] not in ids:
                    fail(f'Upstream {url} does not advertise {route["model"]}.')
                self.a.model = alias
                say(f'TEST {alias} via gateway')
                try:
                    self.validate_one(alias)
                except Exception as e:
                    fail(f'Gateway inference failed for {alias}: {e}')
                passed.append(alias)
        finally:
            self.a.model = old
        if not passed:
            fail('No gateway routes passed actual inference.')
        say(f'Gateway inference: {len(passed)} PASS, {len(skipped)} SKIPPED (offline/unreachable, not validated).')
        for alias in skipped:
            say('SKIP ' + alias)
        if skipped and getattr(self.a, 'action', '') == 'validate':
            fail('Gateway validation incomplete: required workers unreachable.')

    def cleanup(self):
        self.require_single_profile()
        self.host_check()
        self.check_layout()
        if self.role != 'worker':
            say('No managed GGUF models on this node. External model stores untouched.')
            return
        old = self.obsolete()
        if not owned(self.cfg):
            fail('Configuration is not owned by this project; inspect before cleanup.')
        state = json.loads(self.active.read_text()) if self.active.exists() else {}
        current = set(state.get('models', []))
        # Protect active versions even if YAML was edited but not applied yet.
        runtime_keep = {self.runtime.name, self.swap.name, *state.get('runtimes', [])}
        old_runtime = [p for p in self.bin.glob('*') if owned(p) and p.name not in runtime_keep]
        if self.service_active() and not state.get('runtimes'):
            old_runtime = []
        if not old and not old_runtime:
            say('No obsolete managed model or runtime directories.')
            return
        conflict = current.intersection(p.name for p in old)
        for p in old + old_runtime:
            say('REMOVE ' + str(p))
        if conflict and not self.a.reclaim_first:
            fail('Old config still references these models; run --models first or --cleanup --reclaim-first to stop managed service and reclaim disk.')
        confirm('Remove only the listed managed model/runtime directories?', self.a.yes)
        if conflict:
            self.service_owned()
            self.stop()
            self.native.unlink(missing_ok=True)
            self.active.unlink(missing_ok=True)
            if self.service_path().exists():
                run('sudo', 'systemctl', 'disable', SERVICE, check=False)
        for p in old + old_runtime:
            remove_owned(p)
        say('Cleanup complete. Hardlinked source files outside the managed store remain until explicitly removed.')





    def model_log(self, alias):
        # Alias is validated; the private config directory is owned by this stack.
        return self.cfg / (alias.replace('/', '--') + '.native.log')

    def check_gpu_offload(self, since, alias):
        # llama.cpp logs the actual number of layers assigned to GPU. Require
        # positive evidence before deleting legacy weights; never assume device
        # enumeration or a successful text response proves GPU acceleration.
        minimum = float(self.desired[alias].get('min_gpu_fraction', 1.0))
        if not 0 < minimum <= 1:
            fail('min_gpu_fraction must be in (0,1].')
        for _ in range(6):
            native = self.model_log(alias)
            # smoke_all clears this file after stopping the service; never use stale evidence.
            logs = native.read_text(errors='replace') if native.is_file() else ''
            if not offload_counts(logs):
                logs += '\n' + run('sudo', 'journalctl', '-u', SERVICE, '--since', since,
                           '--no-pager', '-o', 'cat', capture=True).stdout
            counts = offload_counts(logs)
            if counts:
                actual, total = counts
                if total and 0 < actual <= total and actual / total >= minimum:
                    say(f'GPU offload {alias}: {actual}/{total} layers')
                    return
                fail(f'GPU offload below target for {alias}: {actual}/{total}; tune context or quantization.')
            time.sleep(2)
        fail('No fresh GPU-layer offload evidence in native log or journal for ' + alias + '; see saved startup diagnostics.')

    def save_failure_log(self, since, error):
        """Collect bounded model startup/service diagnostics on failure."""
        if self.role != 'worker':
            return
        try:
            folder = self.root / 'benchmarks'
            folder.mkdir(exist_ok=True)
            path = folder / f'{self.a.node}-migration-failure-{time.strftime("%Y%m%d-%H%M%S")}-{secrets.token_hex(2)}.log'
            journal = run('sudo', 'journalctl', '-u', SERVICE, '--since', since,
                          '--no-pager', '-o', 'cat', '-n', '600', check=False, capture=True).stdout
            if not isinstance(journal, str):
                return
            text = f'Cluster release {RELEASE}\nBackend: {self.backend}\nError: {error}\n\n{journal}'
            for alias in self.desired:
                native = self.model_log(alias)
                if native.is_file():
                    with native.open('rb') as f:
                        f.seek(max(0, native.stat().st_size - 64000))
                        text += f'\n\n--- {alias} native log tail ---\n' + f.read().decode(errors='replace')
            # The managed key can appear in a server diagnostic. Do not publish it.
            with contextlib.suppress(OSError, RuntimeError):
                key = self.key('worker')
                if key:
                    text = text.replace(key, '[REDACTED]')
            atomic(path, text, mode=0o600)
            say('Failure diagnostics: ' + str(path))
            # Surface model-loader cause, not just a proxy HTTP 500.
            for line in text.splitlines()[-60:]:
                if re.search(r'error loading model|failed to load|not found|out of memory|failed to initialize|invalid argument|unknown argument',line,re.I):
                    say(line[:1000])
        except Exception as e:
            say('Could not save failure diagnostics: ' + str(e))

    def smoke_all(self):
        old = self.a.model
        try:
            for alias in self.desired if self.role == 'worker' else self.c['routes']:
                self.a.model = alias
                if self.role == 'worker':
                    # A warm model won't emit a new load log. Restart before each smoke test.
                    self.stop()
                    # Clear only the managed alias-specific log, after stopping its writer.
                    atomic(self.model_log(alias), '', mode=0o600)
                since = datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S.%f')
                if self.role == 'worker':
                    self.start()
                try:
                    self.validate()
                    if self.role == 'worker':
                        self.check_gpu_offload(since, alias)
                except BaseException as e:
                    say('Validation failed for ' + alias + ': ' + str(e))
                    self.save_failure_log(since, e)
                    raise
        finally:
            self.a.model = old

    def migrate(self):
        return self.reconcile(install=True)

    def reset(self, purge=False, uninstall=False):
        self.require_single_profile()
        self.host_check()
        self.check_layout()
        if self.role == 'edge':
            fail('Edge vendor stack is not reset by this project.')
        self.service_owned()
        confirm('Stop and remove managed runtime/service' + (' and managed GGUF weights?' if purge else ' (preserve weights and cache)?'), self.a.yes)
        self.stop()
        if self.service_path().exists():
            if self.role == 'worker':
                run('sudo', 'systemctl', 'disable', SERVICE, check=False)
            run('sudo', 'rm', '--', self.service_path())
            if self.role == 'worker':
                run('sudo', 'systemctl', 'daemon-reload')
        for p in self.bin.glob('*'):
            if owned(p):
                remove_owned(p)
        if owned(self.cfg):
            for p in (self.native, self.native.with_suffix('.previous.yaml'), self.active, self.cfg / 'seed-complete.json'):
                p.unlink(missing_ok=True)
        if purge:
            for p in self.models.glob('*'):
                if owned(p):
                    remove_owned(p)
        if not (uninstall or purge):
            self.reconcile(install=True)
        else:
            say('Managed runtime removed. Cache, keys, benchmarks and all external stores preserved.')

    def health(self):
        say(f'{self.a.node}: {self.endpoint}')
        run('uname', '-a', check=False)
        run('df', '-h', Path.home(), check=False)
        if platform.system() == 'Linux':
            run('free', '-h', check=False)
            run('systemctl', '--failed', '--no-pager', check=False)
        mode = self.c.get('hardware', {}).get('telemetry')
        tools = {'nvidia_smi': ('nvidia-smi', ['--query-gpu=name,memory.total,memory.free,temperature.gpu','--format=csv']), 'amd_gtt': ('amd-smi', ['metric','--mem-usage']), 'intel_xpu': ('xpu-smi', ['discovery'])}
        if mode in tools:
            tool, flags = tools[mode]
            if shutil.which(tool): run(tool, *flags, check=False)
        if shutil.disk_usage(Path.home()).free < self.c.get('reserve_gib',15)*2**30:
            say('WARNING: low disk capacity')
        if self.role == 'worker':
            if Path('/boot').exists():
                run('df', '-h', '/boot', check=False)
            run('systemctl', 'status', SERVICE, '--no-pager', check=False)
        elif self.role == 'gateway':
            unreachable = []
            if not self.service_active():
                fail('Managed gateway is not loaded')
            self.discover()  # Check the authenticated LiteLLM endpoint before its backends.
            say('Gateway API: PASS')
            if self.gateway_tailnet:
                if not self.gateway_forward(int(self.c['port'])):
                    fail('Gateway Tailscale Serve forwarding missing; rerun setup.')
                say('Tailnet gateway: ' + self.public_endpoint + '/v1 (forward configured; remote peer test required)')
            for node, profile in self.c['nodes'].items():
                if not profile.get('enabled'):
                    say(f'{node}: disabled in gateway config; skipped')
                    continue
                try:
                    expected = {r['model'] for r in self.c['routes'].values() if r['node'] == node}
                    node_cfg = self.c if node == self.a.node else yaml.safe_load((REPO / 'config' / (node + '.yaml')).read_text())
                    data = api(profile['url'].rstrip('/') + '/models', self.key('worker') if node_cfg['role'] == 'worker' else None, timeout=3)
                    advertised = {m['id'] for m in data['data']}
                    status = f'API reachable; advertised={sorted(advertised)}; missing={sorted(expected-advertised)}; extra/unrouted={sorted(advertised-expected)}'
                    if profile.get('enabled') and expected - advertised:
                        status = 'CATALOG DRIFT: ' + status
                        unreachable.append(node)
                except (OSError, ValueError, RuntimeError) as e:
                    status = f'API unavailable or incompatible ({e})'
                    if profile.get('enabled'): unreachable.append(node)
                say(f'{node}: {status}; routing={profile.get("enabled", False)}')
            if unreachable:
                raise DegradedRoutes('Required routes unavailable or stale: ' + ', '.join(unreachable))
            return
        self.discover()

    def discover(self):
        key = self.key('gateway' if self.role == 'gateway' else 'worker')
        data = api(self.endpoint + '/v1/models', key)
        ids = {m['id'] for m in data.get('data', [])}
        expected = set(self.desired if self.role == 'worker' else self.c['routes'])
        say('Advertised models: ' + ', '.join(sorted(ids)))
        if not expected.issubset(ids):
            fail('Missing advertised models: ' + str(expected - ids))
        return ids

    def validate_one(self, alias):
        key = self.key('gateway' if self.role == 'gateway' else 'worker')
        if self.role == 'gateway' and self.c['routes'][alias].get('embedding'):
            result = api(self.endpoint + '/v1/embeddings', key, {'model': alias, 'input': 'local cluster smoke test'}, 600)
            vector = (result.get('data') or [{}])[0].get('embedding')
            if not isinstance(vector, list) or not vector or not all(isinstance(x, (float, int)) and math.isfinite(x) for x in vector) or not any(vector):
                fail(f'{alias}: invalid embedding response.')
            say(f'PASS {alias}: embedding dimensions={len(vector)}')
        else:
            result = api(self.endpoint + '/v1/chat/completions', key, {'model': alias, 'messages': [{'role': 'user', 'content': 'Reply with one word: READY.'}], 'max_tokens': 4096, 'stream': False, **(self.desired[alias].get('test_request', {}) if self.role == 'worker' else {})}, 600)
            text = (result.get('choices') or [{}])[0].get('message', {}).get('content')
            if not isinstance(text, str) or not text.strip() or 'ready' not in text.lower():
                fail(f'{alias}: missing expected completion.')
            say(f'PASS {alias}: completion; usage={result.get("usage")}')
            if self.role == 'worker' and self.desired[alias].get('tool_test'):
                self.validate_tool(alias, key)

    def validate_tool(self, alias, key):
        # No tool executes: verify the model's Jinja template and structured arguments.
        token = 'cluster-' + secrets.token_hex(4)
        tool = {'type': 'function', 'function': {'name': 'cluster_echo', 'description': 'Echo a supplied token', 'parameters': {'type': 'object', 'properties': {'token': {'type': 'string'}}, 'required': ['token']}}}
        payload = {'model': alias, 'messages': [{'role': 'user', 'content': 'Call cluster_echo with token ' + token + '. Do not answer in text.'}], 'tools': [tool], 'max_tokens': 4096, 'stream': False, 'temperature': 0, **self.desired[alias].get('test_request', {})}
        result = api(self.endpoint + '/v1/chat/completions', key, payload, 600)
        calls = (result.get('choices') or [{}])[0].get('message', {}).get('tool_calls') or []
        for call in calls:
            function = call.get('function', {})
            if function.get('name') == 'cluster_echo' and json.loads(function.get('arguments', '{}')).get('token') == token:
                say('PASS structured tool call ' + alias)
                return
        fail('Structured tool call failed: ' + alias)

    def validate(self):
        if self.role == 'gateway':
            if self.a.model and self.a.model not in self.c['routes']:
                fail('Unknown model alias.')
            return self.validate_gateway()
        ids = self.discover()
        aliases = [self.a.model] if self.a.model else list(self.desired)
        for alias in aliases:
            if alias not in ids or alias not in self.desired:
                fail('Unknown model alias: ' + alias)
            self.validate_one(alias)

    def benchmark(self):
        if self.role != 'worker' or self.a.model not in self.desired:
            fail('Use --benchmark --model ALIAS on a primary worker.')
        self.host_check()
        confirm('Stage benchmark runtime/model if missing and temporarily pause inference?', self.a.yes)
        self.prepare(); self.service_owned(); self.install_worker()
        folder, records = self.sync_model(self.a.model, self.desired[self.a.model])
        weight = folder / next(x['name'] for x in records if 'mmproj' not in x['name'].lower())
        bench = self.locate(self.runtime, 'llama-bench')
        with self.isolated_gpu():
            out = self.root / 'benchmarks' / f'{self.a.node}-{self.backend}-{self.a.model.replace(chr(47), chr(45))}-{time.strftime("%Y%m%d-%H%M%S")}.txt'
            out.parent.mkdir(exist_ok=True)
            command = [str(bench), '-m', str(weight), '-ngl', '999', '-p', '512,8192', '-n', '128', '-r', '3']
            with out.open('w') as f:
                f.write(json.dumps({'command': command, 'version': self.v['llama_cpp'], 'weights': records, 'kernel': platform.release()}, indent=2) + '\n')
                f.flush()
                p = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, env=self.runtime_env())
                try:
                    for line in p.stdout:
                        print(line, end='', flush=True)
                        f.write(line)
                    if p.wait():
                        fail('Benchmark failed; see ' + str(out))
                finally:
                    if p.poll() is None:
                        p.terminate()
                        try:
                            p.wait(timeout=15)
                        except subprocess.TimeoutExpired:
                            p.kill(); p.wait()
            say('Benchmark: ' + str(out))

    def calibrate(self, migration=False):
        from calibrate import calibrate_cluster
        calibrate_cluster(self, migration=migration)


def main():
    from inventory import profiles
    parser = argparse.ArgumentParser(description='Local AI cluster: one profile, native services, pinned artifacts.')
    parser.add_argument('--version', action='version', version='local-ai-cluster ' + RELEASE)
    parser.add_argument('node', choices=profiles())
    group = parser.add_mutually_exclusive_group()
    for flag, helptext in [('apply-routes', 'Apply enabled Mac gateway routes without contacting workers'), ('install', 'Seed/reconcile: retire old inference assets first (default)'), ('seed', 'Same as install: clean desired-state deployment, no rollback'), ('migrate', 'Alias for clean seed deployment'), ('models', 'Sync desired models; remove obsolete managed weights first'), ('cleanup', 'Remove obsolete managed model directories'), ('reset', 'Rebuild managed runtime; preserve models/cache'), ('uninstall', 'Remove managed runtime/service; preserve weights'), ('purge', 'Remove managed runtime and owned weights; preserve keys/cache'), ('start', 'Start managed service'), ('stop', 'Stop managed service'), ('health', 'Read-only system/API health'), ('validate', 'Execute inference on configured models; optionally select --model'), ('benchmark', 'Benchmark all enabled models or select --model'), ('calibrate', 'Measure real long-context prompts; optionally select --model'), ('init-keys', 'Create local gateway and worker keys on Mac')]:
        group.add_argument('--' + flag, dest='action', action='store_const', const=flag, help=helptext)
    parser.add_argument('--dry-run', action='store_true', help='Print desired state only; no managed changes')
    parser.add_argument('--yes', action='store_true', help='Approve listed managed operations noninteractively')
    parser.add_argument('--backend', choices=['vulkan', 'rocm', 'sycl', 'cuda', 'cuda-native'], help='Override worker backend for this run')
    parser.add_argument('--model', help='Alias for benchmark or smoke test')
    parser.add_argument('--prune', action='store_true', help='Compatibility flag; seed/reconcile always prunes obsolete weights')
    parser.add_argument('--reclaim-first', action='store_true', help='With --cleanup stop managed inference if obsolete weights are still referenced; seed always reclaims first')
    parser.add_argument('--apply', action='store_true', help='Apply measured context/cache recommendation to source YAML; deploy separately')
    parser.add_argument('--cache-type', choices=['f16','q8_0','q4_0'], help='Test only this KV cache type during calibration')
    a = parser.parse_args()
    say('local-ai-cluster v' + RELEASE)
    a.action = a.action or 'install'
    if (a.apply or a.cache_type) and a.action != 'calibrate':
        parser.error('--apply and --cache-type require --calibrate')
    if a.reclaim_first and a.action not in ('cleanup', 'seed', 'migrate', 'install', 'models'):
        parser.error('--reclaim-first requires --cleanup, --migrate, --install or --models')
    try:
        if not hasattr(tarfile, 'data_filter'):
            fail('Python lacks safe tar extraction; upgrade Python or use uv Python 3.12.')
        c = Cluster(a)
        if a.dry_run:
            c.plan()
            say('Requested action: ' + a.action)
            return
        if (c.cfg / 'modes-enabled.json').exists() and a.action not in ('health', 'validate', 'apply-routes'):
            c.require_single_profile()
        if c.role == 'edge' and a.action in ('health', 'validate'):
            from edge import Edge
            Edge(c).dispatch()
            return
        if a.action in ('health', 'validate'):
            (c.health if a.action == 'health' else c.validate)()
            return
        c.host_check()
        c.check_layout()
        c.root.mkdir(exist_ok=True)
        with (c.root / '.manage.lock').open('a') as lock:
            try:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                fail('Another deployment action is already running on this node.')
            if a.action == 'apply-routes':
                c.apply_routes()
            elif c.role == 'edge':
                from edge import Edge
                Edge(c).dispatch()
            elif a.action == 'init-keys':
                c.init_keys()
            elif a.action in ('migrate','seed'):
                c.reconcile()
            elif a.action in ('install', 'models'):
                c.reconcile(install=a.action == 'install')
            elif a.action in ('reset', 'purge', 'uninstall'):
                c.reset(purge=a.action == 'purge', uninstall=a.action == 'uninstall')
            elif a.action == 'cleanup':
                c.cleanup()
            elif a.action == 'start':
                c.reconcile()
            elif a.action == 'stop':
                c.host_check(); c.service_owned(); c.stop()
            elif a.action == 'health':
                c.health()
            elif a.action == 'validate':
                c.validate()
            elif a.action == 'benchmark':
                if c.migration_pending(): c.reconcile()
                if a.model: c.benchmark()
                else:
                    for alias in c.desired:
                        a.model = alias; c.benchmark()
                    a.model = None
            elif a.action == 'calibrate':
                if c.migration_pending(): c.reconcile()
                c.calibrate()
    except DegradedRoutes as e:
        print('DEGRADED: ' + str(e), file=sys.stderr)
        sys.exit(3)
    except KeyboardInterrupt:
        print('Interrupted; no legacy rollback. Rerun setup to reconcile.', file=sys.stderr)
        sys.exit(130)
    except (RuntimeError, OSError, ValueError, KeyError, subprocess.CalledProcessError) as e:
        print('ERROR:', e, file=sys.stderr)
        sys.exit(1)


if __name__ == '__main__':
    main()
