import json
import os
import socket
import threading
import unittest
from http.server import ThreadingHTTPServer
from unittest.mock import patch
import urllib.error
import urllib.request

from quota import connections, model, providers
from quota.monitor import INTERVAL, Monitor
from quota.server import handler


class MemoryVault:
    def load(self): return {}
    def save(self, value): self.data = value


def groups():
    return model.claude({'five_hour': {'utilization': 20}})


def fail(code, retry_after=0):
    def read():
        raise providers.ReadError(code, retry_after)
    return read


class AntigravityDiscoveryTests(unittest.TestCase):
    def test_missing_cli_without_desktop_reports_the_cli_error(self):
        with patch('quota.antigravity_cli.cli_account', side_effect=providers.ReadError('antigravity_cli_unavailable')), \
                patch.object(providers, 'antigravity_desktop_accounts', return_value=[]):
            with self.assertRaises(providers.ReadError) as raised:
                providers.antigravity_accounts()
        self.assertEqual(raised.exception.code, 'antigravity_cli_unavailable')

    def test_unexpected_cli_failure_is_reported_without_details(self):
        with patch('quota.antigravity_cli.cli_account', side_effect=OSError('secret path')), \
                patch.object(providers, 'antigravity_desktop_accounts', return_value=[]):
            with self.assertRaises(providers.ReadError) as raised:
                providers.antigravity_accounts()
        self.assertEqual(raised.exception.code, 'antigravity_cli_failed')

    def test_placeholder_and_stale_desktop_card_explain_the_missing_cli(self):
        now = [1000]
        monitor = Monitor(MemoryVault(), clock=lambda: now[0])
        monitor.enabled = {'antigravity'}
        desktop = providers.account('antigravity', 'desktop', 'user@example.com',
                                    'Official running Antigravity local service', lambda: (groups(), 'user@example.com'))
        monitor.refresh_one(desktop)
        now[0] = 1000 + INTERVAL * 3
        with patch.object(providers, 'antigravity_accounts', side_effect=providers.ReadError('antigravity_cli_unavailable')):
            monitor.refresh()
        row = monitor.snapshot()['accounts'][0]
        self.assertEqual((row['status'], row['error']), ('stale', 'antigravity_cli_unavailable'))

    def test_desktop_lookup_is_skipped_without_a_language_server(self):
        with patch.object(providers, 'process_names', return_value={'explorer.exe'}), \
                patch.object(providers, 'powershell') as shell:
            self.assertEqual(providers.antigravity_desktop_accounts(), [])
        shell.assert_not_called()

    def test_desktop_lookup_runs_when_listing_is_unavailable_or_a_server_exists(self):
        for names in (None, {'language_server_windows_x64.exe'}):
            with patch.object(providers, 'process_names', return_value=names), \
                    patch.object(providers, 'powershell', return_value='[]') as shell:
                self.assertEqual(providers.antigravity_desktop_accounts(), [])
            shell.assert_called_once()

    @unittest.skipUnless(os.name == 'nt', 'Windows process snapshot')
    def test_process_listing_reads_names_only(self):
        names = providers.process_names()
        self.assertIsInstance(names, set)
        self.assertTrue(any(name.startswith('python') for name in names))

    def test_install_offer_follows_the_cli_on_disk(self):
        monitor = Monitor(MemoryVault(), clock=lambda: 1000)
        link = connections.Connections(monitor, resolver=lambda provider: None)
        with patch('quota.antigravity_cli.command', side_effect=providers.ReadError('antigravity_cli_unavailable')):
            self.assertFalse(link.snapshot()['clients']['antigravityCli'])
        with patch('quota.antigravity_cli.command', return_value=['agy.exe', '-p', '/usage']):
            self.assertTrue(link.snapshot()['clients']['antigravityCli'])


