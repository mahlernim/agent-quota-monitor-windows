import copy
from email.message import Message
import json
import unittest
from unittest.mock import Mock, patch
import urllib.error

from quota import model, providers
from quota.monitor import Monitor, poll_monitor
from quota.retry import MAX_TIMESTAMP


class Store:
    def __init__(self, data=None):
        self.data = copy.deepcopy(data or {})

    def load(self):
        return copy.deepcopy(self.data)

    def save(self, data):
        json.dumps(data, allow_nan=False)
        self.data = copy.deepcopy(data)


class RetryTests(unittest.TestCase):
    def setUp(self):
        self.now = 1000
        self.cache = Store()
        self.settings = Store({'enabledProviders': ['claude', 'codex'],
                               'desktopFloatingSelections': [{'accountId': 'saved-id', 'groupId': 'g', 'bucketId': 'b'}]})
        self.original_settings = copy.deepcopy(self.settings.data)
        self.monitor = Monitor(self.cache, clock=lambda: self.now, settings_vault=self.settings)
        self.reader = Mock(return_value=(model.claude({'five_hour': {'utilization': 20}}), 'same@example.com'))
        self.account = providers.account('claude', 'first', 'same@example.com', 'fixture', self.reader)
        self.account['sessionRevision'] = 1
        self.monitor.refresh_one(self.account)
        self.now = 1301

    def row(self, monitor=None):
        result = (monitor or self.monitor).snapshot()
        json.dumps(result, allow_nan=False)
        return next(row for row in result['accounts'] if row['id'] == self.account['id'])

    def fail(self, hint, code='rate_limited'):
        error = providers.ReadError(code)
        # Validate again at the scheduler boundary even if a reader bypasses ReadError.
        error.retry_after = hint
        self.reader.side_effect = error
        self.monitor.refresh_one(self.account)

    def restart(self):
        self.cache.save(self.monitor.rows)
        return Monitor(self.cache, clock=lambda: self.now, settings_vault=self.settings)

    def test_malformed_hints_use_backoff_and_keep_the_last_success(self):
        for hint in (None, '7200', True, -5, float('nan'), float('inf'), -float('inf'), {}, []):
            with self.subTest(hint=repr(hint)):
                self.monitor.rows[self.account['id']]['nextAttempt'] = 0
                self.monitor.rows[self.account['id']]['failures'] = 0
                self.fail(hint)
                row = self.row()
                self.assertEqual(row['nextAttempt'], 1601)
                self.assertEqual(row['lastSuccess'], 1000)
                self.assertEqual(row['groups'][0]['buckets'][0]['remaining'], 80)
                self.assertEqual(row['status'], 'stale')
                self.assertNotIn('retryState', row)

    def test_valid_two_day_wait_survives_restart_and_refresh(self):
        self.fail(172800)
        expected = 174101
        self.assertEqual(self.row()['nextAttempt'], expected)
        restarted = self.restart()
        self.reader.reset_mock()
        self.now = expected - 1
        restarted.refresh_one(self.account)
        self.reader.assert_not_called()
        self.assertEqual(self.row(restarted)['lastSuccess'], 1000)
        self.now = expected
        self.reader.side_effect = None
        restarted.refresh_one(self.account)
        self.reader.assert_called_once()
        self.assertEqual(self.row(restarted)['status'], 'live')
        self.assertEqual(self.settings.data, self.original_settings)

    def test_valid_provider_cooldown_is_not_bypassed_by_session_rotation(self):
        self.fail(172800, 'sign_in_required')
        self.account['sessionRevision'] = 2
        self.reader.reset_mock()
        self.monitor.refresh_one(self.account)
        self.reader.assert_not_called()

    def test_unrepresentable_valid_wait_is_persisted_as_suspended(self):
        for hint in (10 ** 400, 1e308, MAX_TIMESTAMP):
            with self.subTest(hint_type=type(hint).__name__):
                self.monitor.rows[self.account['id']].pop('retryState', None)
                self.monitor.rows[self.account['id']]['nextAttempt'] = 0
                self.fail(hint)
                row = self.row()
                self.assertIsNone(row['nextAttempt'])
                self.assertEqual(row['retryState'], 'suspended')
                self.assertEqual(row['retryReason'], 'provider_cooldown_unrepresentable')
                restarted = self.restart()
                self.reader.reset_mock()
                self.account['sessionRevision'] += 1
                self.now += 172800
                restarted.refresh_one(self.account)
                self.reader.assert_not_called()
                self.assertEqual(self.row(restarted)['lastSuccess'], 1000)
                self.assertEqual(self.settings.data, self.original_settings)

    def test_corrupt_saved_deadlines_recover_without_losing_accounts_or_pins(self):
        for value in (None, 'later', True, -1, float('nan'), float('inf'), [], {}):
            with self.subTest(value=repr(value)):
                original = copy.deepcopy(self.monitor.rows)
                original[self.account['id']].update(nextAttempt=value, failures='broken')
                restarted = Monitor(Store(original), clock=lambda: self.now, settings_vault=self.settings)
                row = self.row(restarted)
                self.assertEqual(row['nextAttempt'], self.now + 300)
                self.assertEqual(row['lastSuccess'], 1000)
                self.assertEqual(row['groups'], self.row()['groups'])
                self.assertEqual(row['identityStatus'], self.row()['identityStatus'])
                self.reader.reset_mock()
                restarted.refresh_one(self.account)
                self.reader.assert_not_called()
                self.assertEqual(self.settings.data, self.original_settings)

    def test_huge_saved_deadline_stays_paused_without_json_overflow(self):
        self.monitor.rows[self.account['id']]['nextAttempt'] = 10 ** 400
        restarted = self.restart()
        self.reader.reset_mock()
        restarted.refresh_one(self.account)
        self.reader.assert_not_called()
        self.assertEqual(self.row(restarted)['retryState'], 'suspended')

    def test_one_account_failure_does_not_prevent_an_independent_read(self):
        second_reader = Mock(return_value=(model.codex({'rate_limit': {'primary_window': {'used_percent': 30, 'limit_window_seconds': 18000}}}), 'same@example.com'))
        second = providers.account('codex', 'second', 'same@example.com', 'fixture', second_reader)
        original_refresh = self.monitor.refresh_one
        def isolated_refresh(account):
            if account['id'] == self.account['id']:
                raise RuntimeError('private-exception-text')
            return original_refresh(account)
        with patch.object(providers, 'claude_account', return_value=self.account), \
             patch.object(providers, 'codex_account', return_value=second), \
             patch.object(self.monitor, 'refresh_one', side_effect=isolated_refresh):
            self.assertTrue(self.monitor.refresh())
        second_reader.assert_called_once()
        self.assertEqual(self.monitor.rows[second['id']]['lastSuccess'], self.now)
        self.assertEqual(self.row()['lastSuccess'], 1000)
        self.assertFalse(self.monitor.snapshot()['pollingError'])
        self.assertEqual(next(row for row in self.monitor.snapshot()['accounts'] if row['id'] == second['id'])['status'], 'live')
        self.assertNotIn('private-exception-text', json.dumps(self.monitor.snapshot()))
        self.assertFalse(self.monitor.refresh_lock.locked())

    def test_poll_loop_recovers_on_the_next_iteration(self):
        monitor = Mock()
        monitor.refresh.side_effect = [RuntimeError('private'), True]
        stop = Mock()
        stop.is_set.side_effect = [False, False, True]
        flags = []
        stop.wait.side_effect = lambda seconds: flags.append(monitor.polling_error)
        poll_monitor(monitor, stop)
        self.assertEqual(flags, [True, False])
        self.assertEqual(monitor.refresh.call_count, 2)
        self.assertEqual([call.args for call in stop.wait.call_args_list], [(60,), (60,)])

    def test_read_error_normalizes_untrusted_metadata(self):
        for hint in (None, True, '300', float('inf'), float('nan'), -3):
            self.assertEqual(providers.ReadError('rate_limited', hint).retry_after, 0)
        self.assertEqual(providers.ReadError({'private': 'data'}).code, 'reader_failed')

    def test_scheduler_failure_marks_cached_live_values_stale_without_erasing_them(self):
        self.monitor.polling_error = True
        row = self.row()
        self.assertEqual(row['status'], 'stale')
        self.assertEqual(row['lastSuccess'], 1000)
        self.assertEqual(row['groups'][0]['buckets'][0]['remaining'], 80)


