#!/usr/bin/env python3
"""Offline edge and gateway regression tests; no network or service changes."""
import tempfile
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
        c=m.Cluster(SimpleNamespace(node=node,backend=None,yes=True,model=None,action='install',prune=False,apply=False,cache_type=None))
        c.host_check=lambda:None
        c.prepare();return c
    def test_nano_requires_generation_and_gpu(self):
        e=edge.Edge(self.cluster('nano')); spec=e.models['nano/qwen3.5-4b']
        response={'response':'READY','done':True,'eval_count':2,'eval_duration':100000000,'prompt_eval_count':20,'prompt_eval_duration':100000000}
        with patch.object(edge,'api',side_effect=[response,{'models':[{'name':spec['id'],'size_vram':123,'context_length':4096}]}]) as api:
            self.assertEqual(e.request(spec,'test')['generation_tps'],20)
            self.assertEqual(api.call_args_list[0].kwargs['payload']['options']['num_ctx'],4096)
        with patch.object(edge,'api',side_effect=[response,{'models':[{'name':spec['id'],'size_vram':0}]}]):
            with self.assertRaisesRegex(RuntimeError,'no GPU'): e.request(spec,'test')
        with patch.object(edge,'api',return_value={'done':True,'response':''}):
            with self.assertRaisesRegex(RuntimeError,'empty'): e.request(spec,'test')
    def test_geniex_completion_not_listing(self):
        e=edge.Edge(self.cluster('ventunoq')); spec=next(iter(e.models.values()))
        with patch.object(edge,'api',return_value={'choices':[{'message':{'content':'READY'}}],'usage':{'completion_tokens':2}}) as api:
            self.assertEqual(e.request(spec,'test')['text'],'READY')
            self.assertTrue(api.call_args.args[0].endswith('/v1/chat/completions'))
        with patch.object(edge,'api',return_value={'data':[{'id':spec['id']}]}):
            with self.assertRaisesRegex(RuntimeError,'no completion'):e.request(spec,'test')
    def test_edge_setup_validates_before_prune(self):
        e=edge.Edge(self.cluster('nano'))
        with patch.object(e,'install_missing'),patch.object(e,'configure_ollama'),patch.object(e,'wait'),patch.object(e,'ids',return_value=e.wanted()),patch.object(e,'validate',side_effect=RuntimeError('failed inference')),patch.object(e,'cleanup') as cleanup:
            with self.assertRaisesRegex(RuntimeError,'failed inference'):e.sync(prune=True)
            cleanup.assert_not_called()
    def test_cleanup_only_adopted_or_explicit_tags(self):
        e=edge.Edge(self.cluster('nano'));e.record(e.wanted()|{'old:1'})
        with patch.object(e,'ids',side_effect=[e.wanted()|{'old:1','other:1'},e.wanted()|{'other:1'}]),patch.object(e,'validate'),patch.object(e,'cli',return_value='ollama'),patch.object(edge,'run') as run:
            e.cleanup();self.assertEqual(run.call_args.args,('ollama','rm','old:1'))
            self.assertEqual(e.previous(),e.wanted())
    def test_gateway_skip_offline_but_test_local_embedding(self):
        c=self.cluster('macmini');c.key=lambda _: 'fake'
        local='qwen3-embedding:0.6b';alias='macmini/qwen3-embedding-0.6b-q8_0'
        def api(url,*a,**kw):
            if url==c.endpoint+'/v1/models':return {'data':[{'id':x} for x in c.c['routes']]}
            if url.endswith('/embeddings'):return {'data':[{'embedding':[1.,2.]}]}
            if url==c.c['nodes']['macmini']['url']+'/models':return {'data':[{'id':local}]}
            raise urllib.error.URLError('offline')
        with patch.object(m,'api',side_effect=api) as call:
            c.validate();self.assertTrue(any(x.args[0].endswith('/embeddings') for x in call.call_args_list))
        c.a.model=next(x for x in c.c['routes'] if x.startswith('gmini/'))
        with patch.object(m,'api',side_effect=api):
            with self.assertRaisesRegex(RuntimeError,'Required route unavailable'):c.validate()
    def test_gateway_http_error_not_offline(self):
        c=self.cluster('macmini');c.key=lambda _: 'fake'
        with patch.object(c,'discover'),patch.object(m,'api',side_effect=urllib.error.HTTPError('http://worker',401,'unauthorized',{},None)):
            with self.assertRaisesRegex(RuntimeError,'HTTP failure'):c.validate()
    def test_gateway_model_sync_is_idempotent(self):
        c=self.cluster('macmini')
        with patch.object(m,'api',return_value={'models':[{'name':'qwen3-embedding:0.6b'}]}),patch.object(m,'run') as run:
            c.pull_ollama();run.assert_not_called()
    def test_gateway_rejects_zero_vector(self):
        c=self.cluster('macmini');c.key=lambda _: 'fake'
        with patch.object(m,'api',return_value={'data':[{'embedding':[0,0]}]}):
            with self.assertRaisesRegex(RuntimeError,'invalid embedding'):c.validate_one('macmini/qwen3-embedding-0.6b-q8_0')
if __name__=='__main__':unittest.main()
