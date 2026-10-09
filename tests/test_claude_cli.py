import copy
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

from quota import claude_cli as cli, model, providers
from quota.monitor import Monitor


def events(limits=None):
    return [dict(type='assistant', usage_report=dict(rate_limits=dict(limits=limits if limits is not None else [
        dict(kind='session', percent=0, resets_at=None, is_active=False),
        dict(kind='weekly_all', percent=24, resets_at='2026-10-01T19:59:59+00:00'),
    ]))), dict(type='result', subtype='success', is_error=False, num_turns=0, total_cost_usd=0,
              usage=dict(input_tokens=0, output_tokens=0, cache_creation_input_tokens=0, cache_read_input_tokens=0))]


def stream(rows):
    return '\n'.join(json.dumps(row) for row in rows)


META = dict(accountUuid='account-1', organizationUuid='org-1', emailAddress='same@example.test')
STATUS = dict(loggedIn=True, authMethod='claude.ai', apiProvider='firstParty',
              orgId='org-1', email='same@example.test')


class ParseTests(unittest.TestCase):
    def test_real_shape_preserves_existing_ring_ids_and_inactive_reset(self):
        groups = cli.parse_usage(stream(events()))
        self.assertEqual(groups[0]['id'], 'direct')
        a, b = groups[0]['buckets']
        self.assertEqual((a['id'], a['remaining'], a['resetsAt']), ('five_hour', 100, None))
        self.assertEqual((b['id'], b['remaining'], b['windowSeconds']), ('seven_day', 76, 604800))

    def test_missing_windows_are_not_created(self):
        groups = cli.parse_usage(stream(events([dict(kind='weekly_all', percent=100)])))
        self.assertEqual([b['id'] for b in groups[0]['buckets']], ['seven_day'])

    def test_cost_only_output_is_unavailable(self):
        rows = events()
        rows[0]['usage_report']['rate_limits'] = None
        with self.assertRaises(providers.ReadError) as caught:
            cli.parse_usage(stream(rows))
        self.assertEqual(caught.exception.code, 'quota_not_reported')

    def test_malformed_duplicate_and_model_output_are_rejected(self):
        cases = []
        for value in (True, '12', -1, 101, float('nan')):
            row = events(); row[0]['usage_report']['rate_limits']['limits'][0]['percent'] = value
            cases.append(row)
        row = events(); row[0]['usage_report']['rate_limits']['limits'] *= 2; cases.append(row)
        row = events(); row[0]['usage_report']['rate_limits']['limits'][0]['resets_at'] = 'tomorrow'; cases.append(row)
        row = events(); row[-1]['num_turns'] = 1; cases.append(row)
        row = events(); row[-1]['usage']['input_tokens'] = 1; cases.append(row)
        cases.extend([events()[:-1], events() + [events()[-1]], [None]])
        for row in cases:
            with self.subTest(row=row), self.assertRaises(providers.ReadError):
                cli.parse_usage(stream(row))

    def test_unknown_limits_do_not_become_full_quota(self):
        with self.assertRaises(providers.ReadError) as caught:
            cli.parse_usage(stream(events([dict(kind='future', percent=0)])))
        self.assertEqual(caught.exception.code, 'quota_not_reported')


