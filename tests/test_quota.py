import json
import os
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch
import urllib.request
import urllib.error
from http.server import ThreadingHTTPServer
from quota import model, providers
from quota.monitor import Monitor
from quota.server import handler
from quota.vault import Vault


class MemoryVault:
    def load(self): return {}
    def save(self, value): self.data=value


class QuotaTests(unittest.TestCase):
    def test_codex_weekly_in_primary_is_not_five_hour(self):
        result=model.codex({'rate_limit':{'primary_window':{'used_percent':7,'limit_window_seconds':604800,'reset_at':1790411113},'secondary_window':None}})
        self.assertEqual(result[0]['buckets'][0]['windowSeconds'],604800)
        self.assertEqual(result[0]['buckets'][0]['remaining'],93)
        self.assertEqual(len(result[0]['buckets']),1)

    def test_missing_invalid_percent_not_full_or_unlimited(self):
        for value in (None,True,'42',float('nan'),-1,101):
            self.assertIsNone(model.percent(value))
        self.assertEqual(model.percent(0),0)
        self.assertEqual(model.percent(1,100),100)

    def test_antigravity_keeps_groups_windows_and_unknowns(self):
        raw={'response':{'groups':[{'displayName':'Gemini Models','buckets':[{'bucketId':'g','window':'weekly','remainingFraction':.71}]},{'displayName':'Claude and GPT models','buckets':[{'bucketId':'c','window':'5h','remainingFraction':1},{'bucketId':'new','window':'new'}]}]}}
        groups=model.antigravity(raw)
        self.assertEqual(len(groups),2)
        self.assertEqual(groups[0]['buckets'][0]['remaining'],71)
        self.assertEqual(groups[1]['buckets'][0]['windowSeconds'],18000)
        self.assertIsNone(groups[1]['buckets'][1]['remaining'])
        self.assertIsNone(groups[1]['buckets'][1]['windowSeconds'])

    def test_direct_claude_windows(self):
        result=model.claude({'five_hour':{'utilization':100,'resets_at':'2026-09-20T01:00:00Z'},'seven_day':None,'seven_day_breakdown':{'claude_code':100},'extra_usage':{'utilization':10}})
        self.assertEqual(len(result[0]['buckets']),1)
        self.assertEqual(result[0]['buckets'][0]['remaining'],0)

    def test_claude_rejects_credential_metadata_identity_mismatch(self):
        def fixture(path):
            return {'claudeAiOauth':{'accessToken':'test-only'}} if str(path).endswith('.credentials.json') else {'oauthAccount':{'accountUuid':'expected','emailAddress':'same@example.com'}}
        with patch.object(providers,'load',side_effect=fixture), patch.object(Path,'stat') as stat, patch.object(providers,'request',return_value={'account':{'uuid':'different','email':'same@example.com'}}) as request:
            stat.return_value.st_mtime_ns=1
            account=providers.claude_account()
            with self.assertRaises(providers.ReadError) as raised:
                account['read']()
            self.assertEqual(raised.exception.code,'identity_mismatch')
            self.assertEqual(request.call_count,1)

    def test_distinct_subjects_with_same_labels_and_values_stay_separate(self):
        monitor=Monitor(MemoryVault(),lambda:1000)
        groups=model.claude({'five_hour':{'utilization':20}})
        for subject in ('id-1','id-2'):
            monitor.refresh_one(providers.account('claude',subject,'same@example.com','fixture',lambda:(groups,'same@example.com')))
        self.assertEqual(len(monitor.snapshot()['accounts']),2)

    def test_failure_preserves_last_success_and_honors_retry_after(self):
        now=[1000]
        monitor=Monitor(MemoryVault(),lambda:now[0])
        a=providers.account('claude','one','label','fixture',lambda:(model.claude({'five_hour':{'utilization':20}}),'label'))
        monitor.refresh_one(a)
        now[0]=1301
        def fail(): raise providers.ReadError('rate_limited',7200)
        a['read']=fail
        monitor.refresh_one(a)
        row=monitor.snapshot()['accounts'][0]
        self.assertEqual(row['lastSuccess'],1000)
        self.assertEqual(row['status'],'stale')
        self.assertEqual(row['nextAttempt'],8501)
        self.assertEqual(row['groups'][0]['buckets'][0]['remaining'],80)
        now[0]=1400
        monitor.refresh_one(a)
        self.assertEqual(monitor.snapshot()['accounts'][0]['lastAttempt'],1301)

    def test_unexpected_errors_cannot_leak_secret(self):
        monitor=Monitor(MemoryVault(),lambda:1000)
        def fail(): raise ValueError('secret-token')
        monitor.refresh_one(providers.account('codex','id','label','fixture',fail))
        self.assertNotIn('secret-token',json.dumps(monitor.snapshot()))

    def test_official_session_renewal_retries_auth_failure_only(self):
        now=[1000]
        monitor=Monitor(MemoryVault(),lambda:now[0])
        def fail(): raise providers.ReadError('sign_in_required')
        a=providers.account('claude','id','label','fixture',fail)
        a['sessionRevision']=1
        monitor.refresh_one(a)
        now[0]=1001
        a['sessionRevision']=2
        a['read']=lambda:(model.claude({'five_hour':{'utilization':0}}),'label')
        monitor.refresh_one(a)
        self.assertEqual(monitor.snapshot()['accounts'][0]['status'],'live')

    def test_requested_account_is_pending_not_verified(self):
        monitor=Monitor(MemoryVault(),desired_google=['second@example.com'])
        row=monitor.snapshot()['accounts'][0]
        self.assertEqual(row['status'],'pending')
        self.assertEqual(row['groups'],[])
        self.assertEqual(monitor.snapshot()['googleVerifiedCount'],0)

    def test_expired_cache_is_stale_without_a_refresh(self):
        now=[1000]
        monitor=Monitor(MemoryVault(),lambda:now[0])
        monitor.refresh_one(providers.account('claude','id','label','fixture',lambda:(model.claude({'five_hour':{'utilization':0}}),'label')))
        now[0]=1700
        self.assertEqual(monitor.snapshot()['accounts'][0]['status'],'stale')

    @unittest.skipUnless(os.name=='nt','Windows DPAPI')
    def test_dpapi_roundtrip_no_plaintext(self):
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/'test.dpapi'
            vault=Vault(path)
            value={'private':'sentinel-secret-123'}
            vault.save(value)
            self.assertNotIn(b'sentinel-secret-123',path.read_bytes())
            self.assertEqual(vault.load(),value)


