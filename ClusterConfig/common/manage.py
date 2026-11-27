#!/usr/bin/env python3
"""Shared deployment logic (not a daemon). Edit config/<node>.yaml, not this file.
Uses native systemd/launchd and llama-swap/LiteLLM configs. Downloads verified
release/model files into ~/inference; only marked, repo-owned files are pruned.
Run a setup-<node>.sh --help for commands. Dry-run never calls service managers
or downloads model/runtime artifacts. The launcher may initialize uv's cache.
"""
import argparse
import fcntl
import hashlib
import json
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
        if node in ('gmini', 'p520', 'z820'):
            profile = yaml.safe_load((profiles / (node + '.yaml')).read_text())
            models = {a: {'model': a} for a, m in profile['models'].items() if m.get('enabled', True)}
        elif node == 'macmini':
            models = config.get('local_models', {})
        elif node in ('nano', 'ventunoq'):
            models = {a: {'model': m} for a, m in n.get('models', {}).items()}
            if not models:
                fail('Enabled edge node requires explicit model IDs: ' + node)
        else:
            fail('Unknown gateway node: ' + node)
        for alias, model in models.items():
            if not valid_alias(alias, node):
                fail('Invalid server-qualified route: ' + alias)
            routes[alias] = dict(model, url=url, node=node, external=node in ('macmini', 'nano', 'ventunoq'))
    for alias, target in config.get('aliases', {}).items():
        if not SAFE.fullmatch(alias) or target not in routes or alias in routes:
            fail('Invalid optional alias: ' + alias)
        routes[alias] = dict(routes[target])
    if not routes:
        fail('No gateway routes enabled.')
    return routes


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
            '--alias', alias, '--n-gpu-layers', '999', '--ctx-size', str(int(spec['context'])),
            '--parallel', '1', '--jinja', '--flash-attn', str(spec.get('flash_attention', 'auto')),
            '--no-context-shift', '--fit', 'off',
            '--cache-type-k', spec.get('cache_type_k', 'f16'), '--cache-type-v', spec.get('cache_type_v', 'f16')]
    proj = [r['name'] for r in records if 'mmproj' in r['name'].lower()]
    if proj:
        args += ['--mmproj', str(folder / proj[0])]
    return args + [str(x) for x in spec.get('args', [])]


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
    req = urllib.request.Request(url, headers={'User-Agent': 'local-ai-cluster/0.3', **(headers or {})})
    with urllib.request.urlopen(req, timeout=30) as response:
        return response.read()


def get_json(url, headers=None):
    return json.loads(get_bytes(url, headers))


