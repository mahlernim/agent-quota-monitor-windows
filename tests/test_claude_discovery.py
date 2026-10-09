"""Local Claude discovery and upgrade regressions. No provider or credential reads."""
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch

from quota import claude_cli as cli, connections, model, providers
from quota.monitor import Monitor
from quota.activation import ClaudeRunner


class DiscoveryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        cli._versions.clear()
        self.addCleanup(cli._versions.clear)

    def executable(self, relative):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text('synthetic executable')
        return path

    def candidates(self, *paths):
        return patch.object(connections, 'claude_candidates', return_value=[
            dict(executable=str(path), updateMethod='native') for path in paths])

    def test_old_path_client_does_not_hide_new_native_client(self):
        old = self.executable('roaming/npm/node_modules/@anthropic-ai/claude-code/bin/claude.exe')
        new = self.executable('home/.local/bin/claude.exe')
        def version(executable, args, **kwargs):
            self.assertEqual(args, ['--version'])
            return 0, ('2.1.280' if executable == str(old) else '2.1.295') + ' (Claude Code)'
        with patch('quota.providers.refresh_path'), \
                patch.object(connections, '_environment_path', side_effect=lambda key: self.root / ('roaming' if key == 'APPDATA' else 'local')), \
                patch.object(connections.Path, 'home', return_value=self.root / 'home'), \
                patch.object(connections.shutil, 'which', return_value=str(old)), \
                patch.object(cli, 'run', side_effect=version) as run:
            self.assertEqual(cli.supported_command(), str(new))
            self.assertEqual(connections.client_command('claude')[0], str(new))
            self.assertEqual(run.call_count, 2)

    def test_supported_versions_rank_numerically_and_future_major_is_excluded(self):
        paths = [self.executable(str(i) + '/claude.exe') for i in range(4)]
        outputs = ['2.1.99', '2.1.281', '2.1.1000', '3.0.1']
        with self.candidates(*paths), patch.object(cli, 'run', side_effect=[(0, value + ' (Claude Code)') for value in outputs]) as run:
            self.assertEqual(cli.supported_command(), str(paths[2]))
            self.assertEqual(cli.supported_command(), str(paths[2]))
            self.assertEqual(run.call_count, 4)
            self.assertEqual(len(cli._versions), 4)

    def test_all_failed_probes_are_unverified_and_cached_without_fake_logout(self):
        paths = [self.executable(str(i) + '/claude.exe') for i in range(3)]
        with self.candidates(*paths), patch.object(cli, 'run', side_effect=[
                (1, 'private output'), (0, 'unrecognized private output'), providers.ReadError('claude_cli_timeout')]) as run:
            info = cli.client_info()
            self.assertEqual(info['clientState'], 'unverified')
            self.assertEqual(info['clientVersion'], '')
            self.assertIsNone(cli.supported_command())
            self.assertEqual(run.call_count, 3)
            public = cli.client_fields(info)
            self.assertEqual(set(public), set(cli.CLIENT_FIELDS))
            self.assertNotIn('private', repr(public))
            self.assertNotIn(str(self.root), repr(public))

    def test_probe_budget_defers_unchecked_candidates_until_next_discovery(self):
        paths = [self.executable(str(i) + '/claude.exe') for i in range(3)]
        now = [1000]
        def probe(executable, args, timeout):
            self.assertLessEqual(timeout, 5)
            if executable == str(paths[2]):
                return 0, '2.1.295 (Claude Code)'
            now[0] += timeout
            raise providers.ReadError('claude_cli_timeout')
        with self.candidates(*paths), patch.object(cli.time, 'monotonic', side_effect=lambda: now[0]), \
                patch.object(cli, 'run', side_effect=probe) as run:
            self.assertIsNone(cli.supported_command())
            self.assertEqual(run.call_count, 2)
            self.assertEqual(now[0], 1010)
            self.assertEqual(cli.supported_command(), str(paths[2]))
            self.assertEqual(run.call_count, 3)

    def test_unknown_missing_and_unsupported_clients_have_distinct_states(self):
        executable = self.executable('claude.exe')
        with self.candidates(executable), patch.object(cli, 'run', return_value=(0, '3.0.0 (Claude Code)')):
            self.assertEqual(cli.client_info()['clientState'], 'unsupported')
            self.assertIsNone(cli.supported_command())
        with self.candidates(), patch.object(cli, 'run') as run:
            self.assertEqual(cli.client_info()['clientState'], 'missing')
            run.assert_not_called()

    def test_known_update_methods_and_duplicate_paths(self):
        native = self.executable('home/.local/bin/claude.exe')
        npm = self.executable('roaming/npm/node_modules/@anthropic-ai/claude-code/bin/claude.exe')
        winget = self.executable('local/Microsoft/WinGet/Packages/Anthropic.ClaudeCode_1/claude.exe')
        cache = self.executable('local/npm-cache/_npx/123/node_modules/@anthropic-ai/claude-code-win32-x64/claude.exe')
        with patch('quota.providers.refresh_path'), \
                patch.object(connections, '_environment_path', side_effect=lambda key: self.root / ('roaming' if key == 'APPDATA' else 'local')), \
                patch.object(connections.Path, 'home', return_value=self.root / 'home'), \
                patch.object(connections.shutil, 'which', return_value=str(native)):
            found = connections.claude_candidates()
            self.assertEqual({item['executable']: item['updateMethod'] for item in found},
                             {str(native): 'native', str(npm): 'npm', str(winget): 'winget', str(cache): 'manual'})
            self.assertEqual(len(found), 4)

    def test_candidate_limit_bounds_version_work(self):
        for index in range(30):
            self.executable(f'local/npm-cache/_npx/{index}/node_modules/@anthropic-ai/claude-code-win32-x64/claude.exe')
        with patch('quota.providers.refresh_path'), \
                patch.object(connections, '_environment_path', return_value=self.root / 'local'), \
                patch.object(connections.Path, 'home', return_value=self.root / 'home'), \
                patch.object(connections.shutil, 'which', return_value=None):
            self.assertEqual(len(connections.claude_candidates()), connections.MAX_CLAUDE_CANDIDATES)

    def test_noncanonical_native_path_without_launcher_offers_manual_instructions(self):
        for relative in ('home/.local/bin/other/claude.exe', 'home/.local/share/claude/versions/old/claude.exe'):
            executable = self.executable(relative)
            with self.subTest(relative=relative), patch('quota.providers.refresh_path'), \
                    patch.object(connections, '_environment_path', return_value=None), \
                    patch.object(connections.Path, 'home', return_value=self.root / 'home'), \
                    patch.object(connections.shutil, 'which', return_value=str(executable)):
                self.assertEqual(connections.claude_candidates(), [dict(executable=str(executable), updateMethod='manual')])

    def test_native_launcher_and_its_path_target_share_the_launcher_update_method(self):
        target = self.executable('home/.local/share/claude/versions/new/claude.exe')
        launcher = self.executable('home/.local/bin/claude.exe')
        def resolve(path, *args, **kwargs):
            return target if path == launcher else path
        with patch('quota.providers.refresh_path'), patch.object(connections, '_environment_path', return_value=None), \
                patch.object(connections.Path, 'home', return_value=self.root / 'home'), \
                patch.object(connections.shutil, 'which', return_value=str(target)), \
                patch.object(connections.Path, 'resolve', resolve):
            self.assertEqual(connections.claude_candidates(), [dict(executable=str(target), updateMethod='native')])

    def test_upgrade_retries_expired_legacy_row_with_unchanged_credentials(self):
        class Vault:
            def load(self): return {}
        monitor = Monitor(Vault(), clock=lambda: 1000)
        read = Mock(return_value=(model.claude({'five_hour': {'utilization': 0, 'resets_at': None}}), 'Synthetic'))
        current = providers.account('claude', 'account', 'Synthetic', cli.SOURCE, read)
        current.update(clientState='supported', clientVersion='2.1.295', sessionRevision=123)
        old = {k: v for k, v in current.items() if k != 'read'}
        old.update(source='Official Claude Code session / OAuth usage endpoint', clientState='outdated',
                   clientVersion='2.1.280', error='session_expired', status='stale', nextAttempt=1200,
                   lastSuccess=900, groups=[], failures=3)
        monitor.rows[current['id']] = old
        monitor.refresh_one(current)
        self.assertEqual(read.call_count, 1)
        self.assertEqual(monitor.rows[current['id']]['source'], cli.SOURCE)
        self.assertEqual(monitor.rows[current['id']]['status'], 'live')

    def test_upgrade_preserves_provider_cooldown_suspension_and_other_errors(self):
        class Vault:
            def load(self): return {}
        for protection in ({'providerCooldown': True}, {'retryState': 'suspended'},
                           {'error': 'rate_limited'}, {'error': 'claude_cli_failed'}):
            with self.subTest(protection=protection):
                monitor = Monitor(Vault(), clock=lambda: 1000)
                read = Mock()
                current = providers.account('claude', 'account', 'Synthetic', cli.SOURCE, read)
                current.update(clientState='supported', clientVersion='2.1.295', sessionRevision=123)
                old = {key: value for key, value in current.items() if key != 'read'}
                old.update(source='Official Claude Code session / OAuth usage endpoint', error='session_expired',
                           status='stale', nextAttempt=1200, lastSuccess=900, groups=[], failures=3)
                old.update(protection)
                monitor.rows[current['id']] = old
                monitor.refresh_one(current)
                read.assert_not_called()
                saved = monitor.rows[current['id']]
                self.assertEqual(saved['clientState'], 'supported')
                self.assertEqual(saved['lastSuccess'], 900)
                self.assertEqual(saved['source'], old['source'])

    def test_discovery_failure_preserves_safe_client_guidance_on_placeholder_and_cached_row(self):
        class Vault:
            def load(self): return {}
            def save(self, rows): pass
        info = dict(executable='private-path', clientVersion='2.1.280', clientState='outdated',
                    clientUpdateMethod='npm', clientMinimumVersion='2.1.281')
        for cached in (False, True):
            monitor = Monitor(Vault(), clock=lambda: 1000)
            monitor.enabled = {'claude'}
            if cached:
                monitor.rows['old-account'] = dict(id='old-account', provider='claude', source='legacy',
                                                  status='stale', lastSuccess=900, groups=[])
            with patch.object(cli, 'client_info', return_value=info), \
                    patch.object(providers, 'claude_legacy_account', side_effect=providers.ReadError('local_session_unavailable')):
                monitor.refresh()
            rows = monitor.snapshot()['accounts']
            self.assertEqual(len(rows), 1)
            self.assertEqual(rows[0]['clientState'], 'outdated')
            self.assertEqual(rows[0]['clientVersion'], '2.1.280')
            self.assertNotIn('private-path', repr(rows))

    def test_activation_resolves_one_client_with_its_unchanged_minimum(self):
        metadata = dict(accountUuid='account', organizationUuid='org', emailAddress='synthetic@example.test')
        with patch.object(cli, 'client_info', return_value=dict(executable='synthetic.exe', clientState='supported')) as resolver, \
                patch.object(providers, 'load', return_value={'oauthAccount': metadata}), \
                patch.object(cli, 'auth_status', return_value=dict(orgId='org', email='synthetic@example.test', subscriptionType='pro')):
            self.assertEqual(ClaudeRunner().prepare({'id': model.identity('claude', 'account')}), 'synthetic.exe')
            resolver.assert_called_once_with(minimum=(2, 1, 280))


if __name__ == '__main__':
    unittest.main()