class AccountTests(unittest.TestCase):
    def setUp(self):
        self.environment = patch.dict(os.environ, {}, clear=True)
        self.environment.start()
        self.addCleanup(self.environment.stop)
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.home = Path(self.temp.name)
        self.save(META)

    def save(self, meta):
        (self.home / '.claude.json').write_text(json.dumps(dict(oauthAccount=meta)), encoding='utf-8')

    def test_reads_without_credential_contents_and_keeps_identity(self):
        with patch.object(cli, 'auth_status', return_value=STATUS), \
                patch.object(cli, 'usage', return_value=cli.parse_usage(stream(events()))):
            account = cli.cli_account('claude.exe', self.home)
            self.assertEqual(account['id'], model.identity('claude', META['accountUuid']))
            self.assertNotIn('sessionExpiresAt', account)
            self.assertEqual(account['read']()[1], META['emailAddress'])

    def test_account_change_during_read_is_rejected_even_with_same_email(self):
        def usage(_):
            self.save({**META, 'accountUuid': 'other-account'})
            return cli.parse_usage(stream(events()))
        with patch.object(cli, 'auth_status', return_value=STATUS), patch.object(cli, 'usage', side_effect=usage):
            account = cli.cli_account('claude.exe', self.home)
            with self.assertRaises(providers.ReadError) as caught:
                account['read']()
            self.assertEqual(caught.exception.code, 'identity_changed')

    def test_cli_organization_mismatch_is_rejected_before_usage(self):
        with patch.object(cli, 'auth_status', return_value={**STATUS, 'orgId': 'other'}), patch.object(cli, 'usage') as usage:
            account = cli.cli_account('claude.exe', self.home)
            with self.assertRaises(providers.ReadError) as caught:
                account['read']()
            self.assertEqual(caught.exception.code, 'identity_mismatch')
            usage.assert_not_called()

    def test_credential_rotation_by_cli_does_not_change_account(self):
        path = self.home / '.claude/.credentials.json'
        path.parent.mkdir()
        path.write_text('not parsed by the monitor')
        def usage(_):
            path.write_text('CLI renewed its own session')
            return cli.parse_usage(stream(events()))
        with patch.object(cli, 'auth_status', return_value=STATUS), patch.object(cli, 'usage', side_effect=usage):
            account = cli.cli_account('claude.exe', self.home)
            self.assertEqual(account['read']()[0][0]['buckets'][0]['remaining'], 100)

    def test_inherited_overrides_do_not_block_default_account(self):
        for name in ('ANTHROPIC_API_KEY', 'CLAUDE_CONFIG_DIR', 'CLAUDE_CODE_OAUTH_TOKEN'):
            with patch.dict(os.environ, {name: 'synthetic-secret'}), \
                    patch.object(cli, 'auth_status', return_value=STATUS) as auth, \
                    patch.object(cli, 'usage', return_value=cli.parse_usage(stream(events()))):
                account = cli.cli_account('claude.exe', self.home)
                self.assertEqual(account['read']()[1], META['emailAddress'])
                self.assertEqual(auth.call_count, 2)
                self.assertEqual(os.environ[name], 'synthetic-secret')

    def test_failure_keeps_stale_values_and_backoff(self):
        class Vault:
            def load(self): return {}
        now = [1000]
        monitor = Monitor(Vault(), clock=lambda: now[0])
        with patch.object(cli, 'auth_status', return_value=STATUS), patch.object(cli, 'usage', return_value=cli.parse_usage(stream(events()))) as usage:
            account = cli.cli_account('claude.exe', self.home)
            monitor.refresh_one(account)
            before = copy.deepcopy(monitor.rows[account['id']])
            now[0] += 300
            usage.side_effect = providers.ReadError('claude_cli_timeout')
            monitor.refresh_one(account)
            row = monitor.rows[account['id']]
            self.assertEqual((row['status'], row['lastSuccess'], row['groups']), ('stale', before['lastSuccess'], before['groups']))
            monitor.refresh_one(account)
            self.assertEqual(usage.call_count, 2)


class RoutingTests(unittest.TestCase):
    def test_failed_version_probe_falls_back_then_retries_after_cooldown(self):
        for response in ((1, ''), (0, 'unrecognized version'), providers.ReadError('claude_cli_timeout')):
            with self.subTest(response=response), tempfile.TemporaryDirectory() as directory:
                exe = Path(directory) / 'claude.exe'; exe.write_text('binary')
                now = [1000]
                with patch('quota.connections.claude_candidates', return_value=[dict(executable=str(exe), updateMethod='native')]), \
                        patch.object(cli, 'run', side_effect=[response, (0, '2.1.281 (Claude Code)')]) as run, \
                        patch('time.monotonic', side_effect=lambda: now[0]), \
                        patch.object(providers, 'claude_legacy_account', return_value={'source': 'legacy'}):
                    cli._versions.clear()
                    self.assertEqual(providers.claude_account()['source'], 'legacy')
                    now[0] += 299
                    self.assertEqual(providers.claude_account()['source'], 'legacy')
                    self.assertEqual(run.call_count, 1)
                    now[0] += 1
                    self.assertEqual(cli.supported_command(), str(exe))
                    self.assertEqual(run.call_count, 2)

    def test_upgrade_bypasses_failed_probe_cooldown(self):
        with tempfile.TemporaryDirectory() as directory:
            exe = Path(directory) / 'claude.exe'; exe.write_text('old')
            with patch('quota.connections.claude_candidates', return_value=[dict(executable=str(exe), updateMethod='native')]), \
                    patch.object(cli, 'run', side_effect=[(1, ''), (0, '2.1.281 (Claude Code)')]) as run:
                cli._versions.clear()
                self.assertIsNone(cli.supported_command())
                exe.write_text('updated binary')
                self.assertEqual(cli.supported_command(), str(exe))
                self.assertEqual(run.call_count, 2)

    def test_missing_executable_falls_back_without_probe(self):
        with tempfile.TemporaryDirectory() as directory:
            exe = Path(directory) / 'missing.exe'
            with patch('quota.connections.claude_candidates', return_value=[dict(executable=str(exe), updateMethod='native')]), \
                    patch.object(cli, 'run') as run, \
                    patch.object(providers, 'claude_legacy_account', return_value={'source': 'legacy'}):
                self.assertEqual(providers.claude_account()['source'], 'legacy')
                run.assert_not_called()

    def test_cli_errors_never_fall_back_to_direct_http(self):
        info = dict(executable='claude.exe', clientVersion='2.1.281', clientState='supported',
                    clientUpdateMethod='native', clientMinimumVersion='2.1.281')
        with patch.object(cli, 'client_info', return_value=info), \
                patch.object(cli, 'cli_account', side_effect=providers.ReadError('claude_cli_failed')), \
                patch.object(providers, 'claude_legacy_account') as old:
            with self.assertRaises(providers.ReadError): providers.claude_account()
            old.assert_not_called()

    def test_older_client_uses_existing_reader(self):
        info = dict(executable='claude.exe', clientVersion='2.1.280', clientState='outdated',
                    clientUpdateMethod='npm', clientMinimumVersion='2.1.281')
        with patch.object(cli, 'client_info', return_value=info), patch.object(providers, 'claude_legacy_account', return_value={'source': 'legacy'}):
            row = providers.claude_account()
            self.assertEqual(row['source'], 'legacy')
            self.assertEqual(row['clientState'], 'outdated')
            self.assertNotIn('executable', row)

    def test_version_cache_is_invalidated_on_upgrade(self):
        with tempfile.TemporaryDirectory() as directory:
            exe = Path(directory) / 'claude.exe'; exe.write_text('old')
            with patch('quota.connections.claude_candidates', return_value=[dict(executable=str(exe), updateMethod='native')]), \
                    patch.object(cli, 'run', return_value=(0, '2.1.280 (Claude Code)')) as run:
                cli._versions.clear()
                self.assertIsNone(cli.supported_command())
                exe.write_text('updated binary')
                run.return_value = (0, '2.1.281 (Claude Code)')
                self.assertEqual(cli.supported_command(), str(exe))
                self.assertEqual(cli.supported_command(), str(exe))
                self.assertEqual(run.call_count, 2)

    def test_status_requires_subscription_and_only_false_login_means_sign_in(self):
        for data, error in (({**STATUS, 'loggedIn': False}, 'sign_in_required'),
                            ({**STATUS, 'authMethod': 'api_key'}, 'claude_cli_auth_unsupported')):
            with patch.object(cli, 'run', return_value=(0, json.dumps(data))), self.assertRaises(providers.ReadError) as caught:
                cli.auth_status('claude.exe')
            self.assertEqual(caught.exception.code, error)


