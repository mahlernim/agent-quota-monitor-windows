import json
import threading
import unittest
import urllib.request
from http.server import ThreadingHTTPServer

from quota.monitor import Monitor
from quota.server import app_version, handler


class MemoryVault:
    def load(self): return {}
    def save(self, value): self.data = value


class QuotaLabelTests(unittest.TestCase):
    def test_display_names_are_validated_before_saving(self):
        from quota.server import valid_quota_labels
        good = {'antigravity-gemini': {'initials': 'AG', 'name': 'Gemini'},
                'antigravity-claude-gpt': {'initials': 'AC', 'name': 'Claude and GPT'}}
        self.assertTrue(valid_quota_labels(good))
        self.assertTrue(valid_quota_labels({}))
        for bad in ({'unknown': {'initials': 'X', 'name': 'X'}},
                    {'codex': {'initials': '', 'name': 'Codex'}},
                    {'codex': {'initials': 'ABCD', 'name': 'Codex'}},
                    {'codex': {'initials': 'C-X', 'name': 'Codex'}},
                    {'codex': {'initials': 'CX', 'name': 'x' * 17}},
                    {'codex': {'initials': 'CX', 'name': ' Codex'}},
                    {'codex': {'initials': 'CX', 'name': 'Co\ndex'}},
                    {'codex': {'initials': 'CX', 'name': 'Codex', 'color': '#fff'}},
                    {'codex': 'CX'}, ['codex'], None):
            self.assertFalse(valid_quota_labels(bad), bad)


class ReaderVersionTests(unittest.TestCase):
    def test_launching_version_is_accepted_only_in_a_safe_form(self):
        self.assertEqual(app_version('0.2.4'), '0.2.4')
        self.assertEqual(app_version('0.2.4-beta.1+abc'), '0.2.4-beta.1+abc')
        for value in (None, '', '0.2.4 <script>', 'x' * 65, '0.2.4\n'):
            self.assertEqual(app_version(value), 'development')

    def test_status_reports_the_launching_app_version(self):
        server = ThreadingHTTPServer(('127.0.0.1', 0), handler(Monitor(MemoryVault(), clock=lambda: 1000), 0))
        port = server.server_address[1]
        server.RequestHandlerClass = handler(Monitor(MemoryVault(), clock=lambda: 1000), port, version='0.2.4')
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            with urllib.request.urlopen(f'http://127.0.0.1:{port}/api/status') as response:
                backend = json.load(response)['backend']
        finally:
            server.shutdown()
            server.server_close()
        self.assertEqual(backend['appVersion'], '0.2.4')
        self.assertEqual(backend['protocolVersion'], 1)


if __name__ == '__main__':
    unittest.main()
