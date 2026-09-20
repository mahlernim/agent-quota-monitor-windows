import unittest
from unittest.mock import patch
from test_removal import Store
from quota.monitor import Monitor


class ProviderSelectionTests(unittest.TestCase):
    def test_disabled_provider_is_not_read_and_preferences_preserved(self):
        store = Store({'desktopOpacity': 70})
        monitor = Monitor(Store(), settings_vault=store)
        monitor.set_providers(['claude'])
        with patch('quota.monitor.providers.codex_account') as codex, patch('quota.monitor.providers.claude_account', side_effect=RuntimeError), patch('quota.monitor.providers.antigravity_accounts') as google, patch('quota.monitor.copilot_account') as copilot:
            monitor.refresh()
        codex.assert_not_called()
        google.assert_not_called()
        copilot.assert_not_called()
        self.assertEqual([a['provider'] for a in monitor.snapshot()['accounts']], ['claude'])
        self.assertEqual(store.data['desktopOpacity'], 70)
        restarted = Monitor(Store(), settings_vault=store)
        self.assertEqual(restarted.snapshot()['enabledProviders'], ['claude'])

    def test_unconfigured_install_has_no_unwanted_pending_cards(self):
        monitor = Monitor(Store())
        with patch('quota.monitor.providers.codex_account', side_effect=RuntimeError), patch('quota.monitor.providers.claude_account', side_effect=RuntimeError), patch('quota.monitor.providers.antigravity_accounts', return_value=[]), patch('quota.monitor.copilot_account', side_effect=RuntimeError):
            monitor.refresh()
        self.assertEqual(monitor.snapshot()['accounts'], [])

    def test_validation_and_empty_selection(self):
        monitor = Monitor(Store())
        for value in (None, 'codex', [None], ['unknown']):
            with self.assertRaises(ValueError): monitor.set_providers(value)
        monitor.set_providers([])
        with patch('quota.monitor.providers.codex_account') as codex:
            monitor.refresh()
        codex.assert_not_called()
        self.assertEqual(monitor.snapshot()['enabledProviders'], [])
