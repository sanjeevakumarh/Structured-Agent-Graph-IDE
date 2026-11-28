"""Offline mode safety tests. Portable; no SSH, GPU, service or downloads."""
import argparse
import contextlib
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import types
import profile_fixtures as fixtures
import unittest
from unittest.mock import patch, Mock

import modes as m


class ModesTests(unittest.TestCase):
    def test_idle_empty(self):
        with patch.object(m, 'http', side_effect=[{'running': []}, {'queue_running': [], 'queue_pending': []}]):
            m.idle(True, True, 'http://worker', 'key')

    def test_loaded_llm_refused_even_if_idle(self):
        with patch.object(m, 'http', return_value={'running': [{'model': 'a', 'state': 'ready'}]}):
            with self.assertRaisesRegex(RuntimeError, 'loaded/busy'):
                m.idle(True, False, 'http://worker', 'key')

    def test_busy_media_and_unknown_queue_refused(self):
        for result in ({'queue_running': [1], 'queue_pending': []},
                       {'queue_running': [], 'queue_pending': [1]}, {}, {'queue_running': None}):
            with self.subTest(result=result), patch.object(m, 'http', return_value=result):
                with self.assertRaises(RuntimeError):
                    m.idle(False, True, '', '')

    def test_unknown_llm_schema_refused(self):
        for result in ([], {}, {'running': None}):
            with patch.object(m, 'http', return_value=result), self.assertRaises(RuntimeError):
                m.idle(True, False, '', '')

    def test_probe_failure_propagates(self):
        with patch.object(m, 'http', side_effect=OSError('offline')), self.assertRaises(OSError):
            m.idle(True, False, '', '')

    def test_transitional_service_refused(self):
        with patch.object(m, 'run', return_value=types.SimpleNamespace(stdout='activating')):
            with self.assertRaises(RuntimeError):
                m.active(m.LLM)

    def test_gpu_unknown_busy_and_probe_error_refused(self):
        with patch.object(m.glob, 'glob', return_value=['/dev/dri/renderD128']):
            for code, out, err in ((0, '12', ''), (1, '', 'permission denied'), (2, '', '')):
                with patch.object(m, 'run', return_value=types.SimpleNamespace(returncode=code, stdout=out, stderr=err)):
                    with self.assertRaises(RuntimeError):
                        m.gpu_free()

    def test_gpu_unused(self):
        with patch.object(m.glob, 'glob', return_value=['/dev/dri/renderD128']), patch.object(m, 'run', return_value=types.SimpleNamespace(returncode=1, stdout='', stderr='')):
            m.gpu_free()

    def test_no_gpu_is_not_safe(self):
        with patch.object(m.glob, 'glob', return_value=[]), self.assertRaises(RuntimeError):
            m.gpu_free()

    def test_reject_repo_and_relative_storage(self):
        for path in (str(m.REPO / 'private'), str(m.REPO.parent), 'relative-storage'):
            with self.subTest(path=path), self.assertRaises(RuntimeError):
                m.private_path(path)

    def test_unit_specifier_injection_refused(self):
        for value in ('%h/path', 'line\nExecStart=bad', '"quoted'):
            with self.assertRaises(RuntimeError):
                m.unit_quote(value)

    def test_all_dry_runs_have_no_effects(self):
        for action in ('general', 'video', 'off', 'provision-media', 'provision-catalog', 'validate-catalog'):
            with patch.object(sys, 'argv', ['modes.py', action, '--node', fixtures.CUDA, '--dry-run']), patch.object(m, 'run', side_effect=AssertionError('service call')), patch.object(m, 'http', side_effect=AssertionError('network')), contextlib.redirect_stdout(io.StringIO()) as out:
                m.main()
                self.assertEqual(json.loads(out.getvalue())['effects'], 'none')

    def test_remote_dry_run_does_not_ssh(self):
        args = argparse.Namespace(node=fixtures.CUDA, action='video', catalog='standard', quiesced=False, dry_run=True, storage=None, bundle='starter')
        with patch.object(m, 'read', return_value={'repo': "~/a b/it's", 'targets': {fixtures.CUDA: 'my-alias'}}), patch.object(subprocess, 'run', side_effect=AssertionError('SSH')), contextlib.redirect_stdout(io.StringIO()) as out:
            m.remote(args)
            self.assertIn('SSH (not executed)', out.getvalue())

    def test_invalid_ssh_target_refused(self):
        args = argparse.Namespace(node=fixtures.CUDA)
        with patch.object(m, 'read', return_value={'targets': {fixtures.CUDA: '-oProxyCommand=bad'}}), self.assertRaises(RuntimeError):
            m.remote(args)

    def test_switch_checks_busy_before_stopping(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / 'config').mkdir()
            (root / 'config/modes-enabled.json').write_text('{}')
            c = types.SimpleNamespace(role='worker', endpoint='http://worker', key=lambda _: 'key', stop=Mock())
            fake = types.SimpleNamespace(Cluster=lambda _: c, atomic=Mock())
            with patch.dict(sys.modules, {'manage': fake}), patch.object(m, 'ROOT', root), patch.object(m, 'active', side_effect=[True, False]), patch.object(m, 'idle', side_effect=RuntimeError('busy')), patch.object(m, 'run') as run:
                with self.assertRaisesRegex(RuntimeError, 'busy'):
                    m.switch(fixtures.CUDA, 'off', 'standard', True)
                c.stop.assert_not_called()
                run.assert_not_called()

    def test_switch_requires_quiesced_clients(self):
        with patch.dict(sys.modules, {'manage': types.SimpleNamespace(Cluster=Mock(), atomic=Mock())}):
            with self.assertRaisesRegex(RuntimeError, 'clients'):
                m.switch(fixtures.CUDA, 'video', 'standard', False)

    def test_four_profiles_resolve_every_selected_model(self):
        import yaml
        shared = yaml.safe_load((m.REPO / 'config/profiles/abliterated.yaml').read_text())['models']
        media = m.manifest()['models']
        for node in (n for n, spec in fixtures.NODES.items() if spec['role'] == 'worker'):
            profile = yaml.safe_load((m.REPO / 'config' / (node + '.yaml')).read_text())
            self.assertEqual(set(profile['modes']), {'general', 'general-abliterated', 'video', 'video-abliterated'})
            for mode in profile['modes'].values():
                if mode['runtime'] == 'comfyui':
                    self.assertEqual(set(mode['models']), {i['destination'] for i in media
                                     if set(mode['bundles']).intersection(i['bundles'])})
                else:
                    available = set(profile['models'])
                    if mode['catalog'] == 'abliterated':
                        available.update(node + '/' + name for name in shared)
                    self.assertTrue(mode['models'] and set(mode['models']) <= available)

    def test_manifest_pins_and_paths(self):
        spec = m.manifest()
        self.assertRegex(spec['comfyui']['revision'], r'^[a-f0-9]{40}$')
        for item in spec['models']:
            self.assertRegex(item['sha256'], r'^[a-f0-9]{64}$')
            self.assertRegex(item['revision'], r'^[a-f0-9]{40}$')
            self.assertNotIn('..', Path(item['destination']).parts)
            self.assertFalse(Path(item['destination']).is_absolute())
            self.assertGreater(item['size'], 0)


if __name__ == '__main__':
    unittest.main()
