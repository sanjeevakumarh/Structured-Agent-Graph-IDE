#!/usr/bin/env python3
"""Offline tests for launchd transitions and failure preservation; no real services."""
import json
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch, MagicMock
import manage as m
import seed


def result(code=0, stderr=''):
    return SimpleNamespace(returncode=code, stdout='', stderr=stderr)


class GatewayLifecycleTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        home = Path(self.tmp.name)
        h = patch.object(m.Path, 'home', return_value=home)
        h.start(); self.addCleanup(h.stop)
        self.c = c = m.Cluster(SimpleNamespace(dry_run=True,node='macmini', backend=None, yes=True, model=None, prune=False))
        c.host_check = lambda: None
        c.prepare()
        c.service_path = lambda: home / 'gateway.plist'
        c.service_path().write_text('local-ai-cluster')
        c.key = lambda name: 'sk-' + name + 'x'*40
        c.configure_gateway_serve = MagicMock()

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

    def test_original_validation_failure_reported_without_rollback(self):
        c=self.c;c.pull_ollama=MagicMock();c.install_gateway=MagicMock();c.render_gateway=MagicMock(return_value={'new':True})
        c.service_active=MagicMock(return_value=False);c.stop=MagicMock();c.install_service=MagicMock();c.start=MagicMock()
        c.validate_gateway=MagicMock(side_effect=RuntimeError('embedding test failed'));c.gateway_failure_log=MagicMock()
        with self.assertRaisesRegex(RuntimeError,'embedding test failed'):c.sync()
        c.gateway_failure_log.assert_called_once();c.stop.assert_called_once();c.start.assert_called_once()
        self.assertEqual(m.yaml.safe_load(c.native.read_text()),{'new':True})

    def test_no_previous_config_backup(self):
        c=self.c;c.native.write_text('old: yes');c.native.with_suffix('.previous.yaml').write_text('stale: yes')
        c.pull_ollama=MagicMock();c.install_gateway=MagicMock();c.render_gateway=MagicMock(return_value={'new':True});c.service_active=MagicMock(return_value=False)
        c.install_service=MagicMock();c.start=MagicMock();c.validate_gateway=MagicMock();c.sync()
        self.assertFalse(c.native.with_suffix('.previous.yaml').exists());self.assertEqual(m.yaml.safe_load(c.native.read_text()),{'new':True})

    def test_diagnostics_redacts_keys(self):
        c=self.c
        logs=c.root/'logs';logs.mkdir();(logs/'gateway.log').write_text(c.key('gateway'))
        with patch.object(m,'run',return_value=result()):
            c.gateway_failure_log(RuntimeError(c.key('worker')))
        report=next((c.root/'benchmarks').glob('gateway-failure-*.log'))
        self.assertNotIn(c.key('gateway'),report.read_text())
        self.assertNotIn(c.key('worker'),report.read_text())
        self.assertIn('[REDACTED]',report.read_text())

    def test_gateway_recreates_service_and_validates(self):
        c=self.c;c.pull_ollama=MagicMock();c.install_gateway=MagicMock();c.render_gateway=MagicMock(return_value={});c.service_active=MagicMock(return_value=True)
        c.stop=MagicMock();c.start=MagicMock();c.install_service=MagicMock();c.validate_gateway=MagicMock();c.sync()
        c.stop.assert_called_once();c.start.assert_called_once();c.install_service.assert_called_once();c.validate_gateway.assert_called_once()

    def test_declined_seed_does_not_stop_gateway(self):
        c=self.c;c.stop=MagicMock();c.sync=MagicMock()
        with patch.object(seed,'confirm',side_effect=RuntimeError('Not approved')):
            with self.assertRaisesRegex(RuntimeError,'Not approved'):seed.deploy(c)
        c.stop.assert_not_called();c.sync.assert_not_called()

    def test_gateway_tailnet_uses_loopback(self):
        self.assertEqual(self.c.endpoint, 'http://127.0.0.1:4001')
        self.assertTrue(self.c.gateway_tailnet)

    def test_private_serve_config(self):
        c=self.c
        with patch.object(c,'gateway_serve_config',return_value={'TCP':{'4001':{'TCPForward':'127.0.0.1:4001'}}}):
            self.assertTrue(c.gateway_forward(4001))
        with patch.object(c,'gateway_serve_config',return_value={'TCP':{'4001':{'HTTP':True}}}):
            with self.assertRaisesRegex(RuntimeError,'different handler'): c.gateway_forward(4001)
        with patch.object(c,'gateway_serve_config',return_value={'AllowFunnel':{'example.ts.net:4001':True}}):
            with self.assertRaisesRegex(RuntimeError,'Funnel'): c.gateway_forward(4001)

    def test_serve_install_and_retire_owned_port(self):
        c=self.c
        with patch.object(c,'gateway_forward',side_effect=[None,{'TCPForward':'127.0.0.1:4001'}, {'TCPForward':'127.0.0.1:4001'}]), patch.object(c,'tailscale') as cli:
            m.Cluster.configure_gateway_serve(c)
            cli.assert_called_once_with('serve','--bg','--tcp=4001','tcp://127.0.0.1:4001')
            self.assertTrue((c.cfg/'gateway-serve.json').is_file())
            c.remove_gateway_serve()
            cli.assert_called_with('serve','--tcp=4001','off')
            self.assertFalse((c.cfg/'gateway-serve.json').exists())

    def test_serve_idempotent_and_no_unowned_removal(self):
        c=self.c
        with patch.object(c,'gateway_forward',return_value={'TCPForward':'127.0.0.1:4001'}), patch.object(c,'tailscale') as cli:
            c.remove_gateway_serve()  # no marker
            m.Cluster.configure_gateway_serve(c)
            cli.assert_not_called()

    def test_local_readiness_uses_direct_api(self):
        with patch.object(m,'api',return_value={'data':[]}) as api:
            self.c.wait_ready()
            api.assert_called_once_with('http://127.0.0.1:4001/v1/models',self.c.key('gateway'),timeout=2)
