import copy
import json
import threading
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer
from quota.monitor import Monitor
from quota.providers import account
from quota.server import handler


class Store:
    def __init__(self, data=None):
        self.data = data or {}
    def load(self):
        return copy.deepcopy(self.data)
    def save(self, data):
        self.data = copy.deepcopy(data)


class RemovalTests(unittest.TestCase):
    def setUp(self):
        self.settings = Store({'desiredGoogleAccounts': ['same@example.com']})
        self.cache = Store()
        self.monitor = Monitor(self.cache, clock=lambda: 1000,
                               desired_google=['same@example.com'], settings_vault=self.settings)
        self.calls = 0
        def read():
            self.calls += 1
            return [{'id':'g','label':'Gemini','buckets':[{'id':'w','remaining':50}]}], 'same@example.com'
        self.a = account('antigravity','source-a','same@example.com','fixture',read)
        self.b = account('antigravity','source-b','same@example.com','fixture',read)
        self.monitor.refresh_one(self.a)
        self.monitor.refresh_one(self.b)

    def test_remove_persists_and_does_not_merge_same_email_accounts(self):
        self.assertTrue(self.monitor.remove_account(self.a['id']))
        self.assertEqual([a['id'] for a in self.monitor.snapshot()['accounts']], [self.b['id']])
        self.cache.save(self.monitor.rows)
        restarted = Monitor(self.cache, settings_vault=self.settings, desired_google=['same@example.com'])
        self.assertEqual([a['id'] for a in restarted.snapshot()['accounts']], [self.b['id']])
        self.assertEqual(self.settings.data['desiredGoogleAccounts'], ['same@example.com'])
        self.monitor.rows[self.a['id']]['nextAttempt'] = 0
        self.monitor.refresh_one(self.a)
        self.assertEqual(self.calls, 2)

    def test_restore_and_no_placeholder_resurrection(self):
        self.monitor.remove_account(self.a['id'])
        self.monitor.remove_account(self.b['id'])
        self.assertEqual(self.monitor.snapshot()['accounts'], [])
        self.monitor.restore_accounts()
        self.assertEqual(len(self.monitor.snapshot()['accounts']), 2)
        self.assertFalse(self.monitor.snapshot()['hasRemovedAccounts'])

    def test_failed_save_leaves_account_visible(self):
        def fail(data): raise OSError('disk full')
        self.settings.save = fail
        with self.assertRaises(OSError):
            self.monitor.remove_account(self.a['id'])
        self.assertEqual(len(self.monitor.snapshot()['accounts']), 2)

    def test_layout_order_and_removal_persist_atomically(self):
        self.assertTrue(self.monitor.save_layout([self.b['id'], self.a['id']], []))
        self.cache.save(self.monitor.rows)
        restarted = Monitor(self.cache, settings_vault=self.settings)
        self.assertEqual([a['id'] for a in restarted.snapshot()['accounts']], [self.b['id'], self.a['id']])
        self.assertTrue(self.monitor.save_layout([self.a['id']], [self.b['id']]))
        self.assertEqual([a['id'] for a in self.monitor.snapshot()['accounts']], [self.a['id']])
        self.assertEqual(self.settings.data['accountOrder'], [self.a['id']])

    def test_layout_rejects_duplicates_and_concurrent_account_changes(self):
        with self.assertRaises(ValueError):
            self.monitor.save_layout([self.a['id'], self.a['id']], [])
        self.assertFalse(self.monitor.save_layout([self.a['id']], []))
        self.assertFalse(self.monitor.save_layout(['unknown'], [self.a['id'], self.b['id']]))
        self.assertEqual(len(self.monitor.snapshot()['accounts']), 2)

    def test_layout_disk_failure_does_not_change_order_or_visibility(self):
        original = [a['id'] for a in self.monitor.snapshot()['accounts']]
        def fail(data): raise OSError('disk full')
        self.settings.save = fail
        with self.assertRaises(OSError):
            self.monitor.save_layout([self.b['id']], [self.a['id']])
        self.assertEqual([a['id'] for a in self.monitor.snapshot()['accounts']], original)

    def test_unknown_account_and_requested_placeholder(self):
        self.assertFalse(self.monitor.remove_account('unknown'))
        m = Monitor(Store(), desired_google=['pending@example.com'], settings_vault=Store())
        self.assertTrue(m.remove_account(m.snapshot()['accounts'][0]['id']))
        self.assertEqual(m.snapshot()['accounts'], [])

    def test_http_remove_restore_and_cross_origin_rejection(self):
        server = ThreadingHTTPServer(('127.0.0.1',0),handler(self.monitor,0))
        port = server.server_port
        server.RequestHandlerClass = handler(self.monitor,port)
        worker = threading.Thread(target=server.serve_forever,daemon=True)
        worker.start()
        def send(route, payload, origin=None):
            req = urllib.request.Request(f'http://127.0.0.1:{port}'+route,
                    data=json.dumps(payload).encode(), headers={'Origin':origin or f'http://127.0.0.1:{port}',
                    'X-Quota-Request':'refresh','Content-Type':'application/json'})
            try:
                with urllib.request.urlopen(req) as response:
                    return response.status
            except urllib.error.HTTPError as error:
                return error.code
        try:
            self.assertEqual(send('/api/accounts/layout',{'order':[self.b['id'],self.a['id']],'removed':[]}),200)
            self.assertEqual(send('/api/accounts/layout',{'order':[self.a['id']],'removed':[]}),409)
            self.assertEqual(send('/api/accounts/layout',{'order':None,'removed':[]}),400)
            self.assertEqual(send('/api/accounts/remove',{'accountId':self.a['id']},'https://evil.test'),403)
            self.assertEqual(send('/api/accounts/remove',{'accountId':self.a['id']}),200)
            self.assertEqual(len(self.monitor.snapshot()['accounts']),1)
            self.assertEqual(send('/api/accounts/remove',{'accountId':self.a['id']}),404)
            self.assertEqual(send('/api/accounts/remove',{'accountId':[]}),400)
            self.assertEqual(send('/api/accounts/restore',{}),200)
            self.assertEqual(len(self.monitor.snapshot()['accounts']),2)
        finally:
            server.shutdown()
            server.server_close()
            worker.join()
