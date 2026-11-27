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

    def test_exact_upstream_hash_and_hardlink_reuse(self):
        blob = b'GGUF' + b'fixture'
        digest = hashlib.sha256(blob).hexdigest()
        store = self.home / 'ollama'
        source = store / 'blobs' / ('sha256-' + digest)
        source.parent.mkdir(parents=True)
        source.write_bytes(blob)
        rev = 'a' * 40
        spec = {'source': {'repo': 'demo/model', 'revision': rev, 'files': ['model.gguf']}, 'context': 1024}
        self.c.store = store
        meta = {'sha': rev, 'siblings': [{'rfilename': 'model.gguf', 'lfs': {'sha256': digest, 'size': len(blob)}}]}
        with patch.object(m, 'get_json', return_value=meta):
            directory, records = self.c.sync_model('demo', spec)
            self.assertEqual((directory / records[0]['name']).stat().st_ino, source.stat().st_ino)
            with patch.object(m, 'model_sources', side_effect=AssertionError('unexpected metadata refresh')):
                self.assertEqual(self.c.sync_model('demo', spec)[0], directory)
        source.unlink()
        self.assertEqual((directory / records[0]['name']).read_bytes(), blob)
        with self.assertRaisesRegex(RuntimeError, 'raw Ollama layers'):
            m.model_sources({'source': {'ollama': 'demo:tag', 'digest': digest}}, store)

    def test_render_worker_forwards_upstream_logs(self):
        from unittest.mock import MagicMock
        c = self.c
        c.locate = MagicMock(return_value=Path('/bin/llama-server'))
        c.key = MagicMock(return_value='sk-test')
        c.sync_model = MagicMock(return_value=(Path('/models'), [{'name': 'model.gguf'}]))
        c.runtime_env = MagicMock(return_value={})
        config, _ = c.render_worker()
        self.assertEqual(config['logToStdout'], 'both')
        self.assertEqual(config['captureBuffer'], 0)

    def test_disabled_model_cache_is_preserved(self):
        disabled = next(v for v in self.c.c['models'].values() if not v.get('enabled', True))
        path = self.c.models / m.model_id(disabled)
        path.mkdir(); m.atomic(path / m.MARK, 'local-ai-cluster\n')
        self.assertNotIn(path, self.c.obsolete())

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
        c.install_worker = MagicMock(); c.sync = MagicMock(); c.key = MagicMock()
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

    def test_explicit_model_logging_and_offload_parser(self):
        cmd = m.model_command('/bin/server', Path('/models'), [{'name':'model.gguf'}], 'z820/model', {'context':8192}, 12345)
        self.assertEqual(cmd[-5:], ['--log-verbosity','4','--log-colors','off','--no-log-jsonl'])
        self.assertEqual(m.offload_counts('load_tensors: offloaded \x1b[32m25/25\x1b[0m layers to GPU'), (25,25))
        self.assertEqual(m.offload_counts('offloaded 25/25 layers to GPU\noffloaded 0/25 layers to GPU'), (0,25))
        self.assertIsNone(m.offload_counts('Available devices: CUDA0'))

    def test_http_error_preserves_status_and_redacts_key(self):
        key = 'sk-test-secret'
        e = m.urllib.error.HTTPError('http://localhost/v1/chat/completions', 500, 'Internal Server Error', {}, io.BytesIO(('bad tensor ' + key).encode()))
        with patch.object(m.urllib.request,'urlopen',side_effect=e):
            with self.assertRaises(m.urllib.error.HTTPError) as caught:
                m.api('http://localhost/v1/chat/completions',key,{})
        self.assertEqual(caught.exception.code,500)
        self.assertIn('bad tensor [REDACTED]',str(caught.exception))
        self.assertNotIn(key,str(caught.exception))

    def test_full_gpu_offload_required(self):
        from types import SimpleNamespace
        with patch.object(m, 'run', return_value=SimpleNamespace(stdout='load_tensors: offloaded 20/40 layers to GPU')):
            with self.assertRaisesRegex(RuntimeError, 'below target'):
                self.c.check_gpu_offload('2026-11-27 00:00:00', 'gmini/qwen3-coder-next-80b-q4_k_m')
        with patch.object(m, 'run', return_value=SimpleNamespace(stdout='load_tensors: offloaded 40/40 layers to GPU')):
            self.c.check_gpu_offload('2026-11-27 00:00:00', 'gmini/qwen3-coder-next-80b-q4_k_m')

    def test_native_model_log_independent_of_journal(self):
        c=self.c
        alias='gmini/qwen3-coder-next-80b-q4_k_m'
        c.cfg.mkdir(parents=True,exist_ok=True)
        c.model_log(alias).write_text('load_tensors: offloaded 49/49 layers to GPU')
        with patch.object(m,'run') as run:
            c.check_gpu_offload('2026-11-27 00:00:00',alias)
            run.assert_not_called()
        c.model_log(alias).write_text('load_tensors: offloaded 0/49 layers to GPU')
        with patch.object(m,'run') as run:
            with self.assertRaisesRegex(RuntimeError,'below target'):
                c.check_gpu_offload('2026-11-27 00:00:00',alias)
            run.assert_not_called()

    def test_calibration_isolation_restores_old_service_on_interrupt(self):
        from unittest.mock import MagicMock
        c = self.c
        c.legacy_active = MagicMock(side_effect=[True, False])
        c.service_active = MagicMock(side_effect=[False, False])
        with patch.object(m, 'run') as run:
            with self.assertRaises(KeyboardInterrupt):
                with c.isolated_gpu():
                    raise KeyboardInterrupt()
            self.assertEqual([x.args for x in run.call_args_list], [
                ('sudo', 'systemctl', 'stop', 'ollama.service'),
                ('sudo', 'systemctl', 'start', 'ollama.service')])

    def test_isolation_restores_both_services_on_success(self):
        from unittest.mock import MagicMock
        c = self.c
        c.legacy_active = MagicMock(side_effect=[True, False])
        c.service_active = MagicMock(side_effect=[True, False])
        c.stop = MagicMock()
        with patch.object(m, 'run') as run:
            with c.isolated_gpu():
                c.stop.assert_called_once()
            run.assert_any_call('sudo', 'systemctl', 'start', m.SERVICE)
            run.assert_any_call('sudo', 'systemctl', 'start', 'ollama.service')

    def test_isolation_refuses_to_benchmark_if_legacy_still_active(self):
        from unittest.mock import MagicMock
        c = self.c
        c.legacy_active = MagicMock(return_value=True)
        c.service_active = MagicMock(return_value=False)
        with patch.object(m, 'run') as run:
            with self.assertRaisesRegex(RuntimeError, 'Could not isolate'):
                with c.isolated_gpu():
                    self.fail('GPU test must not start')
            run.assert_any_call('sudo', 'systemctl', 'start', 'ollama.service')

    def test_default_reconcile_automatically_migrates(self):
        from unittest.mock import MagicMock
        c = self.c
        c.migration_pending = MagicMock(return_value=True)
        c.migrate = MagicMock(); c.sync = MagicMock()
        c.reconcile()
        c.migrate.assert_called_once_with(); c.sync.assert_not_called()

    def test_migration_receipt_skips_disabled_legacy_but_not_reactivated(self):
        from unittest.mock import MagicMock
        c = self.c
        c.legacy_active = MagicMock(return_value=False)
        c.legacy_exists = MagicMock(return_value=True)
        self.assertTrue(c.migration_pending())
        m.atomic(c.cfg / 'migration-complete.json', '{}')
        self.assertFalse(c.migration_pending())
        c.legacy_active.return_value = True
        self.assertTrue(c.migration_pending())

    def test_default_reconcile_reclaims_only_after_disk_failure(self):
        from unittest.mock import MagicMock
        c = self.c
        c.migration_pending = MagicMock(return_value=False)
        c.sync = MagicMock(side_effect=[m.DiskSpaceError('disk'), None])
        c.obsolete = MagicMock(return_value=[c.models/'old'])
        c.cleanup = MagicMock(side_effect=lambda: self.assertTrue(c.a.reclaim_first))
        c.reconcile()
        self.assertEqual(c.sync.call_count, 2)
        c.cleanup.assert_called_once_with()
        self.assertFalse(c.a.reclaim_first)

    def test_migration_reclaims_listed_weights_then_validates_before_retirement(self):
        from unittest.mock import MagicMock
        c = self.c
        old = self.home / 'models' / 'oversized.gguf'
        old.parent.mkdir(); old.write_bytes(b'GGUF')
        c.c['legacy_cleanup'] = {'reclaim_first': [str(old)], 'model_files': [], 'runtime_dirs': [], 'ollama_tags': ['old:tag']}
        events = []
        c.key = MagicMock(); c.install_worker = MagicMock()
        c.sync = MagicMock(side_effect=[m.DiskSpaceError('disk'), None])
        c.legacy_active = MagicMock(return_value=True)
        c.service_active = MagicMock(return_value=False)
        c.legacy_exists = MagicMock(return_value=True)
        c.start = MagicMock(); c.stop = MagicMock()
        c.smoke_all = MagicMock(side_effect=lambda: events.append('smoke'))
        c.calibrate = MagicMock(side_effect=lambda **kw: events.append('context'))
        c.retire_files = MagicMock(side_effect=lambda paths: events.append(('files', paths)))
        c.retire_ollama = MagicMock(side_effect=lambda tags: events.append('retire'))
        c.retire_runtimes = MagicMock(); c.cleanup = MagicMock(); c.wait_ready = MagicMock()
        with patch.object(m, 'run'):
            c.migrate()
        self.assertEqual(events[:4], [('files', [str(old)]), 'smoke', 'context', 'retire'])
        self.assertEqual(c.sync.call_count, 2)
        self.assertTrue((c.cfg / 'migration-complete.json').is_file())

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