class DiscoveryMissTests(unittest.TestCase):
    def setUp(self):
        self.now = [1000]
        self.monitor = Monitor(MemoryVault(), clock=lambda: self.now[0])
        self.monitor.enabled = {'claude'}
        self.account = providers.account('claude', 'id', 'label', 'fixture', lambda: (groups(), 'label'))

    def miss(self, code='local_session_unavailable'):
        self.monitor.next_discovery = 0
        with patch.object(providers, 'claude_account', side_effect=providers.ReadError(code)):
            self.monitor.refresh()
        return self.monitor.snapshot()['accounts'][0]

    def test_recent_reading_ages_out_normally_with_the_reason(self):
        self.monitor.refresh_one(self.account)
        self.now[0] = 1060
        row = self.miss()
        self.assertEqual((row['status'], row['error']), ('live', 'local_session_unavailable'))
        self.assertEqual(row['groups'][0]['buckets'][0]['remaining'], 80)
        self.now[0] = 1000 + INTERVAL * 2 + 1
        self.assertEqual(self.monitor.snapshot()['accounts'][0]['status'], 'stale')
        self.assertEqual(self.miss()['status'], 'stale')

    def test_returning_source_is_read_without_an_old_timer(self):
        self.monitor.refresh_one(self.account)
        self.now[0] = 1060
        self.assertNotIn('nextAttempt', self.miss())
        reads = []
        self.account['read'] = lambda: reads.append(1) or (groups(), 'label')
        self.monitor.refresh_one(self.account)
        self.assertEqual(reads, [1])

    def test_provider_cooldown_survives_a_discovery_miss(self):
        self.account['read'] = fail('rate_limited', 7200)
        self.monitor.refresh_one(self.account)
        self.assertEqual(self.miss()['nextAttempt'], 1000 + 7200)


class ClaudeExpiryTests(unittest.TestCase):
    def fixture(self, expires):
        def load(path):
            if str(path).endswith('.credentials.json'):
                return {'claudeAiOauth': {'accessToken': 'test-only', 'expiresAt': expires}}
            return {'oauthAccount': {'accountUuid': 'expected', 'emailAddress': 'user@example.com'}}
        return load

    def test_expired_session_is_not_sent_and_expiry_is_reported(self):
        with patch.object(providers, 'load', side_effect=self.fixture(2_000_000)), \
                patch.object(providers.Path, 'stat') as stat, patch.object(providers, 'request') as request, \
                patch.object(providers.time, 'time', return_value=2_001):
            stat.return_value.st_mtime_ns = 1
            account = providers.claude_account()
            self.assertEqual(account['sessionExpiresAt'], 2000)
            with self.assertRaises(providers.ReadError) as raised:
                account['read']()
        self.assertEqual(raised.exception.code, 'session_expired')
        request.assert_not_called()

    def test_valid_or_unreported_expiry_still_reads(self):
        for expires in (2_000_000, None, 'soon', float('nan')):
            with patch.object(providers, 'load', side_effect=self.fixture(expires)), \
                    patch.object(providers.Path, 'stat') as stat, \
                    patch.object(providers, 'request', return_value={'account': {'uuid': 'expected'}}) as request, \
                    patch.object(providers.time, 'time', return_value=1_999):
                stat.return_value.st_mtime_ns = 1
                account = providers.claude_account()
                account['read']()
            self.assertEqual(request.call_count, 2)

    def test_expired_session_retries_on_schedule_and_resumes_after_renewal(self):
        now = [1000]
        monitor = Monitor(MemoryVault(), clock=lambda: now[0])
        account = providers.account('claude', 'id', 'label', 'fixture', fail('session_expired'))
        account['sessionRevision'] = 1
        for _ in range(4):
            monitor.refresh_one(account)
            self.assertEqual(monitor.rows[account['id']]['nextAttempt'] - now[0], INTERVAL)
            now[0] += 1
            monitor.refresh_one(account)
            now[0] = monitor.rows[account['id']]['nextAttempt']
        now[0] -= 60
        account['sessionRevision'] = 2
        account['read'] = lambda: (groups(), 'label')
        monitor.refresh_one(account)
        self.assertEqual(monitor.snapshot()['accounts'][0]['status'], 'live')


