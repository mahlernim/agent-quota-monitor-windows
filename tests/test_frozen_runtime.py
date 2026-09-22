import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

from quota import copilot
from quota.connections import client_command


class FrozenRuntimeTests(unittest.TestCase):
    def test_copilot_command_uses_bundled_bridge_and_external_runtime(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bridge = root / 'bundle' / 'quota' / 'copilot_bridge.mjs'
            bridge_data = root / 'bundle' / 'quota' / 'copilot_bridge_data.mjs'
            sdk = root / 'local' / 'QuotaDashboard/copilot-runtime/node_modules/@github/copilot-sdk/dist/index.js'
            bridge.parent.mkdir(parents=True)
            sdk.parent.mkdir(parents=True)
            bridge.write_text('// bridge', encoding='utf-8')
            bridge_data.write_text('// bridge data', encoding='utf-8')
            sdk.write_text('// sdk', encoding='utf-8')
            found = {'node.exe': 'C:/tools/node.exe', 'gh.exe': 'C:/tools/gh.exe', 'copilot.exe': 'C:/tools/copilot.exe'}
            with patch.object(sys, '_MEIPASS', str(root / 'bundle'), create=True), \
                 patch.dict(os.environ, {'LOCALAPPDATA': str(root / 'local')}, clear=False), \
                 patch.object(copilot.shutil, 'which', side_effect=lambda name: found.get(name)):
                command = copilot.command()
                bridge_data.unlink()
                with self.assertRaises(copilot.ReadError):
                    copilot.command()
            self.assertEqual(command[1], str(bridge))
            self.assertEqual(command[0], found['node.exe'])
            self.assertEqual(command[2:], [found['copilot.exe'], found['gh.exe']])

    def test_missing_windows_profile_paths_do_not_search_working_directory(self):
        with patch.dict(os.environ, {}, clear=True), patch('quota.connections.Path.home', return_value=Path('Z:/missing')), \
             patch('quota.connections.shutil.which', return_value=None):
            self.assertIsNone(client_command('codex'))
            self.assertIsNone(client_command('antigravity'))
            self.assertIsNone(client_command('claude'))


if __name__ == '__main__':
    unittest.main()