class ProcessTests(unittest.TestCase):
    def test_child_ignores_overrides_without_changing_parent_environment(self):
        names = ('CLAUDE_CONFIG_DIR', 'CLAUDE_CODE_OAUTH_TOKEN', 'CLAUDE_CODE_OAUTH_TOKEN_FILE',
                 'ANTHROPIC_API_KEY', 'ANTHROPIC_AUTH_TOKEN', 'ANTHROPIC_BASE_URL',
                 'ANTHROPIC_PROFILE', 'CLAUDE_CODE_USE_BEDROCK', 'CLAUDE_CODE_USE_VERTEX',
                 'CLAUDE_CODE_USE_FOUNDRY', 'CLAUDE_CODE_API_KEY_FILE_DESCRIPTOR')
        overrides = dict.fromkeys(names, 'synthetic-override')
        overrides['AQM_CHILD_TEST'] = 'preserved'
        with patch.dict(os.environ, overrides):
            script = ('import os; '
                      f'assert not any(name in os.environ for name in {names!r}); '
                      'assert os.environ["AQM_CHILD_TEST"] == "preserved"; print("default subscription environment")')
            code, text = cli.run(sys.executable, ['-c', script], timeout=5)
            self.assertEqual((code, text.strip()), (0, 'default subscription environment'))
            self.assertEqual({k: os.environ[k] for k in overrides}, overrides)

    def test_runtime_and_output_are_bounded_without_exposing_stdout(self):
        with patch.object(cli, 'environment', return_value=os.environ.copy()):
            for script, timeout, expected in (
                    ('import time; print("synthetic-secret",flush=True); time.sleep(30)', .2, 'claude_cli_timeout'),
                    ('print("x"*2000)', 5, 'response_too_large')):
                with patch.object(cli, 'MAX_OUTPUT', 1024), self.assertRaises(providers.ReadError) as caught:
                    cli.run(sys.executable, ['-c', script], timeout=timeout)
                self.assertEqual(str(caught.exception), expected)

    def test_command_is_local_usage_with_customizations_disabled(self):
        with patch.object(cli, 'run', return_value=(0, stream(events()))) as run:
            cli.usage('claude.exe')
        args = run.call_args.args[1]
        self.assertEqual(args[-2:], ['-p', '/usage'])
        for flag in ('--safe-mode', '--strict-mcp-config', '--no-session-persistence'):
            self.assertIn(flag, args)
        self.assertEqual(args[args.index('--tools') + 1], '')


if __name__ == '__main__':
    unittest.main()