def api(url, key=None, payload=None, timeout=10):
    headers = {'Content-Type': 'application/json'}
    if key:
        headers['Authorization'] = 'Bearer ' + key
    req = urllib.request.Request(url, headers=headers,
                                 data=json.dumps(payload).encode() if payload is not None else None)
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.load(r)


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
        fail(f'Insufficient space for {dest.name}; clean up explicitly, then retry.')
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
        # Only standard library tags are supported. OCI digest is pinned to inventory.
        name, tag = src['ollama'].split(':', 1)
        if not SAFE.fullmatch(name) or not SAFE.fullmatch(tag) or not SHA.fullmatch(src['digest']):
            fail('Ollama source requires library-name:tag and a pinned manifest digest.')
        base = f'https://registry.ollama.ai/v2/library/{name}'
        local = store / 'manifests/registry.ollama.ai/library' / name / tag
        raw = local.read_bytes() if local.is_file() else None
        if raw is None or hashlib.sha256(raw).hexdigest() != src['digest']:
            headers = {'Accept': 'application/vnd.docker.distribution.manifest.v2+json, application/vnd.oci.image.manifest.v1+json'}
            try:
                raw = get_bytes(base + '/manifests/sha256:' + src['digest'], headers)
            except urllib.error.HTTPError:
                raw = get_bytes(base + '/manifests/' + tag, headers)
        if hashlib.sha256(raw).hexdigest() != src['digest']:
            fail('Ollama tag changed; review the digest in YAML (no silent upgrade).')
        layers = [x for x in json.loads(raw)['layers'] if x['mediaType'] == 'application/vnd.ollama.image.model']
        if len(layers) != 1:
            fail('Ollama import requires one GGUF model layer; use pinned HF files for split/other formats.')
        layer = layers[0]
        dg = layer['digest'].removeprefix('sha256:')
        return [(name + '-' + tag + '.gguf', int(layer['size']), dg,
                 base + '/blobs/sha256:' + dg, [store / 'blobs' / ('sha256-' + dg)])]
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
        candidates = [store / 'blobs' / ('sha256-' + dg)]
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
        self.store = Path(self.c.get('ollama_store', '/usr/share/ollama/.ollama/models')).expanduser()
        if self.c['node'] != args.node:
            fail('Profile node mismatch.')
        if any(not valid_alias(k, args.node) for k in self.c.get('models', {})):
            fail('Model IDs must be node/model-name with safe characters.')
        if self.role == 'gateway':
            self.c['routes'] = gateway_routes(self.c, REPO / 'config')
        if self.role == 'worker' and self.backend not in ('vulkan', 'cuda', 'cuda-native', 'rocm', 'sycl'):
            fail('Unsupported backend: ' + self.backend)
        allowed = {'gmini': ('vulkan', 'rocm'), 'p520': ('vulkan', 'sycl'), 'z820': ('cuda', 'cuda-native')}
        if self.role == 'worker' and self.backend not in allowed[args.node]:
            fail('Backend not supported by this node profile.')
        if self.role == 'worker' and not self.desired:
            fail('Enable at least one worker model.')
        if self.role != 'edge':
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
            vendor = {'gmini': '0x1002', 'p520': '0x8086', 'z820': '0x10de'}[self.a.node]
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

    def plan(self):
        say(f'{self.a.node}: {self.role}; {self.endpoint}; root={self.root}')
        if self.role == 'worker':
            say(f'Runtime: {self.v["llama_cpp"]} {self.backend}; llama-swap {self.v["llama_swap"]}')
            for alias, m in self.desired.items():
                folder = self.models / model_id(m)
                say(f'{"KEEP" if owned(folder) and (folder / "weights.json").exists() else "SYNC"} {alias}: {m["source"]} (ctx {m["context"]})')
            for p in self.obsolete():
                say('PRUNE CANDIDATE ' + str(p))
            say('Legacy service: ' + self.c.get('legacy_service', 'none') + ' (retired only by --migrate)')
        elif self.role == 'gateway':
            say(f'LiteLLM {self.v["litellm"]}; routes:')
            for k, v in self.c['routes'].items():
                say(f'  {k} -> {v["url"]} / {v["model"]}')
        else:
            say('Vendor runtime is external; optional Ollama models: ' + str(self.c['ollama_models']))
        if getattr(self.a, 'action', '') == 'migrate':
            say('MIGRATION: stop legacy service, validate new inference, then retire listed legacy assets.')
            for kind, items in self.c.get('legacy_cleanup', {}).items():
                for item in items:
                    say(f'  {kind}: {item}')
            if self.c.get('legacy_launch_agent'):
                say('  retire launch agent: ' + self.c['legacy_launch_agent'])
        say('No partition, boot, driver or unrelated application changes.')

    def obsolete(self):
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
                    # Python >=3.12 data filter blocks path traversal and unsafe links.
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
            flags = ['-DGGML_CUDA=ON', '-DCMAKE_CUDA_ARCHITECTURES=86', '-DCMAKE_CUDA_COMPILER=' + nvcc]
        else:
            hip = Path('/opt/rocm/bin/hipcc')
            if not hip.is_file():
                fail('ROCm toolkit missing; install vendor-supported ROCm for gfx1151 first.')
            flags = ['-DGGML_HIP=ON', '-DGPU_TARGETS=gfx1151', '-DCMAKE_PREFIX_PATH=/opt/rocm',
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
        self.key('worker')
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
            expect = {'gmini': r'AMD|Radeon|gfx1151', 'p520': r'Intel|BMG|Arc', 'z820': r'A4500|NVIDIA'}[self.a.node]
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
                    if source.is_file() and source.stat().st_size == size and sha(source) == digest:
                        check_gguf(source)
                        try:
                            os.link(source, dest)
                        except OSError:
                            # Never symlink to an Ollama blob: removing the old tag could break inference.
                            if source.is_relative_to(self.store) and source.stat().st_dev == folder.stat().st_dev:
                                say('Hardlinking verified Ollama blob using sudo (no duplicate disk allocation).')
                                run('sudo', 'ln', '--', source, dest)
                            else:
                                if shutil.disk_usage(folder).free < size + self.c.get('reserve_gib', 15) * 2**30:
                                    fail('Not enough free disk to copy verified existing file.')
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
        config = {'healthCheckTimeout': 600, 'apiKeys': [key], 'startPort': 18081, 'models': {}}
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
        # Default llama-swap group policy swaps one model at a time.
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
        except Exception:
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
            data = f'''# local-ai-cluster\n[Unit]\nDescription=Local AI inference (llama-swap)\nAfter=network-online.target tailscaled.service\nWants=network-online.target\n\n[Service]\nUser={user}\nWorkingDirectory={self.root}\nExecStart={cmd}\nRestart=on-failure\nRestartSec=10\nTimeoutStopSec=120\n\n[Install]\nWantedBy=multi-user.target\n'''
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
            fail('Legacy GPU service still active. Review it and stop/disable it manually before cutover.')
        if not self.service_active():
            # Catch stale addresses and occupied ports without altering existing processes.
            with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
                s.bind((self.c['bind'], int(self.c['port'])))
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
                say('READY ' + self.endpoint + '/v1 (model listing; not a generation test)')
                return
            except (OSError, ValueError):
                time.sleep(2)
        fail('Service did not return a model list; inspect native logs.')

    def sync(self, install=False):
        self.prepare()
        if self.role == 'edge':
            say('Vendor software untouched. Use --models to pull optional Ollama tags on Nano.')
            if not install:
                self.pull_ollama()
            return
        self.service_owned()
        if self.role == 'gateway':
            if not install:
                self.pull_ollama()
                return
            self.install_gateway()
            config, active = self.render_gateway(), []
        else:
            if install:
                self.install_worker()
            config, active = self.render_worker()
        # Stage all weights first. Do not restart unchanged configuration.
        native_text = yaml.safe_dump(config, sort_keys=False)
        state_text = json.dumps({'models': active, 'backend': self.backend, 'endpoint': self.endpoint,
                                 'runtimes': [self.runtime.name, self.swap.name] if self.role == 'worker' else [self.venv.name]}, indent=2)
        unchanged = (self.native.is_file() and self.native.read_text() == native_text and
                     self.active.is_file() and self.active.read_text() == state_text and self.service_path().exists())
        was_running = self.service_active()
        if unchanged:
            say('KEEP configuration and service (unchanged).')
            if not was_running and not self.legacy_active():
                self.start()
        else:
            old_files = {p: p.read_bytes() for p in (self.native, self.active, self.service_path()) if p.is_file()}
            if was_running:
                confirm('Restart managed inference now? Active requests will be interrupted.', self.a.yes)
                self.stop()
            try:
                if self.native.exists():
                    atomic(self.native.with_suffix('.previous.yaml'), self.native.read_bytes())
                atomic(self.native, native_text)
                atomic(self.active, state_text)
                self.install_service()
                if not self.legacy_active():
                    self.start()
            except Exception:
                # Best-effort rollback of the prior configuration and native service.
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
                        # Restore via native manager: prior endpoint may differ from current YAML.
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
                raise
        if self.legacy_active():
            say('STAGED ONLY: stop/disable legacy GPU service manually, then run --start.')
            if self.a.prune:
                say('Prune skipped until managed inference is running and tested.')
        elif self.a.prune and self.role == 'worker' and getattr(self.a, 'action', '') != 'migrate':
            # Before destructive cleanup, exercise each configured model at least once.
            self.stop()
            self.start()
            self.smoke_all()
            self.calibrate(migration=True)
            self.cleanup()

    def pull_ollama(self):
        models = self.c.get('ollama_models', [])
        if not models:
            say('No Ollama models configured.')
            return
        if not shutil.which('ollama'):
            fail('Install/start vendor-supported Ollama first; existing vendor runtime is not replaced.')
        for model in models:
            run('ollama', 'pull', model)
        say('Existing Ollama tags are mutable; no automatic pruning of externally managed stores.')

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
            allowed = {'gmini': {'llama-rocm', 'llama-vulkan-gmini'}, 'p520': {'llama-vulkan'}, 'z820': set()}
            if path.parent != home or path.name not in allowed.get(self.a.node, set()):
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
            hits = re.findall(r'offloaded\s+(\d+)\s*/\s*(\d+)\s+layers\s+to\s+GPU', logs, re.I)
            if hits:
                actual, total = map(int, hits[-1])
                if total and actual / total >= minimum:
                    say(f'GPU offload {alias}: {actual}/{total} layers')
                    return
                fail(f'GPU offload below target for {alias}: {actual}/{total}; tune context or quantization.')
            time.sleep(2)
        fail('No GPU-layer offload evidence in journal for ' + alias + '; legacy assets preserved. Inspect logs and runtime compatibility.')

    def smoke_all(self):
        old = self.a.model
        try:
            for alias in self.desired if self.role == 'worker' else self.c['routes']:
                self.a.model = alias
                since = time.strftime('%Y-%m-%d %H:%M:%S')
                self.validate()
                if self.role == 'worker':
                    self.check_gpu_offload(since, alias)
        finally:
            self.a.model = old

    def migrate(self):
        """Install first, smoke test, then delete explicitly listed legacy assets."""
        self.prepare()
        self.plan()
        if self.role == 'edge':
            say('Edge vendor stack preserved. No destructive migration.')
            return
        confirm('Clean migration: retire the listed old services/models/runtimes after validation?', self.a.yes)
        if self.role == 'gateway':
            self.sync(install=True)
            # Offline workers are allowed; a reachable worker must pass through the gateway.
            old = self.a.model
            try:
                for alias, route in self.c['routes'].items():
                    try:
                        api(route['url'].rstrip('/') + '/models', None if route.get('external') else self.key('worker'), timeout=4)
                    except (OSError, ValueError) as e:
                        if route['node'] == 'macmini':
                            fail(f'Local gateway dependency unavailable: {alias}: {e}')
                        say(f'SKIP gateway route {alias}: worker unavailable ({e})')
                        continue
                    self.a.model = alias
                    self.validate()
            finally:
                self.a.model = old
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
        legacy = self.c.get('legacy_service')
        expected = {'gmini': 'ollama.service', 'p520': 'llama-swap.service', 'z820': 'ollama.service'}[self.a.node]
        if legacy != expected:
            fail('Unexpected legacy service; inspect node YAML.')
        cleanup = self.c.get('legacy_cleanup', {})
        names = {str(name).lower() for name in self.c.get('migration_hostnames', [self.a.node])}
        if socket.gethostname().split('.')[0].lower() not in names:
            fail('Migration hostname not allowed. Review legacy_cleanup and migration_hostnames in the node YAML, or use --install on a fresh host.')
        # Verify all destructive paths before making any changes.
        for value in cleanup.get('model_files', []) + cleanup.get('reclaim_first', []):
            self.legacy_path(value)
        for value in cleanup.get('runtime_dirs', []):
            self.legacy_path(value, runtime=True)
        self.install_worker()  # require working binary and visible GPU BEFORE deleting anything
        if self.a.reclaim_first:
            reclaim = cleanup.get('reclaim_first', [])
            if reclaim:
                for p in reclaim:
                    say('EARLY REMOVE ' + p)
                confirm('Delete listed oversized models before new inference is validated? No automatic recovery.', self.a.yes)
                self.retire_files(reclaim)
        was_active = self.legacy_active()
        # Stage verified model weights while the previous service is still available.
        self.sync(install=True)
        try:
            if was_active:
                run('sudo', 'systemctl', 'stop', legacy)
            if self.service_active():
                self.stop()
            self.start()
            self.smoke_all()
            # Validate real long prompts at the deployed settings before irreversible cleanup.
            self.calibrate(migration=True)
        except Exception:
            say('New inference failed; legacy assets retained (except explicit early reclaim).')
            if self.service_active():
                self.stop()
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
            self.sync(install=True)
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
                    data = api(profile['url'].rstrip('/') + '/models', self.key('worker') if node in ('gmini','p520','z820') else None, timeout=3)
                    status = f'API reachable ({len(data.get("data", []))} models)'
                except (OSError, ValueError, RuntimeError) as e:
                    status = f'API unavailable or incompatible ({e})'
                say(f'{node}: {status}; routing={profile.get("enabled", False)}')
        self.validate()

    def validate(self):
        if self.role == 'edge':
            try:
                data = api(self.endpoint + '/v1/models')
                say('Advertised models: ' + str([m['id'] for m in data.get('data', [])]))
            except (OSError, ValueError) as e:
                fail(f'Edge model-list API unavailable ({e}); verify vendor service locally.')
            return
        key = self.key('gateway' if self.role == 'gateway' else 'worker')
        data = api(self.endpoint + '/v1/models', key)
        ids = {m['id'] for m in data.get('data', [])}
        expected = set(self.desired if self.role == 'worker' else self.c['routes'])
        say('Advertised models: ' + ', '.join(sorted(ids)))
        if not expected.issubset(ids):
            fail('Missing advertised models: ' + str(expected - ids))
        if self.a.model:
            alias = self.a.model
            if alias not in ids:
                fail('Unknown model alias.')
            if self.role == 'gateway' and self.c['routes'][alias].get('embedding'):
                result = api(self.endpoint + '/v1/embeddings', key, {'model': alias, 'input': 'local cluster smoke test'}, 600)
                if not result.get('data') or not result['data'][0].get('embedding'):
                    fail('Empty embedding response.')
                say('Embedding dimensions: ' + str(len(result['data'][0]['embedding'])))
            else:
                result = api(self.endpoint + '/v1/chat/completions', key, {'model': alias, 'messages': [{'role': 'user', 'content': 'Reply with one word: ready.'}], 'max_tokens': 512, 'stream': False}, 600)
                if not result.get('choices') or not result['choices'][0].get('message', {}).get('content'):
                    fail('Empty completion response.')
                say('Completion smoke test: ' + json.dumps(result['choices'][0], ensure_ascii=False))
                say('Usage: ' + str(result.get('usage')))
        else:
            say('API discovery passed. Use --validate --model ALIAS to actually run inference.')

    def benchmark(self):
        if self.role != 'worker' or self.a.model not in self.desired:
            fail('Use --benchmark --model ALIAS on a primary worker.')
        self.host_check()
        if self.legacy_active():
            fail('Stop legacy GPU service before benchmarking.')
        folder = self.models / model_id(self.desired[self.a.model])
        records = json.loads((folder / 'weights.json').read_text())
        weight = folder / next(x['name'] for x in records if 'mmproj' not in x['name'].lower())
        bench = self.locate(self.runtime, 'llama-bench')
        confirm('Stop managed inference temporarily and run benchmark?', self.a.yes)
        was_running = self.service_active()
        if was_running:
            self.stop()
        try:
            out = self.root / 'benchmarks' / f'{self.a.node}-{self.backend}-{self.a.model.replace(chr(47), chr(45))}-{time.strftime("%Y%m%d-%H%M%S")}.txt'
            out.parent.mkdir(exist_ok=True)
            command = [str(bench), '-m', str(weight), '-ngl', '999', '-p', '512,8192', '-n', '128', '-r', '3']
            with out.open('w') as f:
                f.write(json.dumps({'command': command, 'version': self.v['llama_cpp'], 'weights': records, 'kernel': platform.release()}, indent=2) + '\n')
                f.flush()
                p = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, env=self.runtime_env())
                for line in p.stdout:
                    print(line, end='', flush=True)
                    f.write(line)
                if p.wait():
                    fail('Benchmark failed; see ' + str(out))
            say('Benchmark: ' + str(out))
        finally:
            if was_running:
                self.start()


    def calibrate(self, migration=False):
        from calibrate import calibrate_cluster
        calibrate_cluster(self, migration=migration)


