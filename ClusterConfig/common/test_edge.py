#!/usr/bin/env python3
"""Offline edge and gateway regression tests; no network or service changes."""
import tempfile
import profile_fixtures as fixtures
import unittest
import urllib.error
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch, MagicMock
import edge
import manage as m

class EdgeGatewayTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory()
        self.home=Path(self.tmp.name)
        self.p=patch.object(m.Path,'home',return_value=self.home);self.p.start()
    def tearDown(self):
        self.p.stop();self.tmp.cleanup()
    def cluster(self,node):
        c=m.Cluster(SimpleNamespace(dry_run=True,node=node,backend=None,yes=True,model=None,action='install',prune=False,apply=False,cache_type=None))
        c.host_check=lambda:None
        c.prepare();return c
    def test_ollama_requires_generation_and_gpu(self):
        e=edge.Edge(self.cluster(fixtures.OLLAMA)); spec=e.models[f'{fixtures.OLLAMA}/qwen3.5-4b']
        response={'response':'READY','done':True,'eval_count':2,'eval_duration':100000000,'prompt_eval_count':20,'prompt_eval_duration':100000000}
        with patch.object(edge,'api',side_effect=[response,{'models':[{'name':spec['id'],'size_vram':123,'context_length':4096}]}]) as api:
            self.assertEqual(e.request(spec,'test')['generation_tps'],20)
            self.assertEqual(api.call_args_list[0].kwargs['payload']['options']['num_ctx'],4096)
        with patch.object(edge,'api',side_effect=[response,{'models':[{'name':spec['id'],'size_vram':0}]}]):
            with self.assertRaisesRegex(RuntimeError,'no GPU'): e.request(spec,'test')
        with patch.object(edge,'api',return_value={'done':True,'response':''}):
            with self.assertRaisesRegex(RuntimeError,'empty'): e.request(spec,'test')
    def test_unload_waits_until_runner_disappears(self):
        e=edge.Edge(self.cluster(fixtures.OLLAMA)); spec=next(iter(e.models.values())); model=spec['id']
        with patch.object(e,'loaded_ids',side_effect=[{model},{model},set()]) as loaded, patch.object(edge,'api') as api, patch.object(edge.time,'sleep') as sleep:
            e.unload(spec)
            self.assertEqual(loaded.call_count,3)
            self.assertEqual(api.call_args.kwargs['payload'],{'model':model,'keep_alive':0})
            sleep.assert_called_once_with(1)

    def test_unload_timeout_blocks_next_model(self):
        e=edge.Edge(self.cluster(fixtures.OLLAMA));spec=next(iter(e.models.values()));e.spec['unload_timeout_seconds']=0
        with patch.object(e,'loaded_ids',return_value={spec['id']}),patch.object(edge,'api'):
            with self.assertRaisesRegex(RuntimeError,'still resident'): e.unload(spec)

    def test_session_releases_previous_model_before_request(self):
        e=edge.Edge(self.cluster(fixtures.OLLAMA));spec=next(iter(e.models.values()))
        old='gemma4:e2b-it-q4_K_M';e.record({old})
        order=[]
        def unload(m): order.append(('unload',m['id']))
        with patch.object(e,'loaded_ids',side_effect=[{old},set()]),patch.object(e,'unload',side_effect=unload):
            with e.model_session(spec): order.append(('request',spec['id']))
        self.assertEqual(order,[('unload',old),('request',spec['id']),('unload',spec['id'])])

    def test_session_preserves_original_error_if_unload_fails(self):
        e=edge.Edge(self.cluster(fixtures.OLLAMA));spec=next(iter(e.models.values()))
        with patch.object(e,'loaded_ids',return_value=set()),patch.object(e,'unload',side_effect=RuntimeError('unload error')):
            with self.assertRaisesRegex(RuntimeError,'original failure'):
                with e.model_session(spec): raise RuntimeError('original failure')

    def test_unrelated_loaded_model_is_not_stopped(self):
        e=edge.Edge(self.cluster(fixtures.OLLAMA));spec=next(iter(e.models.values()))
        with patch.object(e,'loaded_ids',return_value={'unrelated:1'}),patch.object(e,'unload') as unload:
            with self.assertRaisesRegex(RuntimeError,'Unmanaged Ollama'):
                with e.model_session(spec): self.fail('should not enter session')
            unload.assert_not_called()

    def test_seed_removes_unused_ollama_weights(self):
        e=edge.Edge(self.cluster(fixtures.OLLAMA));gemma='gemma4:e2b-it-q4_K_M'
        with patch.object(e,'ids',side_effect=[e.wanted()|{gemma},e.wanted()]),patch.object(e,'unload') as unload,patch.object(e,'cli',return_value='ollama'),patch.object(edge,'run') as run:
            e.cleanup(validated=True);unload.assert_called_once_with({'id':gemma});self.assertEqual(run.call_args.args,('ollama','rm',gemma))
        self.assertNotIn(gemma,e.previous())

    def test_geniex_completion_not_listing(self):
        e=edge.Edge(self.cluster(fixtures.GENIEX)); spec=next(iter(e.models.values()))
        with patch.object(edge,'api',return_value={'choices':[{'message':{'content':'READY'}}],'usage':{'completion_tokens':2}}) as api:
            self.assertEqual(e.request(spec,'test')['text'],'READY')
            self.assertTrue(api.call_args.args[0].endswith('/v1/chat/completions'))
        with patch.object(edge,'api',return_value={'data':[{'id':spec['id']}]}):
            with self.assertRaisesRegex(RuntimeError,'no completion'):e.request(spec,'test')
    def test_edge_seed_prunes_before_validation(self):
        e=edge.Edge(self.cluster(fixtures.OLLAMA))
        with patch.object(e,'install_missing'),patch.object(e,'configure_ollama'),patch.object(e,'wait'),patch.object(e,'ids',return_value=e.wanted()),patch.object(e,'validate',side_effect=RuntimeError('failed inference')),patch.object(e,'cleanup') as cleanup:
            with self.assertRaisesRegex(RuntimeError,'failed inference'):e.sync(prune=True)
            cleanup.assert_called_once_with(validated=True)

    def test_cleanup_removes_all_unconfigured_native_tags(self):
        e=edge.Edge(self.cluster(fixtures.OLLAMA));e.record(e.wanted()|{'old:1'})
        with patch.object(e,'ids',side_effect=[e.wanted()|{'old:1','other:1'},e.wanted()]),patch.object(e,'unload'),patch.object(e,'cli',return_value='ollama'),patch.object(edge,'run') as run:
            e.cleanup();self.assertEqual({x.args[-1] for x in run.call_args_list},{'old:1','other:1'});self.assertEqual(e.previous(),e.wanted())

    def test_context_reports_individual_failure_reasons(self):
        cfg={'min_generation_tps':3,'min_host_free_mib':512,'max_request_seconds':300}
        before={'swapout_pages':10}; after={'host_free_mib':1024,'swapout_pages':10}
        good={'prompt_tokens':1570,'text':'CODE','context_allocated':2048,'generation_tps':10,'seconds':50}
        self.assertEqual(edge.context_checks(good,'CODE',2048,cfg,before,after),[])
        bad=dict(good,prompt_tokens=200,text='wrong',context_allocated=1024,cached_prompt_tokens=199)
        self.assertEqual(set(edge.context_checks(bad,'CODE',2048,cfg,before,after)),{'prompt_fill','retrieval','allocated_context','uncached_prompt'})

    def test_seed_removes_unused_geniex_model(self):
        e=edge.Edge(self.cluster(fixtures.GENIEX));old='google/gemma-4-E2B-it-qat-q4_0-gguf:Q4_0';e.record(e.wanted()|{old})
        with patch.object(e,'ids',side_effect=[e.wanted()|{old},e.wanted()]),patch.object(e,'cli',return_value='geniex'),patch.object(edge,'run') as run:
            e.cleanup(validated=True);self.assertEqual(run.call_args.args,('geniex','remove',old))
        self.assertNotIn(old,e.previous())

    def test_geniex_calibrate_benchmarks_fixed_bundle(self):
        e=edge.Edge(self.cluster(fixtures.GENIEX))
        with patch.object(e,'benchmark') as bench:
            e.calibrate(); bench.assert_called_once()
        e.a.apply=True
        with self.assertRaisesRegex(RuntimeError,'fixed QAIRT'):
            e.calibrate()

    def test_gateway_skip_offline_but_test_local_embedding(self):
        c=self.cluster(fixtures.GATEWAY);c.key=lambda _: 'fake'
        local='qwen3-embedding:0.6b';alias=f'{fixtures.GATEWAY}/qwen3-embedding-0.6b-q8_0'
        def api(url,*a,**kw):
            if url==c.endpoint+'/v1/models':return {'data':[{'id':x} for x in c.c['routes']]}
            if url.endswith('/embeddings'):return {'data':[{'embedding':[1.,2.]}]}
            if url==c.c['nodes'][fixtures.GATEWAY]['url']+'/models':return {'data':[{'id':local}]}
            raise urllib.error.URLError('offline')
        with patch.object(m,'api',side_effect=api) as call:
            c.validate();self.assertTrue(any(x.args[0].endswith('/embeddings') for x in call.call_args_list))
        c.a.model=next(x for x in c.c['routes'] if x.startswith(f'{fixtures.AMD}/'))
        with patch.object(m,'api',side_effect=api):
            with self.assertRaisesRegex(RuntimeError,'Required route unavailable'):c.validate()
    def test_gateway_http_error_not_offline(self):
        c=self.cluster(fixtures.GATEWAY);c.key=lambda _: 'fake'
        with patch.object(c,'discover'),patch.object(m,'api',side_effect=urllib.error.HTTPError('http://worker',401,'unauthorized',{},None)):
            with self.assertRaisesRegex(RuntimeError,'HTTP failure'):c.validate()
    def test_gateway_model_sync_is_idempotent(self):
        c=self.cluster(fixtures.GATEWAY)
        with patch.object(m,'api',return_value={'models':[{'name':'qwen3-embedding:0.6b'}]}),patch.object(m,'run') as run:
            c.pull_ollama();run.assert_not_called()
    def test_gateway_rejects_zero_vector(self):
        c=self.cluster(fixtures.GATEWAY);c.key=lambda _: 'fake'
        with patch.object(m,'api',return_value={'data':[{'embedding':[0,0]}]}):
            with self.assertRaisesRegex(RuntimeError,'invalid embedding'):c.validate_one(f'{fixtures.GATEWAY}/qwen3-embedding-0.6b-q8_0')
if __name__=='__main__':unittest.main()
