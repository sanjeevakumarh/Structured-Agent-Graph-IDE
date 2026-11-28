#!/usr/bin/env python3
"""Offline calibration and routing tests. Run python3 -m unittest discover -s common.
Uses synthetic results and mocked process/network calls; no GPU required.
"""
import contextlib
import io
import json
import tempfile
import profile_fixtures as fixtures
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch, MagicMock
import yaml
import manage as m
import calibrate as cal


class CalibrationTests(unittest.TestCase):
    def setUp(self):
        self.cfg = {'min_generation_tps': 5, 'max_ttft_seconds': 300,
                    'min_host_free_gib': 6, 'min_gpu_free_mib': 1024}

    def test_route_identity_and_disabled_edges(self):
        cfg = yaml.safe_load((m.REPO / f'config/{fixtures.GATEWAY}.yaml').read_text())
        cfg['nodes'] = [n for n in cfg['nodes'] if fixtures.NODES[n]['role'] != 'edge']
        routes = m.gateway_routes(cfg, m.REPO / 'config')
        self.assertEqual({v['node'] for v in routes.values()}, {fixtures.AMD,fixtures.XPU,fixtures.CUDA,fixtures.GATEWAY})
        self.assertTrue(all(k.startswith(v['node'] + '/') for k,v in routes.items()))
        self.assertNotIn(f'{fixtures.AMD}/qwen3.5-122b-a10b-q4_k_m', routes)
        cfg['nodes'].append(fixtures.OLLAMA)
        cfg['aliases'] = {'coder-deep': f'{fixtures.AMD}/qwen3-coder-next-80b-q4_k_m'}
        routes = m.gateway_routes(cfg, m.REPO / 'config')
        self.assertEqual(routes[f'{fixtures.OLLAMA}/qwen3.5-4b']['model'], 'qwen3.5:4b')
        self.assertEqual(routes['coder-deep']['node'], fixtures.AMD)
        cfg['nodes'].append(fixtures.GENIEX)
        routes = m.gateway_routes(cfg, m.REPO / 'config')
        self.assertEqual(routes[f'{fixtures.GENIEX}/qwen3-4b-instruct-2507-w4a16']['model'], 'qualcomm/Qwen3-4B-Instruct-2507:W4A16')
        self.assertNotIn(f'{fixtures.GENIEX}/qwen2.5-vl-7b-w4a16', routes)

    def test_alias_safe_and_model_args(self):
        self.assertTrue(m.valid_alias(f'{fixtures.AMD}/model-q4', fixtures.AMD))
        for name in (f'{fixtures.AMD}/../model', f'{fixtures.CUDA}/model', f'{fixtures.AMD}/..', f'{fixtures.AMD}/model/extra'):
            self.assertFalse(m.valid_alias(name, fixtures.AMD))
        cmd = m.model_command('/bin/server', Path('/models'), [{'name':'model.gguf'}], f'{fixtures.AMD}/model-q4', {'context':65536,'cache_type_k':'q8_0','cache_type_v':'q8_0','flash_attention':'on'}, 12345)
        self.assertEqual(cmd[cmd.index('--ctx-size')+1], '65536')
        self.assertEqual(cmd[cmd.index('--alias')+1], f'{fixtures.AMD}/model-q4')
        self.assertEqual(cmd[cmd.index('--cache-type-v')+1], 'q8_0')
        self.assertIn('--no-context-shift', cmd)

    def test_prompt_needle_positions(self):
        for position in (.1,.5,.9):
            messages, code = cal.prompt_messages(100, position, 17)
            lines = messages[0]['content'].splitlines()
            self.assertIn(code, lines[1+int(100*position)])
            self.assertEqual(messages[0]['content'].count(code), 1)
        self.assertNotEqual(cal.prompt_messages(100,.5,17)[1], cal.prompt_messages(100,.5,18)[1])

    def test_trial_rejects_partial_or_cached_context(self):
        trial = dict(correct=True,prompt_tokens=7372,completion_tokens=100,processed_tokens=7372,generation_tps=20,ttft_seconds=15)
        self.assertTrue(cal.trial_pass(trial,8192,self.cfg))
        for changes in ({'correct':False},{'prompt_tokens':200},{'processed_tokens':10},{'generation_tps':1},{'ttft_seconds':301},{'completion_tokens':1000}):
            self.assertFalse(cal.trial_pass(dict(trial,**changes),8192,self.cfg))

    def test_recommend_only_passing_and_prefer_precision(self):
        def r(ctx,k,ok):
            return dict(context=ctx,cache_type_k=k,cache_type_v=k,flash_attention='on',passed=ok)
        self.assertIsNone(cal.choose([r(8192,'q8_0',False)]))
        rec = cal.choose([r(131072,'q8_0',False),r(65536,'q8_0',True),r(65536,'f16',True),r(32768,'f16',True)])
        self.assertEqual(rec['context'],65536)
        self.assertEqual(rec['cache_type_k'],'f16')

    def test_precision_meets_target_before_extra_context(self):
        def r(ctx,k):
            return dict(context=ctx,cache_type_k=k,cache_type_v=k,flash_attention='auto',passed=True)
        self.assertEqual(cal.choose([r(262144,'q8_0'),r(131072,'f16')],131072)['cache_type_k'],'f16')
        self.assertEqual(cal.choose([r(65536,'f16'),r(131072,'q8_0')],262144)['context'],131072)

    def test_intel_device_memory_csv(self):
        self.assertEqual(cal.intel_memory_used('Timestamp, DeviceId, GPU Memory Used (MiB)\n2026,0,1024\n'),1024)
        self.assertIsNone(cal.intel_memory_used('Timestamp, DeviceId, GPU Utilization (%)\n2026,0,90\n'))
        self.assertIsNone(cal.intel_memory_used('Timestamp, DeviceId, TileId, GPU Memory Used (MiB)\n2026,0,0,1024\n'))

    def test_backend_memory_parser_from_hardware_output(self):
        intel = 'Available devices:\n  Vulkan0: Intel(R) Graphics (BMG G31) (32656 MiB, 29354 MiB free)'
        amd = 'Available devices:\n  Vulkan0: AMD Radeon Graphics (RADV GFX1151) (115712 MiB, 115546 MiB free)'
        self.assertEqual(cal.backend_memory_free(intel, 'Intel|BMG|Arc'), 29354)
        self.assertEqual(cal.backend_memory_free(amd, 'AMD|Radeon|gfx1151'), 115546)
        self.assertIsNone(cal.backend_memory_free(intel, 'AMD'))
        self.assertIsNone(cal.backend_memory_free(intel + '\n' + intel, 'Intel'))
        self.assertIsNone(cal.backend_memory_free(intel.replace('29354', '99999'), 'Intel'))

    def test_backend_probe_reads_stderr_and_never_caches(self):
        def result(free):
            return SimpleNamespace(stdout='', stderr=f'Vulkan0: Intel(R) Graphics (BMG G31) (32656 MiB, {free} MiB free)')
        with patch.object(cal.subprocess,'run',side_effect=[result(29354),result(1024)]) as run:
            self.assertEqual(cal.backend_probe('/bin/server',{},'Intel'),29354)
            self.assertEqual(cal.backend_probe('/bin/server',{},'Intel'),1024)
            self.assertEqual(run.call_count,2)

    def test_native_memory_is_preferred_and_missing_uses_fallback(self):
        probe=MagicMock(return_value=29354)
        with patch.object(cal,'native_gpu_free',return_value=0):
            self.assertEqual(cal.gpu_free(fixtures.XPU,{},probe),0); probe.assert_not_called()
        with patch.object(cal,'native_gpu_free',return_value=None):
            self.assertEqual(cal.gpu_free(fixtures.XPU,{},probe),29354); probe.assert_called_once()

    def test_flash_attention_yaml_boolean_and_slots(self):
        self.assertEqual(m.flash_mode(True),'on')
        self.assertEqual(m.flash_mode(False),'off')
        self.assertEqual(m.slot_count({'parallel':2}),2)
        with self.assertRaises(RuntimeError):
            m.flash_mode('True')

    def test_apply_preserves_comments_and_quotes_on(self):
        with tempfile.TemporaryDirectory() as tmp:
            p = Path(tmp)/'profile.yaml'
            orig = f'models:\n  {fixtures.AMD}/model:\n    context: 32768 # note\n    cache_type_k: f16\n    cache_type_v: f16\n    flash_attention: auto\n'
            p.write_text(orig)
            rec = {f'{fixtures.AMD}/model':{'context':65536,'cache_type_k':'q8_0','cache_type_v':'q8_0','flash_attention':'on'}}
            cal.update_yaml(p,rec,orig)
            self.assertIn('# note',p.read_text())
            self.assertEqual(yaml.safe_load(p.read_text())['models'],rec)
            with self.assertRaisesRegex(RuntimeError,'changed during'):
                cal.update_yaml(p,rec,orig)

    def test_stream_requires_usage_and_timings(self):
        chunks = [dict(choices=[{'delta':{'content':'CODE'}}]),dict(choices=[],timings={'prompt_n':100},usage={'prompt_tokens':100,'completion_tokens':3})]
        payload = b''.join(b'data: '+json.dumps(c).encode()+b'\n' for c in chunks)+b'data: [DONE]\n'
        with patch.object(cal.urllib.request,'urlopen',return_value=io.BytesIO(payload)):
            text, timings, usage, ttft = cal.stream_chat('http://localhost:1','key',f'{fixtures.AMD}/model',[],10)
            self.assertEqual(text,'CODE'); self.assertEqual(usage['prompt_tokens'],100)
        with patch.object(cal.urllib.request,'urlopen',return_value=io.BytesIO(b'data: [DONE]\n')):
            with self.assertRaisesRegex(RuntimeError,'Incomplete stream'):
                cal.stream_chat('http://localhost:1','key',f'{fixtures.AMD}/model',[],10)

    def test_missing_telemetry_prevents_load(self):
        with tempfile.TemporaryDirectory() as tmp:
            p = Path(tmp)
            c = SimpleNamespace(a=SimpleNamespace(dry_run=True,node=fixtures.XPU),runtime=p,locate=lambda *a:p/'server',runtime_env=lambda:{})
            with patch.object(cal,'meminfo',return_value={'host_free_mib':32768}),patch.object(cal,'gpu_free',return_value=None),patch.object(cal.subprocess,'Popen') as popen:
                res = cal.candidate(c,f'{fixtures.XPU}/model',{'context':8192},8192,'f16','f16','auto',self.cfg,p,[{'name':'model.gguf'}],p)
            self.assertFalse(res['passed']); popen.assert_not_called()

    def test_amd_probe_skips_incomplete_and_nonmatching_devices(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root/'card0/device').mkdir(parents=True)  # unreadable/incomplete entry
            (root/'card1-DP-1/device').mkdir(parents=True)  # connector, not a GPU
            (root/'card1/device').mkdir(parents=True)
            d = root/'card1/device'
            for k,v in {'vendor':'0x1002', 'device':'0x1586', 'mem_info_gtt_total':str(120259084288), 'mem_info_gtt_used':str(18661376)}.items():
                (d/k).write_text(v)
            hw = {'vendor':'0x1002', 'pci_device':'0x1586'}
            self.assertAlmostEqual(cal.drm_memory_free(hw, 'gtt', root), (120259084288-18661376)/cal.MIB)
            self.assertIsNone(cal.drm_memory_free(dict(hw, pci_device='0x0000'), 'gtt', root))
            (d/'mem_info_gtt_used').write_text(str(120259084289))
            self.assertIsNone(cal.drm_memory_free(hw, 'gtt', root))

    def test_baseline_error_reports_host_and_gpu_separately(self):
        with tempfile.TemporaryDirectory() as tmp:
            p = Path(tmp)
            c = SimpleNamespace(a=SimpleNamespace(dry_run=True,node=fixtures.AMD),runtime=p,locate=lambda *a:p/'server',runtime_env=lambda:{})
            for free, host, expected in ((None, 32768, 'GPU telemetry unavailable'), (500, 32768, 'Insufficient GPU/GTT headroom'), (32768, 500, 'Insufficient host memory headroom')):
                with patch.object(cal,'meminfo',return_value={'host_free_mib':host}),patch.object(cal,'gpu_free',return_value=free),patch.object(cal.subprocess,'Popen') as popen:
                    res = cal.candidate(c,f'{fixtures.AMD}/model',{'context':8192},8192,'f16','f16','auto',self.cfg,p,[{'name':'model.gguf'}],p)
                self.assertIn(expected,res['error'])
                self.assertEqual(res['baseline']['gpu_free_mib'],free)
                popen.assert_not_called()

    def test_context_calibration_never_recommends_failed_candidate(self):
        self.assertIsNone(cal.choose([{'context':32768,'cache_type_k':'f16','cache_type_v':'f16','passed':False}],32768))


if __name__ == '__main__':
    unittest.main(verbosity=2)
