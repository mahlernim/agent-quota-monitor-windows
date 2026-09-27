import json
import subprocess
import unittest
from unittest.mock import Mock, patch
from quota import antigravity_cli as cli, providers
from quota.monitor import Monitor
from test_removal import Store


USAGE = ('Gemini Models\tWeekly Limit Remaining\t71%\t2026-09-26T00:46:07Z\n'
         'Gemini Models\tFive Hour Limit Remaining\t100%\t2026-09-21T17:11:37Z\n'
         'Claude and GPT models\tWeekly Limit Remaining\t0%\t2026-09-28T12:11:37Z\n')


class ParsingTests(unittest.TestCase):
    def test_userinfo_requires_stable_subject_and_verified_email(self):
        for value in ({'email': 'same@example.com', 'email_verified': True},
                      {'sub': 'id', 'email': 'same@example.com', 'email_verified': False}):
            with patch.object(cli, 'request', return_value=value), self.assertRaises(providers.ReadError):
                cli.profile('fixture')

    def test_custom_auth_mode_is_rejected(self):
        with patch.object(cli.Path, 'exists', return_value=False), patch.dict(cli.os.environ, {'GEMINI_API_KEY': 'fixture'}):
            with self.assertRaises(providers.ReadError) as caught:
                cli.check_auth_mode()
            self.assertEqual(caught.exception.code, 'antigravity_cli_auth_unsupported')

    def test_groups_and_windows_remain_separate_and_missing_is_not_invented(self):
        groups = cli.parse_usage(USAGE)
        self.assertEqual([g['id'] for g in groups], ['gemini', 'claude-gpt'])
        self.assertEqual([b['remaining'] for b in groups[0]['buckets']], [71, 100])
        self.assertEqual([b['windowSeconds'] for b in groups[0]['buckets']], [604800, 18000])
        self.assertEqual(len(groups[1]['buckets']), 1)
        self.assertEqual(groups[1]['buckets'][0]['remaining'], 0)

    def test_malformed_duplicate_and_unknown_output_is_rejected(self):
        for text in ('', 'login required', USAGE.replace('71%', '101%'), USAGE.replace('71%', 'NaN%'),
                     USAGE.replace('2026-09-26T00:46:07Z', 'tomorrow'), USAGE + USAGE,
                     USAGE.replace('Gemini Models', 'Unknown Group')):
            with self.subTest(text=text), self.assertRaises(providers.ReadError):
                cli.parse_usage(text)

    def test_disabled_window_preserves_other_quotas_without_inventing_a_value(self):
        disabled = 'Claude and GPT models\tFive Hour Limit Remaining\tdisabled\t\n'
        self.assertEqual(cli.parse_usage(USAGE + disabled), cli.parse_usage(USAGE))
        self.assertEqual(cli.parse_usage(disabled + USAGE), cli.parse_usage(USAGE))
        with self.assertRaises(providers.ReadError) as caught:
            cli.parse_usage(disabled)
        self.assertEqual(caught.exception.code, 'quota_not_reported')

    def test_disabled_rows_do_not_hide_duplicates_or_malformed_values(self):
        disabled = 'Claude and GPT models\tFive Hour Limit Remaining\tdisabled\t\n'
        active = disabled.replace('disabled\t', '50%\t2026-09-28T12:11:37Z')
        for extra in (disabled + disabled, disabled + active, active + disabled,
                      disabled.replace('disabled\t', 'disabled\t2026-09-28T12:11:37Z'),
                      disabled.replace('disabled', 'unknown')):
            with self.subTest(extra=extra), self.assertRaises(providers.ReadError):
                cli.parse_usage(USAGE + extra)

    def test_subprocess_is_quota_only_bounded_and_redacts_failure(self):
        with patch.object(cli, 'command', return_value=['agy.exe', '-p', '/usage', '--print-timeout', '20s']), patch.object(cli.subprocess, 'run') as run:
            run.return_value = subprocess.CompletedProcess([], 0, USAGE.encode())
            self.assertEqual(len(cli.usage()), 2)
            self.assertEqual(run.call_args.args[0][2], '/usage')
            self.assertEqual(run.call_args.kwargs['stdin'], subprocess.DEVNULL)
            self.assertEqual(run.call_args.kwargs['timeout'], 30)
            self.assertEqual(run.call_args.kwargs['env']['AGY_CLI_DISABLE_AUTO_UPDATE'], 'true')
            self.assertEqual(run.call_args.kwargs['env'].get('PATH'), cli.os.environ.get('PATH'))
            for failure, expected in ((subprocess.TimeoutExpired('secret', 30), 'antigravity_cli_timeout'),
                                      (OSError('secret'), 'antigravity_cli_failed')):
                run.side_effect = failure
                with self.assertRaises(providers.ReadError) as caught:
                    cli.usage()
                self.assertEqual(caught.exception.code, expected)
                self.assertNotIn('secret', str(caught.exception))


