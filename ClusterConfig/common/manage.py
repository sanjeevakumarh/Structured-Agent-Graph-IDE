#!/usr/bin/env python3
"""Shared deployment logic (not a daemon). Edit config/<node>.yaml, not this file.
Uses native systemd/launchd and llama-swap/LiteLLM configs. Downloads verified
release/model files into ~/inference; only marked, repo-owned files are pruned.
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
RELEASE = '0.7'
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
    routes = {}
    for node, n in config['nodes'].items():
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
    return args + extra + ['--log-verbosity', '3', '--log-colors', 'off', '--no-log-jsonl']


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
    req = urllib.request.Request(url, headers={'User-Agent': 'local-ai-cluster/0.6', **(headers or {})})
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
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.load(r)
    except urllib.error.HTTPError as e:
        detail = http_error_detail(e, key)
        # Keep status available to gateway offline/authentication handling.
        raise urllib.error.HTTPError(e.url, e.code, detail, e.headers, None) from None


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
        if self.role != 'edge':
            if self.c['bind'] == 'tailnet':
                cli = shutil.which('tailscale') or '/Applications/Tailscale.app/Contents/MacOS/Tailscale'
                ip = run(cli, 'ip', '-4', capture=True).stdout.strip()
                import ipaddress
                if not ipaddress.ip_address(ip).is_private and ipaddress.ip_address(ip) not in ipaddress.ip_network('100.64.0.0/10'):
                    fail('Unexpected tailnet IPv4 address')
                self.c['bind'] = ip
            self.endpoint = f'http://{self.c["bind"]}:{self.c["port"]}'
        else:
            self.endpoint = self.c['endpoint']

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
            if not any(x.read_text().strip() == vendor for x in cards):
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
        if self.role == 'worker':
            say(f'Runtime: {self.v["llama_cpp"]} {self.backend}; llama-swap {self.v["llama_swap"]}')
            for alias, m in self.desired.items():
                folder = self.models / model_id(m)
                say(f'{"KEEP" if owned(folder) and (folder / "weights.json").exists() else "SYNC"} {alias}: {m["source"]} (ctx {m["context"]})')
            for p in self.obsolete():
                say('PRUNE CANDIDATE ' + str(p))
            say('Legacy service: ' + self.c.get('legacy_service', 'none') + ' (retired during initial reconciliation)')
        elif self.role == 'gateway':
            say(f'LiteLLM {self.v["litellm"]}; routes:')
            for k, v in self.c['routes'].items():
                say(f'  {k} -> {v["url"]} / {v["model"]}')
        else:
            say(f'Runtime: {self.c["runtime"]}; desired models:')
            for alias, spec in self.c['edge_models'].items():
                if spec.get('enabled', True): say(f'  {alias}: {spec["id"]}')
            say('Explicit legacy retirement: ' + str(self.c.get('retire_models', [])))
        if migration or getattr(self.a, 'action', '') in ('migrate', 'install', 'models', 'start'):
            say('IF LEGACY MIGRATION IS NEEDED: validate replacement, then retire listed assets.')
            for kind, items in self.c.get('legacy_cleanup', {}).items():
                for item in items:
                    say(f'  {kind}: {item}')
            if self.c.get('legacy_launch_agent'):
                say('  retire launch agent: ' + self.c['legacy_launch_agent'])
        say('No partition, boot, driver or unrelated application changes.')

    def obsolete(self):
        keep = {model_id(m) for m in self.c.get('models', {}).values()}
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
            fail(f'Runtime failed ({e}); Z820 may need --backend cuda-native; check driver/library compatibility.')
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
        # Fast repeated deployment: validated files with unchanged size and mtime.
        if receipt.exists():
            records = json.loads(receipt.read_text())
            if records and all((folder / r['name']).is_file() and
                               (folder / r['name']).stat().st_size == r['size'] and
                               (folder / r['name']).stat().st_mtime_ns == r['mtime'] for r in records):
                say('KEEP ' + alias)
                return folder, records
        if self.store and self.store.exists() and not os.access(self.store / 'blobs', os.R_OK | os.X_OK):
            say('WARNING: legacy blob store unreadable; exact-hash reuse unavailable; new downloads may be large.')
        sources = model_sources(spec, self.store)
        if not folder.exists():
            folder.mkdir()
            atomic(folder / MARK, 'local-ai-cluster\n')
        records = []
        for filename, size, digest, url, candidates in sources:
            dest = folder / filename
            dest.parent.mkdir(parents=True, exist_ok=True)
            if not (dest.is_file() and dest.stat().st_size == size and sha(dest) == digest):
                if dest.exists():
                    fail('Managed file differs from manifest; inspect/remove it explicitly: ' + str(dest))
                for source in candidates:
                    try:
                        matches = source.is_file() and source.stat().st_size == size and sha(source) == digest
                    except PermissionError:
                        say('WARNING: cannot read reuse candidate ' + str(source))
                        matches = False
                    if matches:
                        check_gguf(source)
                        try:
                            os.link(source, dest)
                        except OSError:
                            if shutil.disk_usage(folder).free < size + self.c.get('reserve_gib', 15) * 2**30:
                                raise DiskSpaceError('Not enough free disk to copy verified existing file.')
                            shutil.copy2(source, dest)
                        say('REUSE ' + str(source))
                        break
                else:
                    download(url, dest, digest, size, self.c.get('reserve_gib', 15) * 2**30)
            check_gguf(dest)
            records.append({'name': filename, 'size': size, 'sha256': digest, 'mtime': dest.stat().st_mtime_ns})
        atomic(receipt, json.dumps(records, indent=2))
        return folder, records

    def render_worker(self):
        server = self.locate(self.runtime, 'llama-server')
        key = self.key('worker')
        # v277 defaults to proxy-only logging: GPU offload evidence lives upstream.
        config = {'healthCheckTimeout': 600, 'logLevel': 'info', 'logToStdout': 'both',
                  'captureBuffer': 0, 'apiKeys': [key], 'startPort': 18081, 'models': {}}
        active = []
        for alias, spec in self.desired.items():
            folder, rec = self.sync_model(alias, spec)
            active.append(folder.name)
            args = model_command(server, folder, rec, alias, spec, '${PORT}')
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
        cmd = ('launchctl', 'print', 'system/' + LABEL) if self.role == 'gateway' else ('systemctl', 'is-active', '--quiet', SERVICE)
        return run(*cmd, check=False, capture=True).returncode == 0

    def legacy_exists(self):
        name = self.c.get('legacy_service')
        return bool(name and run('systemctl', 'show', '-p', 'LoadState', '--value', name, check=False, capture=True).stdout.strip() == 'loaded')

    def legacy_active(self):
        legacy = self.c.get('legacy_service')
        return bool(legacy and run('systemctl', 'is-active', '--quiet', legacy, check=False, capture=True).returncode == 0)

    def legacy_unit(self):
        expected = self.c.get('legacy_service')
        if expected and (not SAFE.fullmatch(expected) or not expected.endswith('.service') or expected == SERVICE):
            fail('Invalid legacy service; inspect node YAML.')
        return expected

    def migration_pending(self):
        if self.role == 'gateway':
            label = self.c.get('legacy_launch_agent')
            return bool(label == 'local.litellm' and run('launchctl', 'print', f'gui/{os.getuid()}/{label}', check=False, capture=True).returncode == 0)
        if self.role != 'worker':
            return False
        self.legacy_unit()
        if self.legacy_active():
            return True
        if (self.cfg / 'migration-complete.json').is_file():
            return False
        cleanup = self.c.get('legacy_cleanup', {})
        return self.legacy_exists() or any(Path(v).expanduser().exists() for v in cleanup.get('model_files', []) + cleanup.get('runtime_dirs', []))

    @contextlib.contextmanager
    def isolated_gpu(self):
        """Pause known GPU services and restore their previous state, even on Ctrl-C."""
        legacy = self.legacy_unit()
        old = self.legacy_active()
        managed = self.service_active()
        try:
            if old:
                say('PAUSE ' + legacy)
                run('sudo', 'systemctl', 'stop', legacy)
            if managed:
                say('PAUSE ' + SERVICE)
                self.stop()
            if self.legacy_active() or self.service_active():
                fail('Could not isolate GPU; a known inference service is still active.')
            yield
        finally:
            errors = []
            for was_active, unit in [(managed, SERVICE), (old, legacy)]:
                if was_active:
                    try:
                        say('RESTORE ' + unit)
                        run('sudo', 'systemctl', 'start', unit)
                    except Exception as e:
                        errors.append(f'{unit}: {e}')
            if errors:
                fail('Could not restore previous inference service(s): ' + '; '.join(errors))

    def reconcile(self, install=True):
        if self.migration_pending():
            self.migrate()
        else:
            self.plan()
            confirm('Apply this configuration and retire obsolete managed models after validation?', self.a.yes)
            try:
                self.sync(install=install)
            except DiskSpaceError:
                if self.role != 'worker' or not self.obsolete():
                    raise
                say('Insufficient disk space; obsolete managed weights can be reclaimed.')
                previous = self.a.reclaim_first
                self.a.reclaim_first = True
                try:
                    self.cleanup()  # lists files and confirms before early removal
                finally:
                    self.a.reclaim_first = previous
                self.sync(install=install)

    def stop(self):
        if self.role == 'edge':
            fail('Edge service is vendor-managed; no stop action.')
        self.service_owned()
        if self.role == 'gateway':
            if self.service_active():
                run('sudo', 'launchctl', 'bootout', 'system/' + LABEL)
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
                     'EnvironmentVariables': {'HOME': str(Path.home()), 'PATH': '/opt/homebrew/bin:/usr/bin:/bin'},
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
        if not self.service_active():
            # Catch stale addresses and occupied ports without altering existing processes.
            with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
                s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                try:
                    s.bind((self.c['bind'], int(self.c['port'])))
                except OSError as e:
                    if e.errno == 98 or e.errno == 48:
                        # Report actual listener if ss is available; never kill unknown processes.
                        if shutil.which('ss'):
                            run('ss', '-ltnp', f'sport = :{self.c["port"]}', check=False)
                        fail(f'Port {self.c["port"]} already in use on {self.c["bind"]}; not stopping an unknown listener.')
                    raise
        if self.role == 'gateway':
            if not self.service_active():
                run('sudo', 'launchctl', 'bootstrap', 'system', self.service_path())
        else:
            run('sudo', 'systemctl', 'enable', '--now', SERVICE)
        self.wait_ready()

    def wait_ready(self):
        key = self.key('gateway' if self.role == 'gateway' else 'worker')
        for _ in range(30):
            try:
                api(self.endpoint + '/v1/models', key, timeout=2)
                say('API reachable: ' + self.endpoint + '/v1; inference validation pending.')
                return
            except (OSError, ValueError):
                time.sleep(2)
        fail('Service did not return a model list; inspect native logs.')

    def sync(self, install=False, stage_only=False):
        self.prepare()
        if self.role == 'edge':
            from edge import Edge
            return Edge(self).sync(prune=self.a.prune)
        self.service_owned()
        if self.role == 'gateway':
            self.pull_ollama()
            self.install_gateway()
            config, active = self.render_gateway(), []
        else:
            if install or not owned(self.runtime) or not owned(self.swap):
                self.install_worker()
            config, active = self.render_worker()
        # Stage all weights first. Do not restart unchanged configuration.
        native_text = yaml.safe_dump(config, sort_keys=False)
        state_text = json.dumps({'service_schema': 7, 'models': active, 'backend': self.backend, 'endpoint': self.endpoint,
                                 'runtimes': [self.runtime.name, self.swap.name] if self.role == 'worker' else [self.venv.name]}, indent=2)
        unchanged = (self.native.is_file() and self.native.read_text() == native_text and
                     self.active.is_file() and self.active.read_text() == state_text and self.service_path().exists())
        was_running = self.service_active()
        old_files = {p: p.read_bytes() for p in (self.native, self.active, self.service_path()) if p.is_file()}
        changed_service = not unchanged or (not was_running and not stage_only)
        prune = self.role == 'worker' and (self.a.prune or bool(self.obsolete()))
        try:
            if unchanged:
                say('KEEP configuration and service (unchanged).')
            else:
                if was_running:
                    confirm('Restart managed inference now? Active requests will be interrupted.', self.a.yes)
                    self.stop()
                if self.native.exists():
                    atomic(self.native.with_suffix('.previous.yaml'), self.native.read_bytes())
                atomic(self.native, native_text)
                atomic(self.active, state_text)
                self.install_service()
            if stage_only:
                say('Staged verified runtime and configuration for automatic cutover.')
                return
            if self.legacy_active():
                fail('Legacy service became active during reconciliation; retry setup for automatic migration.')
            if not self.service_active():
                self.start()
            if self.role == 'gateway':
                self.validate_gateway()
            else:
                if prune and not changed_service:
                    self.stop(); self.start()
                if prune or changed_service:
                    self.smoke_all()
                else:
                    self.validate()
                if prune:
                    self.calibrate(migration=True)
        except BaseException:
            if changed_service:
                self.restore_configuration(old_files, was_running)
            elif was_running and not self.service_active():
                self.start()
            raise
        # Irreversible cleanup is outside rollback: never restore deleted assets.
        if self.role == 'worker':
            self.cleanup()

    def restore_configuration(self, old_files, was_running):
        say('Apply failed; restoring previous configuration where available.')
        self.stop()
        for p in (self.native, self.active):
            if p in old_files:
                atomic(p, old_files[p])
            else:
                p.unlink(missing_ok=True)
        svc = self.service_path()
        if svc in old_files:
            with tempfile.TemporaryDirectory() as tmp:
                saved = Path(tmp) / svc.name
                saved.write_bytes(old_files[svc])
                run('sudo', 'install', '-m', '0644', saved, svc)
            if self.role == 'worker':
                run('sudo', 'systemctl', 'daemon-reload')
            if was_running:
                if self.role == 'gateway':
                    run('sudo', 'launchctl', 'bootstrap', 'system', svc)
                else:
                    run('sudo', 'systemctl', 'start', SERVICE)
        elif svc.exists():
            if self.role == 'worker':
                run('sudo', 'systemctl', 'disable', SERVICE, check=False)
            run('sudo', 'rm', '--', svc)
            if self.role == 'worker':
                run('sudo', 'systemctl', 'daemon-reload')

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
                        fail(f'Upstream HTTP failure {url}: {e.code}; check authentication/configuration.')
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
                self.validate_one(alias)
                passed.append(alias)
        finally:
            self.a.model = old
        if not passed:
            fail('No gateway routes passed actual inference.')
        say(f'Gateway inference: {len(passed)} PASS, {len(skipped)} SKIPPED (offline/unreachable, not validated).')
        for alias in skipped:
            say('SKIP ' + alias)

    def cleanup(self):
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

    def legacy_path(self, value, runtime=False):
        """Migration allowlist: only inventoried paths in this login user's home."""
        home = Path.home().resolve()
        path = Path(value).expanduser()
        if not path.is_absolute() or not path.is_relative_to(home) or path == home:
            fail('Legacy path outside login home: ' + str(path))
        if path.resolve() != path or any(x.is_symlink() for x in [path, *path.parents] if x.is_relative_to(home)):
            fail('Refusing symlink or ambiguous legacy path: ' + str(path))
        if runtime:
            allowed = {Path(v).expanduser() for v in self.c.get('legacy_cleanup', {}).get('runtime_dirs', [])}
            if path.parent != home or path not in allowed:
                fail('Runtime not in inventoried directory allowlist: ' + str(path))
        elif not path.is_relative_to(home / 'models') or path.suffix != '.gguf':
            fail('Only exact GGUF files under ~/models may be retired: ' + str(path))
        return path

    def retire_files(self, values):
        paths = [self.legacy_path(v) for v in values]
        for p in paths:
            if p.exists():
                if not p.is_file() or os.path.ismount(p):
                    fail('Not a regular legacy model file: ' + str(p))
                for proc in Path('/proc').glob('[0-9]*'):
                    try:
                        maps = (proc / 'maps').read_text(errors='replace')
                        cmd = (proc / 'cmdline').read_bytes().replace(b'\x00', b' ').decode(errors='replace')
                    except OSError:
                        continue
                    if str(p) in maps or str(p) in cmd:
                        fail('Legacy model appears in use: ' + str(p))
                say('REMOVE legacy model ' + str(p))
                p.unlink()
                # Remove empty model subdirectories only, never the top-level ~/models.
                parent = p.parent
                while parent != Path.home() / 'models':
                    try:
                        parent.rmdir()
                    except OSError:
                        break
                    parent = parent.parent

    def retire_runtimes(self, values):
        for value in values:
            p = self.legacy_path(value, runtime=True)
            if not p.exists():
                continue
            if not p.is_dir() or not any(p.rglob('llama-server')):
                fail('Unexpected legacy runtime directory: ' + str(p))
            # Never cross a nested mount or remove a runtime that is in use.
            if any(os.path.ismount(x) for x in [p, *p.rglob('*')] if x.is_dir()):
                fail('Legacy runtime contains a mount: ' + str(p))
            for proc in Path('/proc').glob('[0-9]*'):
                try:
                    exe = os.readlink(proc / 'exe')
                    cmd = (proc / 'cmdline').read_bytes().replace(b'\x00', b' ').decode(errors='replace')
                except OSError:
                    continue
                if exe.startswith(str(p) + '/') or str(p) + '/' in cmd:
                    fail('Legacy runtime still in use by PID ' + proc.name)
            say('REMOVE legacy runtime ' + str(p))
            shutil.rmtree(p)

    def retire_ollama(self, tags):
        if not tags or not self.legacy_exists():
            return
        if self.c.get('legacy_service') != 'ollama.service':
            fail('Legacy Ollama cleanup is restricted to Ollama workers.')
        # Use Ollama's own deletion API to preserve shared blobs and unknown tags.
        # Do not let the old server and new worker execute inference together.
        if not shutil.which('ollama'):
            say('Legacy Ollama CLI missing; model store preserved for manual review.')
            return
        self.stop()
        try:
            run('sudo', 'systemctl', 'start', 'ollama.service')
            base = 'http://127.0.0.1:11434'
            for _ in range(20):
                try:
                    installed = {x['name'] for x in api(base + '/api/tags')['models']}
                    break
                except (OSError, ValueError):
                    time.sleep(1)
            else:
                fail('Legacy Ollama API unavailable; model store preserved.')
            env = dict(os.environ, OLLAMA_HOST='127.0.0.1:11434')
            for tag in tags:
                if tag in installed:
                    say('REMOVE legacy Ollama tag ' + tag)
                    run('ollama', 'rm', tag, env=env)
            unknown = installed - set(tags)
            if unknown:
                say('Unlisted Ollama tags preserved: ' + ', '.join(sorted(unknown)))
        finally:
            run('sudo', 'systemctl', 'disable', '--now', 'ollama.service')
            self.start()

    def check_gpu_offload(self, since, alias):
        # llama.cpp logs the actual number of layers assigned to GPU. Require
        # positive evidence before deleting legacy weights; never assume device
        # enumeration or a successful text response proves GPU acceleration.
        minimum = float(self.desired[alias].get('min_gpu_fraction', 1.0))
        if not 0 < minimum <= 1:
            fail('min_gpu_fraction must be in (0,1].')
        for _ in range(3):
            logs = run('sudo', 'journalctl', '-u', SERVICE, '--since', since,
                       '--no-pager', '-o', 'cat', capture=True).stdout
            counts = offload_counts(logs)
            if counts:
                actual, total = counts
                if total and 0 < actual <= total and actual / total >= minimum:
                    say(f'GPU offload {alias}: {actual}/{total} layers')
                    return
                fail(f'GPU offload below target for {alias}: {actual}/{total}; tune context or quantization.')
            time.sleep(2)
        fail('No GPU-layer offload evidence in journal for ' + alias + '; legacy assets preserved. See saved migration diagnostics.')

    def save_failure_log(self, since, error):
        """Collect bounded model startup/service diagnostics before rollback."""
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
            # The managed key can appear in a server diagnostic. Do not publish it.
            with contextlib.suppress(OSError, RuntimeError):
                key = self.key('worker')
                if key:
                    text = text.replace(key, '[REDACTED]')
            atomic(path, text, mode=0o600)
            say('Failure diagnostics: ' + str(path))
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
        """Install first, smoke test, then delete explicitly listed legacy assets."""
        self.prepare()
        self.plan(migration=True)
        if self.role == 'edge':
            from edge import Edge
            return Edge(self).sync(prune=True)
        confirm('Clean migration: retire the listed old services/models/runtimes after validation?', self.a.yes)
        if self.role == 'gateway':
            self.sync(install=True)
            label = self.c.get('legacy_launch_agent')
            if label:
                if label != 'local.litellm':
                    fail('Unexpected legacy launch agent label.')
                domain = f'gui/{os.getuid()}'
                if run('launchctl', 'print', domain + '/' + label, check=False, capture=True).returncode == 0:
                    run('launchctl', 'bootout', domain + '/' + label)
                run('launchctl', 'disable', domain + '/' + label)
                say('Old gateway disabled; its files retained for rollback. Test remote routes when workers are online.')
            return
        legacy = self.legacy_unit()
        cleanup = self.c.get('legacy_cleanup', {})
        names = {str(name).lower() for name in self.c.get('migration_hostnames', [self.a.node])}
        if names and socket.gethostname().split('.')[0].lower() not in names:
            fail('Migration hostname not allowed. Review legacy_cleanup and migration_hostnames in the node YAML, or use --install on a fresh host.')
        # Verify all destructive paths before making any changes.
        for value in cleanup.get('model_files', []) + cleanup.get('reclaim_first', []):
            self.legacy_path(value)
        for value in cleanup.get('runtime_dirs', []):
            self.legacy_path(value, runtime=True)
        if self.c.get('resident_groups'):
            fail('Migrate with resident_groups empty first; enable groups after combined memory testing.')
        self.key('worker')
        self.install_worker()  # require working binary and visible GPU BEFORE deleting anything
        if self.a.reclaim_first:
            reclaim = cleanup.get('reclaim_first', [])
            if reclaim:
                for p in reclaim:
                    say('EARLY REMOVE ' + p)
                confirm('Delete listed oversized models before new inference is validated? No automatic recovery.', self.a.yes)
                self.retire_files(reclaim)
        was_active = self.legacy_active()
        managed_was_active = self.service_active()
        prior = {p: p.read_bytes() for p in (self.native, self.active, self.service_path()) if p.is_file()}
        # Stage verified weights before cutover; reclaim only explicitly approved paths on disk pressure.
        try:
            self.sync(install=True, stage_only=True)
        except DiskSpaceError:
            reclaim = [v for v in cleanup.get('reclaim_first', []) if Path(v).expanduser().is_file()]
            if not reclaim:
                raise
            for v in reclaim:
                say('EARLY REMOVE ' + v)
            confirm('Insufficient disk space. Delete listed obsolete weights now and retry? No automatic recovery.', self.a.yes)
            self.retire_files(reclaim)
            self.sync(install=True, stage_only=True)
        cutover_since = datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S.%f')
        try:
            if was_active:
                run('sudo', 'systemctl', 'stop', legacy)
            if self.service_active():
                self.stop()
            self.start()
            self.smoke_all()
            # Validate real long prompts at the deployed settings before irreversible cleanup.
            self.calibrate(migration=True)
        except BaseException as e:
            self.save_failure_log(cutover_since, e)
            say('New inference failed; legacy assets retained (except explicit early reclaim).')
            if self.service_active():
                self.stop()
            if managed_was_active and self.native in prior and self.active in prior:
                for p in (self.native, self.active):
                    atomic(p, prior[p])
                svc = self.service_path()
                if svc in prior:
                    with tempfile.TemporaryDirectory() as tmp:
                        saved = Path(tmp) / svc.name
                        saved.write_bytes(prior[svc])
                        run('sudo', 'install', '-m', '0644', saved, svc)
                    run('sudo', 'systemctl', 'daemon-reload')
                run('sudo', 'systemctl', 'start', SERVICE)
            else:
                run('sudo', 'systemctl', 'disable', SERVICE, check=False)
            if was_active:
                run('sudo', 'systemctl', 'start', legacy)
            raise
        # This is the irreversible commit point. No deletion until all configured models respond.
        if self.legacy_exists():
            run('sudo', 'systemctl', 'disable', '--now', legacy)
        say('Validation passed. Retiring explicitly listed legacy assets.')
        self.retire_ollama(cleanup.get('ollama_tags', []))
        self.retire_files(cleanup.get('model_files', []))
        self.retire_runtimes(cleanup.get('runtime_dirs', []))
        self.cleanup()
        self.wait_ready()
        run('df', '-h', Path.home())
        atomic(self.cfg / 'migration-complete.json', json.dumps({'legacy_service': legacy, 'completed': time.strftime('%Y-%m-%dT%H:%M:%S')}, indent=2))
        say('Clean migration complete. Legacy service definitions and system GPU libraries retained.')

    def reset(self, purge=False, uninstall=False):
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
            for p in (self.native, self.native.with_suffix('.previous.yaml'), self.active):
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
        for tool, flags in [('nvidia-smi', []), ('amd-smi', ['metric']), ('xpu-smi', ['discovery'])]:
            if shutil.which(tool):
                run(tool, *flags, check=False)
        if self.role == 'worker':
            if Path('/boot').exists():
                run('df', '-h', '/boot', check=False)
            run('systemctl', 'status', SERVICE, '--no-pager', check=False)
        elif self.role == 'gateway':
            say('Managed gateway loaded: ' + str(self.service_active()))
            for node, profile in self.c['nodes'].items():
                try:
                    expected = {r['model'] for r in self.c['routes'].values() if r['node'] == node}
                    node_cfg = self.c if node == self.a.node else yaml.safe_load((REPO / 'config' / (node + '.yaml')).read_text())
                    data = api(profile['url'].rstrip('/') + '/models', self.key('worker') if node_cfg['role'] == 'worker' else None, timeout=3)
                    advertised = {m['id'] for m in data['data']}
                    status = f'API reachable; advertised={sorted(advertised)}; missing={sorted(expected-advertised)}; extra/unrouted={sorted(advertised-expected)}'
                    if profile.get('enabled') and expected - advertised:
                        status = 'CATALOG DRIFT: ' + status
                except (OSError, ValueError, RuntimeError) as e:
                    status = f'API unavailable or incompatible ({e})'
                say(f'{node}: {status}; routing={profile.get("enabled", False)}')
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
    parser = argparse.ArgumentParser(description='Local AI cluster: one profile, native services, pinned artifacts.')
    parser.add_argument('--version', action='version', version='local-ai-cluster ' + RELEASE)
    parser.add_argument('node', choices=sorted(p.stem for p in (REPO / 'config').glob('*.yaml') if p.stem != 'versions'))
    group = parser.add_mutually_exclusive_group()
    for flag, helptext in [('install', 'Reconcile runtime and models; automatically migrate known legacy assets (default)'), ('migrate', 'Clean migration: validate then retire inventoried legacy assets'), ('models', 'Sync desired models and prune obsolete managed weights after validation'), ('cleanup', 'Remove obsolete managed model directories'), ('reset', 'Rebuild managed runtime; preserve models/cache'), ('uninstall', 'Remove managed runtime/service; preserve weights'), ('purge', 'Remove managed runtime and owned weights; preserve keys/cache'), ('start', 'Start managed service'), ('stop', 'Stop managed service'), ('health', 'Read-only system/API health'), ('validate', 'Execute inference on configured models; optionally select --model'), ('benchmark', 'Benchmark inference; --model required on primary workers'), ('calibrate', 'Measure real long-context prompts; optionally select --model'), ('init-keys', 'Create local gateway and worker keys on Mac')]:
        group.add_argument('--' + flag, dest='action', action='store_const', const=flag, help=helptext)
    parser.add_argument('--dry-run', action='store_true', help='Print desired state only; no managed changes')
    parser.add_argument('--yes', action='store_true', help='Approve listed managed operations noninteractively')
    parser.add_argument('--backend', choices=['vulkan', 'rocm', 'sycl', 'cuda', 'cuda-native'], help='Override worker backend for this run')
    parser.add_argument('--model', help='Alias for benchmark or smoke test')
    parser.add_argument('--prune', action='store_true', help='After successful worker model sync, remove obsolete managed weights')
    parser.add_argument('--reclaim-first', action='store_true', help='With --cleanup reclaim obsolete managed weights; with --migrate reclaim listed large legacy models before sync')
    parser.add_argument('--apply', action='store_true', help='Apply measured context/cache recommendation to source YAML; deploy separately')
    parser.add_argument('--cache-type', choices=['f16','q8_0','q4_0'], help='Test only this KV cache type during calibration')
    a = parser.parse_args()
    say('local-ai-cluster v' + RELEASE)
    a.action = a.action or 'install'
    if (a.apply or a.cache_type) and a.action != 'calibrate':
        parser.error('--apply and --cache-type require --calibrate')
    if a.reclaim_first and a.action not in ('cleanup', 'migrate', 'install', 'models'):
        parser.error('--reclaim-first requires --cleanup, --migrate, --install or --models')
    try:
        if not hasattr(tarfile, 'data_filter'):
            fail('Python lacks safe tar extraction; upgrade Python or use uv Python 3.12.')
        c = Cluster(a)
        if a.dry_run:
            c.plan()
            say('Requested action: ' + a.action)
            return
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
            if c.role == 'edge':
                from edge import Edge
                Edge(c).dispatch()
            elif a.action == 'init-keys':
                c.init_keys()
            elif a.action == 'migrate':
                c.migrate()
            elif a.action in ('install', 'models'):
                c.reconcile(install=a.action == 'install')
            elif a.action in ('reset', 'purge', 'uninstall'):
                c.reset(purge=a.action == 'purge', uninstall=a.action == 'uninstall')
            elif a.action == 'cleanup':
                c.cleanup()
            elif a.action == 'start':
                if c.migration_pending():
                    c.migrate()
                else:
                    c.start()
            elif a.action == 'stop':
                c.host_check(); c.service_owned(); c.stop()
            elif a.action == 'health':
                c.health()
            elif a.action == 'validate':
                c.validate()
            elif a.action == 'benchmark':
                c.benchmark()
            elif a.action == 'calibrate':
                c.calibrate()
    except KeyboardInterrupt:
        print('Interrupted; see service restoration messages above.', file=sys.stderr)
        sys.exit(130)
    except (RuntimeError, OSError, ValueError, KeyError, subprocess.CalledProcessError) as e:
        print('ERROR:', e, file=sys.stderr)
        sys.exit(1)


if __name__ == '__main__':
    main()
