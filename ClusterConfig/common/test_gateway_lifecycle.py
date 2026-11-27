#!/usr/bin/env python3
"""Offline tests for launchd transitions and failure preservation; no real services."""
import json
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch, MagicMock
import manage as m


def result(code=0, stderr=''):
    return SimpleNamespace(returncode=code, stdout='', stderr=stderr)


class GatewayLifecycleTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        home = Path(self.tmp.name)
        h = patch.object(m.Path, 'home', return_value=home)
        h.start(); self.addCleanup(h.stop)
        self.c = c = m.Cluster(SimpleNamespace(node='macmini', backend=None, yes=True, model=None, prune=False))
        c.host_check = lambda: None
        c.prepare()
        c.service_path = lambda: home / 'gateway.plist'
        c.service_path().write_text('local-ai-cluster')
        c.key = lambda name: 'sk-' + name + 'x'*40

    def test_bootstrap_loaded_is_noop(self):
        with patch.object(self.c, 'gateway_loaded', return_value=True), patch.object(m, 'run') as run:
            self.c.gateway_bootstrap()
            run.assert_not_called()

    def test_bootout_waits_for_unload(self):
        with patch.object(self.c, 'gateway_loaded', side_effect=[True, True, False]), patch.object(m, 'run', return_value=result()) as run, patch.object(m.time, 'sleep'):
            self.c.gateway_bootout()
            run.assert_called_once_with('sudo','launchctl','bootout','system/'+m.LABEL,check=False,capture=True)

    def test_bootstrap_eio_when_job_loaded_is_idempotent(self):
        with patch.object(self.c, 'gateway_loaded', side_effect=[False, True]), patch.object(m, 'run', side_effect=[result(),result(),result(5,'Input/output error')]) as run:
            self.c.gateway_bootstrap()
            self.assertEqual(run.call_count,3)

    def test_bootstrap_reports_real_failure(self):
        with patch.object(self.c, 'gateway_loaded', return_value=False), patch.object(m, 'run', side_effect=[result(),result(),result(5,'Input/output error'),result(5,'Input/output error')]), patch.object(m.time,'sleep'):
            with self.assertRaisesRegex(RuntimeError,'bootstrap failed.*Input/output error'):
                self.c.gateway_bootstrap()

    def test_original_validation_failure_survives_failed_rollback(self):
        c=self.c
        c.pull_ollama=MagicMock(); c.install_gateway=MagicMock(); c.render_gateway=MagicMock(return_value={})
        c.gateway_loaded=MagicMock(side_effect=[True,True]); c.stop=MagicMock(); c.install_service=MagicMock()
        c.wait_ready=MagicMock(); c.validate_gateway=MagicMock(side_effect=RuntimeError('embedding test failed'))
        c.gateway_failure_log=MagicMock(); c.restore_configuration=MagicMock(side_effect=RuntimeError('bootstrap error 5'))
        with self.assertRaisesRegex(RuntimeError,'Original apply failure: embedding test failed; rollback also failed: bootstrap error 5'):
            c.sync()
        c.gateway_failure_log.assert_called_once()
        c.restore_configuration.assert_called_once()

    def test_rollback_uses_old_endpoint(self):
        c=self.c
        c.stop=MagicMock();c.gateway_bootstrap=MagicMock();c.wait_ready=MagicMock()
        old={c.native:b'old config',c.active:json.dumps({'endpoint':'http://old-host:4000'}).encode(),c.service_path():b'local-ai-cluster'}
        with patch.object(m,'run'):
            c.restore_configuration(old,True)
        c.gateway_bootstrap.assert_called_once()
        c.wait_ready.assert_called_once_with(endpoint='http://old-host:4000')

    def test_diagnostics_redacts_keys(self):
        c=self.c
        logs=c.root/'logs';logs.mkdir();(logs/'gateway.log').write_text(c.key('gateway'))
        with patch.object(m,'run',return_value=result()):
            c.gateway_failure_log(RuntimeError(c.key('worker')))
        report=next((c.root/'benchmarks').glob('gateway-failure-*.log'))
        self.assertNotIn(c.key('gateway'),report.read_text())
        self.assertNotIn(c.key('worker'),report.read_text())
        self.assertIn('[REDACTED]',report.read_text())

    def test_unchanged_gateway_validates_without_restart(self):
        c=self.c
        c.native.write_text(m.yaml.safe_dump({},sort_keys=False))
        c.active.write_text(json.dumps({'service_schema':7,'models':[],'backend':c.backend,'endpoint':c.endpoint,'runtimes':[c.venv.name]},indent=2))
        c.pull_ollama=MagicMock();c.install_gateway=MagicMock();c.render_gateway=MagicMock(return_value={})
        c.gateway_loaded=MagicMock(return_value=True);c.stop=MagicMock();c.start=MagicMock();c.install_service=MagicMock()
        c.wait_ready=MagicMock();c.validate_gateway=MagicMock()
        c.sync()
        c.stop.assert_not_called();c.start.assert_not_called();c.install_service.assert_not_called()
        c.validate_gateway.assert_called_once()

    def test_declined_restart_leaves_running_gateway_untouched(self):
        c=self.c
        c.pull_ollama=MagicMock();c.install_gateway=MagicMock();c.render_gateway=MagicMock(return_value={})
        c.gateway_loaded=MagicMock(return_value=True);c.stop=MagicMock();c.restore_configuration=MagicMock()
        with patch.object(m,'confirm',side_effect=RuntimeError('Not approved')):
            with self.assertRaisesRegex(RuntimeError,'Not approved'):
                c.sync()
        c.stop.assert_not_called();c.restore_configuration.assert_not_called()