class IdentityTests(unittest.TestCase):
    def setUp(self):
        cli._identity_retry.clear()
        self.store = Store()
        for name, kwargs in [('check_auth_mode', {'return_value': None}),
                             ('command', {'return_value': ['agy.exe']}),
                             ('descriptor_vault', {'return_value': self.store}),
                             ('credential', {'return_value': ('access-fixture', 'revision-a')}),
                             ('profile', {'return_value': ('google-a', 'same@example.com')}),
                             ('usage', {'return_value': cli.parse_usage(USAGE)})]:
            patcher = patch.object(cli, name, **kwargs)
            setattr(self, name, patcher.start())
            self.addCleanup(patcher.stop)

    def test_discovery_binds_once_without_quota_reads_and_never_saves_tokens(self):
        first = cli.cli_account()
        self.assertEqual(cli.cli_account()['id'], first['id'])
        self.profile.assert_called_once()
        self.usage.assert_not_called()
        self.assertNotIn('access-fixture', json.dumps(self.store.data))

    def test_failed_identity_discovery_honors_retry_after(self):
        self.profile.side_effect = providers.ReadError('rate_limited', 7200)
        with patch.object(cli.time, 'monotonic', return_value=1000):
            for _ in range(2):
                with self.assertRaises(providers.ReadError):
                    cli.cli_account()
        self.profile.assert_called_once()
        with patch.object(cli.time, 'monotonic', return_value=8201):
            with self.assertRaises(providers.ReadError):
                cli.cli_account()
        self.assertEqual(self.profile.call_count, 2)

    def test_cli_identity_is_separate_from_desktop_and_same_email_other_account(self):
        first = cli.cli_account()
        desktop = providers.account('antigravity', 'directory\nsame@example.com', 'same@example.com', '', lambda: None)
        self.assertNotEqual(first['id'], desktop['id'])
        self.credential.return_value = ('other-access', 'revision-b')
        self.profile.return_value = ('google-b', 'same@example.com')
        self.assertNotEqual(cli.cli_account()['id'], first['id'])

    def test_unrepresentable_identity_wait_never_retries_early(self):
        self.profile.side_effect = providers.ReadError('rate_limited', 10 ** 400)
        with patch.object(cli.time, 'monotonic', return_value=1000):
            with self.assertRaises(providers.ReadError):
                cli.cli_account()
        with patch.object(cli.time, 'monotonic', return_value=1000000):
            with self.assertRaises(providers.ReadError):
                cli.cli_account()
        self.profile.assert_called_once()

    def test_account_switch_before_read_never_launches_quota_command(self):
        account = cli.cli_account()
        self.credential.return_value = ('other', 'revision-b')
        with self.assertRaises(providers.ReadError):
            account['read']()
        self.usage.assert_not_called()

    def test_account_switch_during_read_rejects_quotas(self):
        account = cli.cli_account()
        self.profile.return_value = ('google-b', 'same@example.com')
        with self.assertRaises(providers.ReadError) as caught:
            account['read']()
        self.assertEqual(caught.exception.code, 'identity_changed')

    def test_official_token_rotation_keeps_identity_and_pins(self):
        account = cli.cli_account()
        self.credential.side_effect = [('old', 'revision-a'), ('new', 'revision-b'), ('new', 'revision-b')]
        groups, label = account['read']()
        self.assertEqual(len(groups), 2)
        self.assertTrue(label.endswith('CLI'))
        self.credential.side_effect = None
        self.credential.return_value = ('new', 'revision-b')
        self.assertEqual(cli.cli_account()['id'], account['id'])

    def test_failure_keeps_stale_values_and_backoff_and_hidden_skips_cli(self):
        now = [1000]
        monitor = Monitor(Store(), clock=lambda: now[0])
        account = cli.cli_account()
        monitor.refresh_one(account)
        self.usage.side_effect = providers.ReadError('antigravity_cli_timeout')
        now[0] = 1301
        monitor.refresh_one(account)
        row = monitor.snapshot()['accounts'][0]
        self.assertEqual(row['status'], 'stale')
        self.assertEqual(row['lastSuccess'], 1000)
        self.assertEqual(row['groups'][0]['buckets'][0]['remaining'], 71)
        monitor.refresh_one(account)
        self.assertEqual(self.usage.call_count, 2)
        monitor.hidden.add(account['id'])
        now[0] = 9999
        monitor.refresh_one(account)
        self.assertEqual(self.usage.call_count, 2)

    def test_disabled_window_replaces_previous_active_window_on_success(self):
        now = [1000]
        monitor = Monitor(Store(), clock=lambda: now[0])
        account = cli.cli_account()
        row = 'Claude and GPT models\tFive Hour Limit Remaining\t'
        self.usage.return_value = cli.parse_usage(USAGE + row + '50%\t2026-09-28T12:11:37Z\n')
        monitor.refresh_one(account)
        now[0] = 1301
        self.usage.return_value = cli.parse_usage(USAGE + row + 'disabled\t\n')
        monitor.refresh_one(account)
        result = monitor.snapshot()['accounts'][0]
        self.assertIsNone(result['error'])
        self.assertEqual(result['lastSuccess'], 1301)
        self.assertEqual([b['id'] for b in result['groups'][1]['buckets']], ['weekly'])

    def test_desktop_unavailable_still_returns_cli(self):
        with patch.object(providers, 'antigravity_desktop_accounts', side_effect=providers.ReadError('local_discovery_failed')):
            self.assertEqual(len(providers.antigravity_accounts()), 1)

    def test_missing_cli_keeps_desktop(self):
        self.command.side_effect = providers.ReadError('antigravity_cli_unavailable')
        with patch.object(providers, 'antigravity_desktop_accounts', return_value=['desktop']):
            self.assertEqual(providers.antigravity_accounts(), ['desktop'])

    def test_cli_is_preferred_without_polling_desktop(self):
        with patch.object(providers, 'antigravity_desktop_accounts', return_value=[{'id': 'desktop'}]) as desktop:
            found = providers.antigravity_accounts()
        self.assertEqual(len(found), 1)
        desktop.assert_not_called()

    def test_successful_cli_suppresses_desktop_until_stale(self):
        monitor = Monitor(Store(), clock=lambda: 1000, desired_google=['same@example.com'])
        legacy = providers.account('antigravity', 'legacy', 'same@example.com',
                                   'Official running Antigravity local service',
                                   lambda: (cli.parse_usage(USAGE), 'same@example.com'))
        monitor.refresh_one(legacy)
        account = cli.cli_account()
        monitor.refresh_one(account)
        self.assertEqual([r['id'] for r in monitor.snapshot()['accounts']], [account['id']])
        monitor.rows[account['id']]['status'] = 'stale'
        self.assertEqual([r['id'] for r in monitor.snapshot()['accounts']], [legacy['id']])
        self.assertIn(legacy['id'], monitor.rows)


