import copy
import threading
import tempfile
from pathlib import Path
from types import SimpleNamespace
import unittest
import urllib.error
from quota.cli_updates import CliUpdates, DAY, version, latest, discover
from unittest.mock import patch


class Store:
    def __init__(self): self.data = {}
    def load(self): return copy.deepcopy(self.data)
    def save(self, value): self.data = copy.deepcopy(value)


class Monitor:
    def __init__(self):
        self.lock = threading.RLock()
        self.enabled = {'codex'}
        self.rows = {}

    def snapshot(self):
        return dict(enabledProviders=[r['provider'] for r in self.rows.values()])


class CliUpdateTests(unittest.TestCase):
    def setUp(self):
        self.monitor, self.store = Monitor(), Store()
        self.now = 1000000
        self.calls, self.probes = [], []
        self.installed, self.identity = '1.2.3', 'synthetic'
        self.service = self.create()

    def create(self):
        def resolve(provider):
            self.probes.append(provider)
            return dict(state='unchecked', installed=self.installed, method='npm', channel='latest', identity=self.identity)
        def fetch(provider, channel):
            self.calls.append((provider, channel))
            return '1.3.0'
        return CliUpdates(self.monitor, self.store, resolve, fetch, lambda: self.now)

    def row(self): return self.service.snapshot()['tools'][0]

    def test_local_detection_uses_only_version_and_scoped_environment(self):
        with tempfile.TemporaryDirectory() as directory:
            executable = Path(directory) / 'Programs/OpenAI/Codex/bin/codex.exe'
            executable.parent.mkdir(parents=True)
            executable.write_bytes(b'MZ' + bytes(58) + (64).to_bytes(4, 'little') + b'PE\0\0' + bytes(20))
            with patch.dict('os.environ', {'LOCALAPPDATA': directory}), \
                    patch('quota.connections.client_command', return_value=[str(executable), 'login']), \
                    patch('quota.cli_updates.subprocess.run', return_value=SimpleNamespace(returncode=0, stdout=b'codex-cli 1.2.3')) as run:
                row = discover('codex')
                self.assertEqual((row['method'], row['installed']), ('native', '1.2.3'))
                self.assertEqual(run.call_args.args[0], [str(executable.resolve()), '--version'])
                self.assertEqual(run.call_args.kwargs['env']['DISABLE_AUTOUPDATER'], '1')
                run.return_value.stdout = b'codex-cli 1.2.3-beta.1'
                with self.assertRaises(ValueError): discover('codex')

    def test_codex_update_prefers_npm_cli_over_desktop_path(self):
        with tempfile.TemporaryDirectory() as directory:
            executable = Path(directory) / 'npm/node_modules/@openai/codex/node_modules/@openai/codex-win32-x64/vendor/bin/codex.exe'
            executable.parent.mkdir(parents=True)
            executable.write_bytes(b'MZ' + bytes(58) + (64).to_bytes(4, 'little') + b'PE\0\0' + bytes(20))
            with patch.dict('os.environ', {'APPDATA': directory, 'LOCALAPPDATA': directory}), \
                    patch('quota.connections.client_command', return_value=['desktop/codex.exe', 'login']) as fallback, \
                    patch('quota.cli_updates.subprocess.run', return_value=SimpleNamespace(returncode=0, stdout=b'codex-cli 0.153.4')) as run:
                row = discover('codex')
                self.assertEqual(row['method'], 'npm')
                self.assertEqual(row['installed'], '0.153.4')
                self.assertEqual(run.call_args.args[0], [str(executable.resolve()), '--version'])
                fallback.assert_not_called()

    def test_update_probe_rejects_placeholder_without_launch(self):
        with tempfile.TemporaryDirectory() as directory:
            executable = Path(directory) / 'Programs/OpenAI/Codex/bin/codex.exe'
            executable.parent.mkdir(parents=True)
            executable.write_bytes(b'echo "native binary not installed"')
            with patch.dict('os.environ', {'LOCALAPPDATA': directory}), \
                    patch('quota.cli_updates.os.name', 'nt'), \
                    patch('quota.cli_updates.subprocess.run') as run:
                with self.assertRaisesRegex(ValueError, 'incomplete'):
                    discover('codex')
                run.assert_not_called()

    def test_daily_interval_survives_restart(self):
        self.service.check()
        self.assertEqual(self.row()['state'], 'available')
        self.now += DAY - 1
        self.service = self.create()
        self.service.check()
        self.assertEqual(len(self.calls), 1)
        self.now += 1
        self.service.check()
        self.assertEqual(len(self.calls), 2)

    def test_manual_bypasses_daily_but_not_minute(self):
        self.service.check()
        self.now += 59
        self.service.check(True)
        self.assertEqual(len(self.calls), 1)
        self.now += 1
        self.service.check(True)
        self.assertEqual(len(self.calls), 2)
        self.assertEqual(self.store.data['codex']['nextCheck'], self.now + DAY)

    def test_disabled_never_probed_or_fetched(self):
        self.monitor.enabled = set()
        self.service.check(True)
        self.assertEqual((self.calls, self.probes), ([], []))
        self.assertEqual(self.service.snapshot()['tools'], [])
        with self.assertRaises(ValueError): self.service.prepare('codex')

    def test_disabled_after_discovery_skips_network(self):
        original = self.service.resolver
        def resolve(p):
            row = original(p)
            self.monitor.enabled = set()
            return row
        self.service.resolver = resolve
        self.service.check()
        self.assertFalse(self.calls)

    def test_disable_during_network_hides_result_and_blocks_update(self):
        def fetch(*args):
            self.monitor.enabled = set()
            return '1.3.0'
        self.service.fetch = fetch
        self.service.check()
        self.assertEqual(self.service.snapshot()['tools'], [])
        with self.assertRaises(ValueError): self.service.prepare('codex')

    def test_failure_is_not_up_to_date_and_is_persisted(self):
        def fail(*args): raise TimeoutError()
        self.service.fetch = fail
        self.service.check()
        self.assertEqual(self.row()['state'], 'failed')
        self.service = self.create()
        self.service.check()
        self.assertEqual(self.row()['state'], 'failed')
        self.assertFalse(self.calls)

    def test_retry_after_blocks_manual_across_restart(self):
        def limited(*args): raise urllib.error.HTTPError('synthetic', 429, '', {'Retry-After': '172800'}, None)
        self.service.fetch = limited
        self.service.check()
        self.now += DAY
        self.service = self.create()
        self.service.check(True)
        self.assertFalse(self.calls)
        self.now += DAY
        self.service.check(True)
        self.assertEqual(len(self.calls), 1)

    def test_registry_cooldown_covers_other_enabled_packages(self):
        self.monitor.enabled = {'codex', 'claude', 'copilot'}
        calls = []
        def limited(provider, channel):
            calls.append(provider)
            raise urllib.error.HTTPError('synthetic', 429, '', {'Retry-After': '3600'}, None)
        self.service.fetch = limited
        self.service.check()
        self.assertEqual(calls, ['codex'])
        self.now += 60
        self.service = self.create()
        self.service.check(True)
        self.assertFalse(self.calls)

    def test_unknown_install_identity_never_inherits_offer(self):
        self.service.check()
        self.identity = 'replaced'
        with self.assertRaises(ValueError): self.service.prepare('codex')
        self.service.check()
        self.assertEqual(self.row()['state'], 'unchecked')
        self.assertEqual(len(self.calls), 1)

    def test_versions_numeric_and_no_downgrade(self):
        self.installed = '1.10.0'
        self.service.check()
        self.assertEqual(self.row()['state'], 'current')
        with self.assertRaises(ValueError): self.service.prepare('codex')
        self.assertGreater(version('2.10.0'), version('2.9.9'))
        for value in ('1.2', '1.2.3-beta.1', '1.2.3; evil', None):
            with self.assertRaises(ValueError): version(value)

    def test_storage_failure_prevents_network(self):
        self.store.save = lambda value: (_ for _ in ()).throw(OSError())
        self.service.check()
        self.assertFalse(self.calls)
        self.assertTrue(self.service.snapshot()['storageError'])

    def test_snapshot_does_not_expose_paths_or_identity(self):
        self.service.check()
        self.assertNotIn('identity', self.row())
        self.assertEqual(self.service.prepare('codex'), dict(provider='codex', method='npm', channel='latest'))

    def test_missing_client_not_checked(self):
        self.service.resolver = lambda p: dict(state='missing', identity='missing')
        self.service.check()
        self.assertFalse(self.calls)
        self.assertEqual(self.row()['state'], 'missing')

    def test_inflight_coalesced_and_snapshot_nonblocking(self):
        entered, release = threading.Event(), threading.Event()
        def fetch(*args):
            entered.set()
            release.wait(2)
            return '1.3.0'
        self.service.fetch = fetch
        self.assertTrue(self.service.request())
        self.assertTrue(entered.wait(1))
        try:
            self.assertFalse(self.service.request())
            self.assertTrue(self.service.snapshot()['busy'])
        finally: release.set()

    def test_default_enabled_uses_discovered_providers(self):
        self.monitor.enabled = None
        self.monitor.rows = {'synthetic': {'provider': 'claude'}}
        self.service.check()
        self.assertEqual(self.probes, ['claude'])

    def test_prepare_rejects_expired_offer(self):
        self.service.check()
        self.now += DAY
        with self.assertRaises(ValueError): self.service.prepare('codex')


if __name__ == '__main__': unittest.main()
