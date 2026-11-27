#!/usr/bin/env python3
"""Offline safety tests; run python3 common/test_manage.py. No services or network."""
import contextlib
import hashlib
import io
import json
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
import manage as m


class SafetyTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.home = Path(self.temp.name)
        self.patch = patch.object(m.Path, 'home', return_value=self.home)
        self.patch.start()
        self.args = SimpleNamespace(node='gmini', backend=None, yes=True, reclaim_first=False, prune=False, model=None)
        self.c = m.Cluster(self.args)
        self.c.host_check = lambda: None
        self.c.prepare()

    def tearDown(self):
        self.patch.stop()
        self.temp.cleanup()

    def test_all_profiles(self):
        for node in ['macmini', 'gmini', 'p520', 'z820', 'nano', 'ventunoq']:
            self.args.node = node
            c = m.Cluster(self.args)
            self.assertEqual(c.c['node'], node)
            with contextlib.redirect_stdout(io.StringIO()):
                c.plan()

    def test_ollama_manifest_local_and_hardlink_reuse(self):
        blob = b'GGUF' + b'fixture'
        digest = hashlib.sha256(blob).hexdigest()
        store = self.home / 'ollama'
        source = store / 'blobs' / ('sha256-' + digest)
        source.parent.mkdir(parents=True)
        source.write_bytes(blob)
        manifest = store / 'manifests/registry.ollama.ai/library/demo/tag'
        manifest.parent.mkdir(parents=True)
        manifest.write_text(json.dumps({'layers': [{'mediaType': 'application/vnd.ollama.image.model', 'digest': 'sha256:' + digest, 'size': len(blob)}]}))
        spec = {'source': {'ollama': 'demo:tag', 'digest': m.sha(manifest)}, 'context': 1024}
        self.c.store = store
        with patch.object(m, 'get_bytes', side_effect=AssertionError('unexpected network')):
            directory, records = self.c.sync_model('demo', spec)
            self.assertEqual((directory / records[0]['name']).stat().st_ino, source.stat().st_ino)
            with patch.object(m, 'model_sources', side_effect=AssertionError('unexpected metadata refresh')):
                self.assertEqual(self.c.sync_model('demo', spec)[0], directory)
        source.unlink()
        self.assertEqual((directory / records[0]['name']).read_bytes(), blob)

    def test_cleanup_refuses_active_and_preserves_unowned(self):
        old = self.c.models / 'old-managed'
        old.mkdir(); m.atomic(old / m.MARK, 'local-ai-cluster\n')
        external = self.c.models / 'unrelated'
        external.mkdir(); (external / 'my.gguf').write_bytes(b'GGUF')
        m.atomic(self.c.active, json.dumps({'models': [old.name]}))
        with patch.object(self.c, 'service_active', return_value=False):
            with self.assertRaisesRegex(RuntimeError, 'still references'):
                self.c.cleanup()
            m.atomic(self.c.active, json.dumps({'models': []}))
            self.c.cleanup()
        self.assertFalse(old.exists())
        self.assertTrue(external.exists())

    def test_remove_owned_rejects_unmarked_and_symlink(self):
        external = self.home / 'external'
        external.mkdir()
        with self.assertRaises(RuntimeError):
            m.remove_owned(external)
        m.atomic(external / m.MARK, 'local-ai-cluster\n')
        link = self.home / 'link'
        link.symlink_to(external, target_is_directory=True)
        with self.assertRaises(RuntimeError):
            m.remove_owned(link)
        self.assertTrue(external.exists())

    def test_source_change_changes_identity(self):
        spec = self.c.desired['gmini/qwen3-coder-next-80b-q4_k_m']
        changed = {'source': dict(spec['source'], digest='0' * 64), 'context': spec['context']}
        self.assertNotEqual(m.model_id(spec), m.model_id(changed))
        self.assertEqual(m.model_id(spec), m.model_id(dict(spec, context=65536)))

    def test_gateway_config_secrets_and_models(self):
        self.args.node = 'macmini'
        c = m.Cluster(self.args)
        for name in ['worker', 'gateway']:
            m.atomic(c.cfg / (name + '.key'), 'sk-' + name + 'x' * 40)
        config = c.render_gateway()
        self.assertEqual(len(config['model_list']), len(c.c['routes']))
        embedding = next(x for x in config['model_list'] if x['model_name'] == 'macmini/qwen3-embedding-0.6b-q8_0')
        self.assertEqual(embedding['model_info']['mode'], 'embedding')
        self.assertTrue(config['general_settings']['master_key'].startswith('sk-'))

    def test_dry_run_no_writes(self):
        before = list(self.home.rglob('*'))
        with contextlib.redirect_stdout(io.StringIO()):
            self.c.plan()
        self.assertEqual(before, list(self.home.rglob('*')))

    def test_legacy_path_rejects_symlink_and_unrelated_files(self):
        root = self.home / 'models'; root.mkdir()
        good = root / 'old.gguf'; good.write_bytes(b'GGUF')
        unrelated = self.home / 'notes.txt'; unrelated.write_text('keep')
        link = root / 'link.gguf'; link.symlink_to(unrelated)
        for path in (unrelated, link, self.home / 'models/../notes.gguf'):
            with self.assertRaises(RuntimeError):
                self.c.legacy_path(str(path))
        self.assertEqual(self.c.legacy_path(str(good)), good)
        self.assertTrue(unrelated.exists())

    def test_migration_never_retires_when_smoke_fails(self):
        from unittest.mock import MagicMock
        c = self.c
        c.c['legacy_cleanup'] = {'model_files': [], 'runtime_dirs': [], 'ollama_tags': []}
        c.install_worker = MagicMock(); c.sync = MagicMock()
        c.legacy_active = MagicMock(return_value=True)
        c.service_active = MagicMock(return_value=True)
        c.start = MagicMock(); c.stop = MagicMock()
        c.smoke_all = MagicMock(side_effect=RuntimeError('smoke failed'))
        c.retire_files = MagicMock(); c.retire_ollama = MagicMock(); c.retire_runtimes = MagicMock()
        with patch.object(m.socket, 'gethostname', return_value='gmkmini'), patch.object(m, 'run') as run:
            with self.assertRaisesRegex(RuntimeError, 'smoke failed'):
                c.migrate()
            run.assert_any_call('sudo', 'systemctl', 'start', 'ollama.service')
        c.retire_files.assert_not_called(); c.retire_ollama.assert_not_called(); c.retire_runtimes.assert_not_called()

    def test_full_gpu_offload_required(self):
        from types import SimpleNamespace
        with patch.object(m, 'run', return_value=SimpleNamespace(stdout='load_tensors: offloaded 20/40 layers to GPU')):
            with self.assertRaisesRegex(RuntimeError, 'below target'):
                self.c.check_gpu_offload('2026-11-27 00:00:00', 'gmini/qwen3-coder-next-80b-q4_k_m')
        with patch.object(m, 'run', return_value=SimpleNamespace(stdout='load_tensors: offloaded 40/40 layers to GPU')):
            self.c.check_gpu_offload('2026-11-27 00:00:00', 'gmini/qwen3-coder-next-80b-q4_k_m')

    def test_checksum_rejects_bad_download(self):
        target = self.c.cache / 'test.gguf'
        def fake(*args, **kwargs):
            target.with_name(target.name + '.partial').write_bytes(b'wrong')
        with patch.object(m, 'run', side_effect=fake):
            with self.assertRaisesRegex(RuntimeError, 'checksum'):
                m.download('https://example.invalid/test', target, '0'*64, 5)
        self.assertFalse(target.exists())


if __name__ == '__main__':
    unittest.main(verbosity=2)