def main():
    parser = argparse.ArgumentParser(description='Local AI cluster: one profile, native services, pinned artifacts.')
    parser.add_argument('node', choices=['macmini', 'gmini', 'p520', 'z820', 'nano', 'ventunoq'])
    group = parser.add_mutually_exclusive_group()
    for flag, helptext in [('install', 'Install/reconcile runtime, model files and service (default)'), ('migrate', 'Clean migration: validate then retire inventoried legacy assets'), ('models', 'Sync model list; worker re-renders and reloads managed service'), ('cleanup', 'Remove obsolete managed model directories'), ('reset', 'Rebuild managed runtime; preserve models/cache'), ('uninstall', 'Remove managed runtime/service; preserve weights'), ('purge', 'Remove managed runtime and owned weights; preserve keys/cache'), ('start', 'Start managed service'), ('stop', 'Stop managed service'), ('health', 'Read-only system/API health'), ('validate', 'Check API; --model enables a real inference smoke test'), ('benchmark', 'Run llama-bench; --model required'), ('calibrate', 'Measure real long-context prompts; optionally select --model'), ('init-keys', 'Create local gateway and worker keys on Mac')]:
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
    a.action = a.action or 'install'
    if (a.apply or a.cache_type) and a.action != 'calibrate':
        parser.error('--apply and --cache-type require --calibrate')
    if a.reclaim_first and a.action not in ('cleanup', 'migrate'):
        parser.error('--reclaim-first requires --cleanup or --migrate')
    try:
        c = Cluster(a)
        if a.dry_run:
            c.plan()
            say('Requested action: ' + a.action)
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
            if a.action == 'init-keys':
                c.init_keys()
            elif a.action == 'migrate':
                c.migrate()
            elif a.action in ('install', 'models'):
                c.plan()
                confirm('Apply this configuration (may download large files and restart managed service)?', a.yes)
                c.sync(install=a.action == 'install')
            elif a.action in ('reset', 'purge', 'uninstall'):
                c.reset(purge=a.action == 'purge', uninstall=a.action == 'uninstall')
            elif a.action == 'cleanup':
                c.cleanup()
            elif a.action == 'start':
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
    except (RuntimeError, OSError, ValueError, KeyError, subprocess.CalledProcessError) as e:
        print('ERROR:', e, file=sys.stderr)
        sys.exit(1)


if __name__ == '__main__':
    main()