class DesktopFallbackTests(unittest.TestCase):
    def setUp(self):
        self.now = [1000]
        self.monitor = Monitor(Store(), clock=lambda: self.now[0])
        self.monitor.enabled = {'antigravity'}
        self.cli_read = Mock(return_value=(cli.parse_usage(USAGE), 'same@example.com · CLI'))
        self.desktop_read = Mock(return_value=(cli.parse_usage(USAGE), 'same@example.com'))
        self.cli = providers.account('antigravity', 'cli-subject', 'same@example.com · CLI',
                                     'Official Antigravity CLI /usage (desktop app not required)', self.cli_read)
        self.desktop = providers.account('antigravity', 'desktop-session', 'same@example.com',
                                         'Official running Antigravity local service', self.desktop_read)
        for target, value in [('antigravity_accounts', [self.cli]), ('antigravity_desktop_accounts', [self.desktop])]:
            p = patch.object(providers, target, return_value=value)
            setattr(self, target, p.start())
            self.addCleanup(p.stop)

    def test_read_failure_uses_separate_desktop_identity_and_recovers_to_cli(self):
        self.monitor.refresh()
        self.antigravity_desktop_accounts.assert_not_called()
        self.now[0] = 1301
        self.cli_read.side_effect = providers.ReadError('schema_changed')
        self.monitor.refresh()
        rows = {r['id']: r for r in self.monitor.snapshot()['accounts']}
        self.assertEqual(set(rows), {self.desktop['id']})
        self.assertEqual(self.monitor.rows[self.cli['id']]['error'], 'schema_changed')
        self.assertEqual(rows[self.desktop['id']]['status'], 'live')
        self.assertEqual(self.monitor.rows[self.cli['id']]['lastSuccess'], 1000)
        self.assertEqual(rows[self.desktop['id']]['lastSuccess'], 1301)
        self.now[0] = 1362
        self.monitor.refresh()
        self.assertEqual(self.cli_read.call_count, 2)
        self.desktop_read.assert_called_once()
        self.now[0] = 1602
        self.cli_read.side_effect = None
        self.monitor.refresh()
        self.assertEqual([r['id'] for r in self.monitor.snapshot()['accounts']], [self.cli['id']])
        self.assertIn(self.desktop['id'], self.monitor.rows)

    def test_stale_cached_desktop_never_duplicates_failed_cli(self):
        self.monitor.refresh_one(self.desktop)
        self.now[0] = 2000
        self.cli_read.side_effect = providers.ReadError('schema_changed')
        self.antigravity_desktop_accounts.return_value = []
        self.monitor.refresh()
        self.assertEqual([r['id'] for r in self.monitor.snapshot()['accounts']], [self.cli['id']])
        self.assertIn(self.desktop['id'], self.monitor.rows)

    def test_closed_desktop_does_not_replace_cli_with_open_app_guidance(self):
        self.monitor.refresh_one(self.desktop)
        self.now[0] = 1061
        self.cli_read.side_effect = providers.ReadError('schema_changed')
        self.antigravity_desktop_accounts.return_value = []
        self.monitor.refresh()
        rows = self.monitor.snapshot()['accounts']
        self.assertEqual([r['id'] for r in rows], [self.cli['id']])
        self.assertEqual(rows[0]['error'], 'schema_changed')

    def test_cached_fallback_is_not_shown_during_cli_cooldown(self):
        self.monitor.refresh_one(self.desktop)
        self.cli_read.side_effect = providers.ReadError('rate_limited', 7200)
        self.monitor.refresh()
        self.assertEqual([r['id'] for r in self.monitor.snapshot()['accounts']], [self.cli['id']])
        self.antigravity_desktop_accounts.assert_not_called()

    def test_hidden_fresh_desktop_does_not_hide_cli(self):
        self.monitor.refresh_one(self.desktop)
        self.monitor.hidden.add(self.desktop['id'])
        self.cli_read.side_effect = providers.ReadError('schema_changed')
        self.monitor.refresh()
        self.assertEqual([r['id'] for r in self.monitor.snapshot()['accounts']], [self.cli['id']])

    def test_desktop_only_setup_remains_visible(self):
        self.antigravity_accounts.return_value = [self.desktop]
        self.monitor.refresh()
        self.assertEqual([r['id'] for r in self.monitor.snapshot()['accounts']], [self.desktop['id']])

    def test_desktop_failure_keeps_cli_error_and_retry_schedule(self):
        self.cli_read.side_effect = providers.ReadError('antigravity_cli_timeout')
        self.antigravity_desktop_accounts.side_effect = providers.ReadError('local_discovery_failed')
        self.monitor.refresh()
        row = self.monitor.snapshot()['accounts'][0]
        self.assertEqual(row['error'], 'antigravity_cli_timeout')
        self.assertEqual(row['nextAttempt'], 1300)

    def test_cooldowns_never_trigger_desktop_quota_reads(self):
        for code, delay in [('rate_limited', 0), ('rate_limited', 7200), ('provider_http_503', 7200)]:
            with self.subTest(code=code, delay=delay):
                self.monitor.rows.clear()
                self.monitor.next_discovery = 0
                self.cli_read.side_effect = providers.ReadError(code, delay)
                self.monitor.refresh()
        self.antigravity_desktop_accounts.assert_not_called()
        self.desktop_read.assert_not_called()

    def test_hidden_cli_does_not_trigger_fallback(self):
        self.monitor.hidden.add(self.cli['id'])
        self.monitor.refresh()
        self.cli_read.assert_not_called()
        self.antigravity_desktop_accounts.assert_not_called()

    def test_hidden_desktop_is_not_read_on_cli_failure(self):
        self.monitor.hidden.add(self.desktop['id'])
        self.cli_read.side_effect = providers.ReadError('schema_changed')
        self.monitor.refresh()
        self.desktop_read.assert_not_called()
        self.assertEqual([r['id'] for r in self.monitor.snapshot()['accounts']], [self.cli['id']])

    def test_cli_discovery_failure_does_not_hide_working_desktop(self):
        self.monitor.refresh()
        self.now[0] = 1061
        self.antigravity_accounts.return_value = [self.desktop]
        self.monitor.refresh()
        self.assertIn(self.desktop['id'], [r['id'] for r in self.monitor.snapshot()['accounts']])

    def test_hiding_the_cli_card_does_not_revive_the_legacy_desktop_card(self):
        monitor = Monitor(Store(), clock=lambda: 1000)
        legacy = providers.account('antigravity', 'legacy', 'same@example.com',
                                   'Official running Antigravity local service',
                                   lambda: (cli.parse_usage(USAGE), 'same@example.com'))
        monitor.refresh_one(legacy)
        monitor.rows[legacy['id']]['status'] = 'stale'
        account = self.cli
        monitor.refresh_one(account)
        self.assertTrue(monitor.remove_account(account['id']))
        self.assertEqual(monitor.snapshot()['accounts'], [])
        monitor.restore_accounts()
        self.assertEqual([r['id'] for r in monitor.snapshot()['accounts']], [account['id']])
        self.assertIn(legacy['id'], monitor.rows)