class HttpRetryTests(unittest.TestCase):
    def test_http_retry_after_future_date(self):
        headers = Message()
        headers['Retry-After'] = 'Fri, 02 Jan 1970 00:00:00 GMT'
        error = urllib.error.HTTPError('https://example.invalid', 429, 'limited', headers, None)
        opener = Mock()
        opener.open.side_effect = error
        with patch.object(providers.urllib.request, 'build_opener', return_value=opener), patch('time.time', return_value=1000):
            with self.assertRaises(providers.ReadError) as raised:
                providers.request('https://example.invalid')
        self.assertEqual(raised.exception.retry_after, 85400)

    def test_http_retry_after_seconds_dates_invalid_and_huge_values(self):
        for header, minimum, maximum in (
                ('172800', 172800, 172800),
                ('Wed, 21 Oct 2015 07:28:00 GMT', 0, 0),
                ('not-a-date', 0, 0), ('-1', 0, 0),
                ('9' * 5000, MAX_TIMESTAMP + 1, MAX_TIMESTAMP + 1)):
            with self.subTest(header=header[:40]):
                headers = Message()
                headers['Retry-After'] = header
                error = urllib.error.HTTPError('https://example.invalid', 429, 'limited', headers, None)
                opener = Mock()
                opener.open.side_effect = error
                with patch.object(providers.urllib.request, 'build_opener', return_value=opener):
                    with self.assertRaises(providers.ReadError) as raised:
                        providers.request('https://example.invalid')
                self.assertGreaterEqual(raised.exception.retry_after, minimum)
                self.assertLessEqual(raised.exception.retry_after, maximum)


if __name__ == '__main__':
    unittest.main()