class CodexRenewalTests(unittest.TestCase):
    def test_last_refresh_is_reported_without_decoding_the_token(self):
        auth = {'tokens': {'account_id': 'acct', 'access_token': 'not.a.jwt'}, 'last_refresh': '2026-09-19T00:55:19.1326212Z'}
        with patch.object(providers, 'load', return_value=auth), patch.object(providers.Path, 'stat') as stat:
            stat.return_value.st_mtime_ns = 1
            account = providers.codex_account()
        self.assertAlmostEqual(account['sessionRenewedAt'], 1789779319.13, places=1)

    def test_missing_or_invalid_last_refresh_is_unreported(self):
        for value in (None, '', 'yesterday', 12345, '2026-09-19T00:55:19'):
            self.assertIsNone(providers.codex_renewed(value))


class NetworkTests(unittest.TestCase):
    def raise_url_error(self, reason):
        class Opener:
            def open(self, *args, **kwargs):
                raise urllib.error.URLError(reason)
        with patch.object(providers.urllib.request, 'build_opener', return_value=Opener()):
            with self.assertRaises(providers.ReadError) as raised:
                providers.request('https://example.invalid/usage')
        return raised.exception.code

    def test_failures_before_the_provider_are_classified(self):
        self.assertEqual(self.raise_url_error(socket.gaierror(11001, 'lookup failed')), 'network_unavailable')
        self.assertEqual(self.raise_url_error(ConnectionRefusedError()), 'network_unavailable')
        unreachable = OSError('unreachable')
        unreachable.winerror = 10051
        self.assertEqual(self.raise_url_error(unreachable), 'network_unavailable')
        self.assertEqual(self.raise_url_error(TimeoutError()), 'connection_or_response_error')

    def test_offline_failures_do_not_escalate_backoff(self):
        now = [1000]
        monitor = Monitor(MemoryVault(), clock=lambda: now[0])
        account = providers.account('codex', 'id', 'label', 'fixture', fail('network_unavailable'))
        for _ in range(5):
            monitor.refresh_one(account)
            row = monitor.rows[account['id']]
            self.assertEqual(row['nextAttempt'] - now[0], INTERVAL)
            now[0] = row['nextAttempt']
        account['read'] = fail('provider_http_503')
        monitor.refresh_one(account)
        self.assertEqual(monitor.rows[account['id']]['nextAttempt'] - now[0], 3600)

    def test_wake_retries_network_failures_but_keeps_provider_cooldowns(self):
        now = [1000]
        monitor = Monitor(MemoryVault(), clock=lambda: now[0])
        offline = providers.account('codex', 'offline', 'label', 'fixture', fail('network_unavailable'))
        limited = providers.account('claude', 'limited', 'label', 'fixture', fail('rate_limited', 7200))
        monitor.refresh_one(offline)
        monitor.refresh_one(limited)
        # Keep discovery out of this check. Wake itself must not bypass the cooldown.
        monitor.enabled = set()
        self.assertTrue(monitor.wake())
        self.assertNotIn('nextAttempt', monitor.rows[offline['id']])
        self.assertEqual(monitor.rows[limited['id']]['nextAttempt'], 8200)
        monitor.rows[offline['id']]['nextAttempt'] = 5000
        self.assertFalse(monitor.wake())
        self.assertEqual(monitor.rows[offline['id']]['nextAttempt'], 5000)


class WakeRouteTests(unittest.TestCase):
    def setUp(self):
        self.monitor = Monitor(MemoryVault(), clock=lambda: 1000)
        self.server = ThreadingHTTPServer(('127.0.0.1', 0), handler(self.monitor, 0))
        self.port = self.server.server_address[1]
        self.server.RequestHandlerClass = handler(self.monitor, self.port)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()

    def post(self, headers):
        request = urllib.request.Request(f'http://127.0.0.1:{self.port}/api/wake', data=b'{}', method='POST',
                                         headers={'Content-Type': 'application/json', **headers})
        try:
            with urllib.request.urlopen(request) as response:
                return response.status
        except urllib.error.HTTPError as err:
            return err.code

    def test_wake_requires_the_native_request_boundary(self):
        with patch.object(self.monitor, 'wake') as wake:
            self.assertEqual(self.post({}), 403)
            origin = f'http://127.0.0.1:{self.port}'
            self.assertEqual(self.post({'Origin': origin, 'X-Quota-Request': 'refresh'}), 202)
            for _ in range(50):
                if wake.called:
                    break
                threading.Event().wait(0.02)
            wake.assert_called_once()


if __name__ == '__main__':
    unittest.main()
