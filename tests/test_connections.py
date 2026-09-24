from unittest.mock import patch
import copy
import threading
import unittest
import json
import urllib.request
import urllib.error
from http.server import ThreadingHTTPServer
from quota.server import handler
from quota.connections import Connections


class Monitor:
    def __init__(self):
        self.rows = [dict(id='original', provider='claude', label='same@example.com', status='stale', lastSuccess=1)]
        self.next_discovery = 999
    def snapshot(self):
        return {'accounts': copy.deepcopy(self.rows)}
    def refresh(self):
        return False


class Process:
    def __init__(self, status=None):
        self.status = status
        self.terminated = False
    def poll(self): return self.status
    def terminate(self):
        self.terminated = True
        self.status = -1
    def wait(self, timeout): return self.status


class ConnectionTests(unittest.TestCase):
    def test_claude_npm_install_is_detected_without_exe_on_path(self):
        from pathlib import Path
        from tempfile import TemporaryDirectory
        from quota.connections import client_command
        with TemporaryDirectory() as directory:
            root = Path(directory)
            executable = root / 'npm/node_modules/@anthropic-ai/claude-code/bin/claude.exe'
            executable.parent.mkdir(parents=True)
            executable.touch()
            with patch('quota.connections._environment_path', return_value=root), patch('quota.connections.Path.home', return_value=root), patch('quota.connections.shutil.which', return_value=None):
                self.assertEqual(client_command('claude'), [str(executable.resolve()), 'auth', 'login', '--claudeai'])

    def setUp(self):
        self.monitor = Monitor()
        self.process = Process()
        self.connection = Connections(self.monitor, resolver=lambda p: ['official.exe'],
                                      launcher=lambda cmd: self.process, clock=lambda: 100)
    def tearDown(self):
        self.connection.close()
        if self.connection.worker:
            self.connection.worker.join(3)

    def test_copilot_command_uses_official_github_browser_flow(self):
        from quota.connections import client_command
        with patch('quota.connections.shutil.which', return_value='C:/tools/gh.exe'):
            self.assertEqual(client_command('copilot'), ['C:/tools/gh.exe', 'auth', 'login', '--hostname', 'github.com', '--git-protocol', 'https', '--web'])

    def test_copilot_binds_only_after_successful_login(self):
        self.process.status = 0
        observed = threading.Event()
        self.monitor.refresh = observed.set
        with patch('quota.copilot.command', return_value=['reader']), patch('quota.copilot.bind_current_account') as bind:
            job = self.connection.start('copilot')
            self.assertTrue(observed.wait(3))
            bind.assert_called_once()
            self.assertEqual(self.connection.job['state'], 'verifying')
            self.connection.cancel(job['id'])
            self.connection.worker.join(3)

    def test_copilot_connect_links_the_existing_sign_in_without_launching_a_client(self):
        launched = []
        self.connection.launcher = lambda cmd: launched.append(cmd) or self.process
        observed = threading.Event()
        self.monitor.refresh = observed.set
        with patch('quota.copilot.command', return_value=['reader']), patch('quota.copilot.bind_current_account') as bind:
            job = self.connection.start('copilot', verify=True)
            self.assertTrue(observed.wait(3))
            bind.assert_called_once()
            self.assertEqual(launched, [])
            self.assertEqual(self.connection.job['state'], 'verifying')
            self.connection.cancel(job['id'])
            self.connection.worker.join(3)

    def test_connect_is_copilot_only_and_requires_a_boolean(self):
        for provider, verify in (('claude', True), ('codex', True), ('copilot', 'yes'), ('copilot', 1)):
            with self.assertRaises(ValueError):
                self.connection.start(provider, verify=verify)
        self.assertIsNone(self.connection.worker)

    def test_failed_copilot_connect_points_to_sign_in(self):
        from quota.providers import ReadError
        with patch('quota.copilot.command', return_value=['reader']), \
                patch('quota.copilot.bind_current_account', side_effect=ReadError('copilot_reader_failed')):
            self.connection.start('copilot', verify=True)
            self.connection.worker.join(3)
        self.assertEqual(self.connection.job['state'], 'failed')
        self.assertIn('Use Sign in instead', self.connection.job['message'])

    def test_copilot_distinguishes_missing_setup_from_an_unlinked_account(self):
        from quota import copilot
        from quota.providers import ReadError
        class Empty:
            def load(self): return {}
        for ready, code in ((False, 'copilot_setup_required'), (True, 'copilot_not_connected')):
            with patch.object(copilot, 'descriptor_vault', return_value=Empty()), patch.object(copilot, 'setup_ready', return_value=ready):
                with self.assertRaises(ReadError) as raised:
                    copilot.copilot_account()
            self.assertEqual(raised.exception.code, code)

    def test_copilot_setup_missing_never_launches_login(self):
        with patch('quota.copilot.command', side_effect=RuntimeError('missing')):
            with self.assertRaisesRegex(ValueError, 'setup is required'):
                self.connection.start('copilot')
        self.assertIsNone(self.connection.worker)

    def test_invalid_provider_and_account_never_launch(self):
        for provider in ('shell', None, ['claude']):
            with self.assertRaises(ValueError): self.connection.start(provider)
        with self.assertRaises(ValueError): self.connection.start('claude', 'other')
        with self.assertRaises(ValueError): self.connection.start('codex', 'original')
        self.assertIsNone(self.connection.worker)

    def test_missing_client(self):
        self.connection.resolver = lambda p: None
        with self.assertRaises(ValueError): self.connection.start('claude')

    def test_cancel_does_not_sign_out_and_blocks_duplicate(self):
        job = self.connection.start('claude', 'original')
        with self.assertRaises(RuntimeError): self.connection.start('codex')
        with self.assertRaises(ValueError): self.connection.cancel('old-job')
        self.connection.cancel(job['id'])
        self.connection.worker.join(3)
        self.assertEqual(self.connection.job['state'], 'cancelled')
        self.assertEqual(self.monitor.rows[0]['id'], 'original')

    def test_old_quota_cannot_verify_login(self):
        job = self.connection.start('claude', 'original')
        self.monitor.rows[0]['status'] = 'live'
        self.assertFalse(self.connection._verify(job))
        self.monitor.rows[0]['lastSuccess'] = 101
        self.assertTrue(self.connection._verify(job))
        self.assertEqual(self.connection.job['state'], 'connected')

    def test_same_email_different_identity_is_not_reconnected(self):
        job = self.connection.start('claude', 'original')
        self.monitor.rows.append(dict(id='different', provider='claude', label='same@example.com', status='live', lastSuccess=101))
        self.assertTrue(self.connection._verify(job))
        self.assertEqual(self.connection.job['state'], 'different_account')

    def test_cancelled_job_cannot_be_overwritten_by_late_success(self):
        job = self.connection.start('claude', 'original')
        self.connection.cancel(job['id'])
        self.monitor.rows[0].update(status='live', lastSuccess=101)
        self.connection._verify(job)
        self.assertEqual(self.connection.job['state'], 'cancelled')

    def test_failed_client_output_is_not_exposed(self):
        def fail(cmd): raise OSError('secret-token-must-not-leak')
        self.connection.launcher = fail
        self.connection.start('claude')
        self.connection.worker.join(3)
        self.assertEqual(self.connection.job['state'], 'failed')
        self.assertNotIn('secret-token', str(self.connection.snapshot()))

    def test_timeout_terminates_only_owned_login(self):
        self.connection.clock = lambda: 1000
        self.connection.job = dict(id='test', provider='claude', state='waiting', startedAt=1, deadline=2, accountId=None)
        self.connection._run(['official.exe'], copy.deepcopy(self.connection.job), threading.Event())
        self.assertEqual(self.connection.job['state'], 'timed_out')
        self.assertTrue(self.process.terminated)

    def test_nonzero_client_exit_is_failure_not_connection(self):
        self.process.status = 1
        self.connection.start('claude')
        self.connection.worker.join(3)
        self.assertEqual(self.connection.job['state'], 'failed')

    def test_early_codex_exit_explains_possible_fixes_without_output(self):
        self.process.status = 1
        self.connection.start('codex')
        self.connection.worker.join(3)
        message = self.connection.job['message']
        self.assertEqual(self.connection.job['state'], 'failed')
        self.assertIn('exited before sign-in could start', message)
        self.assertIn('npm install -g @openai/codex@latest', message)

    def test_late_codex_exit_keeps_the_generic_failure(self):
        now = [100]
        self.connection.clock = lambda: now[0]
        self.connection.job = dict(id='late', provider='codex', state='waiting', startedAt=100, deadline=700, accountId=None)
        self.process.status = 1
        now[0] = 160
        self.connection._run(['official.exe'], copy.deepcopy(self.connection.job), threading.Event())
        self.assertNotIn('exited before sign-in', self.connection.job['message'])

    def test_successful_client_exit_still_requires_fresh_quota(self):
        self.process.status = 0
        observed = threading.Event()
        self.monitor.refresh = observed.set
        job = self.connection.start('claude')
        self.assertTrue(observed.wait(3))
        self.assertEqual(self.connection.job['state'], 'verifying')
        self.connection.cancel(job['id'])

    def test_opening_antigravity_desktop_never_claims_cli_quota_verification(self):
        self.monitor.rows = [dict(id='cli', provider='antigravity', label='CLI',
                                  source='Official Antigravity CLI /usage (desktop app not required)',
                                  status='live', lastSuccess=101)]
        self.connection.start('antigravity')
        self.connection.worker.join(3)
        self.assertEqual(self.connection.job['state'], 'opened')
        self.assertIn('press Refresh', self.connection.job['message'])
        self.assertEqual(self.monitor.next_discovery, 0)
        self.assertFalse(self.process.terminated)

    def test_login_route_rejects_cross_origin_before_launch(self):
        server = ThreadingHTTPServer(('127.0.0.1', 0), handler(self.monitor, 0, self.connection))
        port = server.server_port
        server.RequestHandlerClass = handler(self.monitor, port, self.connection)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            req = urllib.request.Request(f'http://127.0.0.1:{port}/api/connections/start',
                  data=b'{"provider":"claude"}', headers={'Origin':'https://untrusted.example',
                  'Content-Type':'application/json', 'X-Quota-Request':'refresh'})
            with self.assertRaises(urllib.error.HTTPError) as caught:
                urllib.request.urlopen(req)
            self.assertEqual(caught.exception.code, 403)
            self.assertIsNone(self.connection.worker)
            req.headers['Origin'] = f'http://127.0.0.1:{port}'
            with urllib.request.urlopen(req) as response:
                self.assertEqual(response.status, 202)
                job = json.load(response)
            self.assertEqual(job['provider'], 'claude')
            self.assertNotIn('command', job)
            self.connection.cancel(job['id'])
        finally:
            server.shutdown()
            server.server_close()


if __name__ == '__main__':
    unittest.main()
