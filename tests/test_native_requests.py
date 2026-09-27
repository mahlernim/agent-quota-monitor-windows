"""Exercise the HTTP requests emitted by the Windows HttpClient."""
import json
import threading
import unittest
from unittest.mock import patch
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer
from quota.monitor import Monitor
from quota.server import handler
from test_removal import Store


class NativeRequestTests(unittest.TestCase):
    def setUp(self):
        self.settings = Store({'enabledProviders': [], 'unrelated': 'preserved'})
        self.monitor = Monitor(Store(), settings_vault=self.settings)
        self.start_server(self.monitor)

    def start_server(self, monitor):
        self.server = ThreadingHTTPServer(('127.0.0.1', 0), handler(monitor, 0))
        self.url = f'http://127.0.0.1:{self.server.server_port}'
        self.server.RequestHandlerClass = handler(monitor, self.server.server_port)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def stop_server(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(3)

    def tearDown(self):
        self.stop_server()

    def post(self, route, payload, content_type='application/json; charset=utf-8', origin=None):
        request = urllib.request.Request(self.url + route, data=json.dumps(payload).encode(),
            headers={'Origin': origin or self.url, 'X-Quota-Request': 'refresh', 'Content-Type': content_type})
        return urllib.request.urlopen(request)

    def test_native_pins_and_preferences_survive_restart(self):
        preferences = {'desktopFloatingSelections': [{'accountId': 'a', 'groupId': 'g', 'bucketId': 'b'}],
                       'desktopFloating': True, 'desktopOpacity': 70}
        with self.post('/api/desktop', preferences) as response:
            self.assertEqual(response.status, 200)
        self.stop_server()
        self.start_server(Monitor(Store(), settings_vault=self.settings))
        with urllib.request.urlopen(self.url + '/api/desktop') as response:
            self.assertEqual(json.load(response), preferences)
        self.assertEqual(self.settings.load()['unrelated'], 'preserved')
        with self.post('/api/desktop', {'desktopFloatingSelections': []}):
            pass
        self.assertEqual(self.settings.load()['desktopFloatingSelections'], [])

    def test_native_provider_settings_accept_charset(self):
        with self.post('/api/providers', {'enabled': []}) as response:
            self.assertEqual(response.status, 200)

    def test_refresh_reports_pending_before_worker_starts_and_coalesces_clicks(self):
        workers = []
        def queue_worker(*, target, daemon):
            class Worker:
                def start(self):
                    workers.append(target)
            return Worker()
        # Patch only the worker constructor, keeping the HTTP server threads real.
        from types import SimpleNamespace
        with patch('quota.server.threading', SimpleNamespace(Thread=queue_worker)):
            with self.post('/api/refresh', {}) as response:
                request_id = json.load(response)['requestId']
            with self.post('/api/refresh', {}) as response:
                self.assertEqual(json.load(response)['requestId'], request_id)
            with urllib.request.urlopen(self.url + '/api/status') as response:
                self.assertEqual(json.load(response)['manualRefresh'], dict(id=request_id, state='running'))
        self.assertEqual(len(workers), 1)
        workers.pop()()
        with urllib.request.urlopen(self.url + '/api/status') as response:
            self.assertEqual(json.load(response)['manualRefresh'], dict(id=request_id, state='complete'))
        # A failed read is observable, never reported as a successful refresh.
        with patch.object(self.monitor, 'refresh', side_effect=RuntimeError('synthetic')), \
                patch('quota.server.threading', SimpleNamespace(Thread=queue_worker)):
            with self.post('/api/refresh', {}) as response:
                second_id = json.load(response)['requestId']
            workers.pop()()
        self.assertGreater(second_id, request_id)
        with urllib.request.urlopen(self.url + '/api/status') as response:
            self.assertEqual(json.load(response)['manualRefresh'], dict(id=second_id, state='failed'))

    def test_plain_json_still_works(self):
        with self.post('/api/desktop', {'desktopOpacity': 75}, 'application/json') as response:
            self.assertEqual(response.status, 200)

    def test_non_json_and_cross_origin_remain_rejected(self):
        for route in ('/api/desktop', '/api/providers'):
            with self.subTest(route=route):
                with self.assertRaises(urllib.error.HTTPError) as caught:
                    self.post(route, {}, 'text/plain')
                self.assertEqual(caught.exception.code, 400)
                with self.assertRaises(urllib.error.HTTPError) as caught:
                    self.post(route, {}, origin='https://example.com')
                self.assertEqual(caught.exception.code, 403)