class HTTPTests(unittest.TestCase):
    def setUp(self):
        self.monitor=Monitor(MemoryVault())
        self.server=ThreadingHTTPServer(('127.0.0.1',0),handler(self.monitor,0))
        self.port=self.server.server_port
        self.server.RequestHandlerClass=handler(self.monitor,self.port)
        self.thread=threading.Thread(target=self.server.serve_forever,daemon=True)
        self.thread.start()
    def tearDown(self):
        self.server.shutdown();self.server.server_close();self.thread.join()
    def request(self,path,headers={},method='GET'):
        req=urllib.request.Request(f'http://127.0.0.1:{self.port}'+path,headers=headers,method=method)
        try:
            with urllib.request.urlopen(req) as res:return res.status,res.headers,res.read()
        except urllib.error.HTTPError as err:return err.code,err.headers,err.read()
    def test_host_origin_and_csrf_boundaries(self):
        self.assertEqual(self.request('/api/status',{'Host':'evil.test'})[0],403)
        self.assertEqual(self.request('/api/status',{'Origin':'https://evil.test'})[0],403)
        self.assertEqual(self.request('/api/status',{'Sec-Fetch-Site':'cross-site'})[0],403)
        self.assertEqual(self.request('/api/refresh',method='POST')[0],403)
        self.assertEqual(self.request('/../quota/providers.py')[0],404)
    def test_api_and_security_headers(self):
        status,headers,data=self.request('/api/status')
        self.assertEqual(status,200)
        self.assertEqual(headers['Cache-Control'],'no-store')
        self.assertIn("frame-ancestors 'none'",headers['Content-Security-Policy'])
        self.assertEqual(json.loads(data)['accounts'],[])


if __name__=='__main__':unittest.main()
