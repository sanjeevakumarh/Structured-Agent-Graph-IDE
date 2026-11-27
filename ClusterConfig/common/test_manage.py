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
from unittest.mock import patch, MagicMock
import seed
import manage as m


class SafetyTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.home = Path(self.temp.name)
        self.patch = patch.object(m.Path, 'home', return_value=self.home)
        self.patch.start()
        self.args = SimpleNamespace(dry_run=True,node='gmini', action='install', backend=None, yes=True, reclaim_first=False, prune=False, model=None)
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

    def test_disabled_model_cache_is_obsolete(self):
        alias=next(iter(self.c.desired)); spec=self.c.desired.pop(alias)
        path=self.c.models / m.model_id(spec);path.mkdir();m.atomic(path/m.MARK,'local-ai-cluster\n')
        self.assertIn(path,self.c.obsolete())

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
        root=self.home/'models';root.mkdir(); unrelated=self.home/'notes';unrelated.mkdir()
        self.c.c['legacy_cleanup']={'directories':[str(unrelated)]}
        with self.assertRaisesRegex(RuntimeError,'allowlist'):seed.cleanup_plan(self.c)
        root.rmdir();root.symlink_to(unrelated,target_is_directory=True)
        self.c.c['legacy_cleanup']={'directories':[str(root)]}
        with self.assertRaisesRegex(RuntimeError,'Unsafe'):seed.cleanup_plan(self.c)
        self.assertTrue(unrelated.exists())

    def test_seed_retires_before_failure_and_never_restores(self):
        c=self.c;old=self.home/'models';old.mkdir();(old/'old.gguf').write_bytes(b'old')
        c.c['legacy_cleanup']={'directories':[str(old)],'services':['ollama.service']}
        c.key=MagicMock();c.stop=MagicMock();c.service_path=lambda:self.home/'missing.service'
        c.sync_model=MagicMock();c.sync=MagicMock(side_effect=RuntimeError('smoke failed'))
        m.atomic(c.native,'OLD CONFIG');m.atomic(c.active,'{}')
        with patch.object(seed,'model_sources',return_value=[]),patch.object(seed,'retire_unit') as retire,patch.object(seed,'run') as run:
            with self.assertRaisesRegex(RuntimeError,'smoke failed'): seed.deploy(c)
            retire.assert_called_once_with('ollama.service')
            self.assertFalse(old.exists());self.assertFalse(c.native.exists());self.assertFalse(c.active.exists())
            self.assertFalse((c.cfg/'seed-complete.json').exists())
            self.assertFalse(any('start' in call.args for call in run.call_args_list))

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

    def test_calibration_isolation_resumes_only_managed_on_interrupt(self):
        c=self.c;c.legacy_active=MagicMock(return_value=False);c.service_active=MagicMock(side_effect=[True,False]);c.stop=MagicMock()
        with patch.object(m,'run') as run:
            with self.assertRaises(KeyboardInterrupt):
                with c.isolated_gpu():raise KeyboardInterrupt()
            c.stop.assert_called_once();run.assert_called_once_with('sudo','systemctl','start',m.SERVICE)

    def test_isolation_resumes_managed_on_success(self):
        c=self.c;c.legacy_active=MagicMock(return_value=False);c.service_active=MagicMock(side_effect=[True,False]);c.stop=MagicMock()
        with patch.object(m,'run') as run:
            with c.isolated_gpu():pass
            c.stop.assert_called_once();run.assert_called_once_with('sudo','systemctl','start',m.SERVICE)

    def test_isolation_refuses_to_benchmark_if_legacy_still_active(self):
        c=self.c;c.legacy_active=MagicMock(return_value=True)
        with patch.object(m,'run') as run:
            with self.assertRaisesRegex(RuntimeError,'Legacy service is active'):
                with c.isolated_gpu():self.fail('GPU test must not start')
            run.assert_not_called()

    def test_default_reconcile_calls_seed(self):
        with patch.object(seed,'deploy') as deploy:
            self.c.reconcile();deploy.assert_called_once_with(self.c)

    def test_seed_receipt_requires_new_seed_if_legacy_reactivated(self):
        c=self.c;c.legacy_active=MagicMock(return_value=False)
        self.assertTrue(c.migration_pending());m.atomic(c.cfg/'seed-complete.json','{}')
        self.assertFalse(c.migration_pending());c.legacy_active.return_value=True
        self.assertTrue(c.migration_pending())

    def test_seed_preflight_failure_preserves_files_and_stops_before_deletion(self):
        c=self.c;c.key=MagicMock();c.stop=MagicMock();c.service_path=lambda:self.home/'missing.service'
        old=self.home/'models';old.mkdir();c.c['legacy_cleanup']={'directories':[str(old)]}
        with patch.object(seed,'model_sources',side_effect=RuntimeError('metadata unavailable')),patch.object(seed,'run') as run:
            with self.assertRaisesRegex(RuntimeError,'metadata unavailable'):seed.deploy(c)
            self.assertTrue(old.exists());c.stop.assert_not_called();run.assert_not_called()

    def test_seed_reclaims_before_download_and_validation(self):
        c=self.c;old=self.home/'models';old.mkdir();(old/'old.gguf').write_bytes(b'old')
        c.c['legacy_cleanup']={'directories':[str(old)]};c.key=MagicMock();c.stop=MagicMock();c.service_path=lambda:self.home/'missing.service';c.sync_model=MagicMock()
        def install(**kw): self.assertFalse(old.exists());self.assertFalse(c._adopt_only)
        c.sync=MagicMock(side_effect=install)
        with patch.object(seed,'model_sources',return_value=[]),patch.object(seed,'run'):
            seed.deploy(c)
        self.assertTrue((c.cfg/'seed-complete.json').exists());c.sync.assert_called_once_with(install=True)

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
