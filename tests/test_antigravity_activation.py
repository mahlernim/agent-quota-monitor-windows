import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from quota import antigravity_activation as isolated
from quota.activation_errors import ActivationFailure
from quota.activation_runners import AntigravityRunner, NotLaunched
from tests import test_activation as support


def report(name, data):
    return 0, json.dumps(dict(status='SUCCESS', num_turns=0, usage={'input_tokens': 0},
                              command=dict(name=name, data=data)))


class IsolationTests(unittest.TestCase):
    def test_profile_failure_before_launch_releases_with_correct_reason(self):
        with patch.object(isolated, 'validate', side_effect=OSError('private-path')), patch('quota.activation_runners.claude_cli.run') as run:
            with self.assertRaises(NotLaunched) as raised:
                AntigravityRunner().send(('agy.exe', 'model', 'revision', 'workspace', 'home'))
            self.assertEqual(raised.exception.code, 'profile')
            run.assert_not_called()

    def test_linked_profile_is_rejected_before_writing(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, LOCALAPPDATA=directory), patch.object(Path, 'is_junction', return_value=True):
            with self.assertRaises(ActivationFailure):
                isolated.profile()
            self.assertEqual(list(Path(directory).iterdir()), [])

    def test_profile_is_separate_and_parent_environment_unchanged(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, LOCALAPPDATA=directory, AGY_ADC_AUTH='true', GOOGLE_API_KEY='secret'):
            before = dict(os.environ)
            home, cwd = isolated.profile()
            self.assertNotEqual(home, os.environ['USERPROFILE'])
            env = isolated.child_environment(home)
            self.assertEqual(env['USERPROFILE'], home)
            self.assertNotIn('AGY_ADC_AUTH', env)
            self.assertNotIn('GOOGLE_API_KEY', env)
            self.assertEqual(dict(os.environ), before)
            self.assertEqual((Path(cwd)/'.agents/agents/aqm-activation.md').read_text(), isolated.AGENT)
            # CLI normalizes an explicit false to its default and creates an empty MCP file.
            (Path(home)/'.gemini/antigravity-cli/settings.json').write_text('{}')
            (Path(home)/'.gemini/config/mcp_config.json').write_text('')
            isolated.validate(home, cwd)
            marker = Path(cwd)/'personal.txt'
            marker.write_text('retain')
            with self.assertRaisesRegex(ActivationFailure, 'workspace'):
                isolated.validate(home, cwd)
            self.assertEqual(marker.read_text(), 'retain')

    def test_modified_agent_billing_and_hooks_fail_closed(self):
        for rel, text in [
            ('workspace/.agents/agents/aqm-activation.md', 'tools: [run_command]'),
            ('home/.gemini/config/config.json', '{"userSettings":{"useAiCredits":true}}'),
            ('home/.gemini/config/hooks.json', '{}'),
        ]:
            with self.subTest(rel=rel), tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, LOCALAPPDATA=directory):
                home, cwd = isolated.profile()
                path = Path(home).parent/rel
                path.write_text(text)
                with self.assertRaises(ActivationFailure):
                    isolated.profile()
                self.assertEqual(path.read_text(), text)

    def test_live_preflight_contract_and_each_guard(self):
        good = [(0, '1.4.0'), (0, 'No plugins installed.'), (0, 'No MCP servers configured.'),
                report('hooks', {'hooks': []}), report('config', {'config': {'useG1Credits': False}}),
                report('agents', {'agents': [isolated.AGENT_NAME]})]
        with patch.object(isolated, 'validate'), patch.object(isolated.claude_cli, 'run', side_effect=good) as run:
            isolated.preflight('agy.exe', 'isolated-home', 'workspace')
            self.assertTrue(all(c.kwargs['env']['USERPROFILE']=='isolated-home' for c in run.call_args_list))
        failures = [(0, (0, '1.3.5'), 'version'), (1, (0, 'Installed plugins: synthetic'), 'plugins'),
                    (2, (0, 'MCP present'), 'mcp'), (3, report('hooks', {'hooks': ['synthetic']}), 'hooks'),
                    (4, report('config', {'config': {'useG1Credits': True}}), 'billing'),
                    (5, report('agents', {'agents': []}), 'agent'), (3, (0, '{}'), 'probe')]
        for index, replacement, code in failures:
            replies = list(good); replies[index] = replacement
            with self.subTest(code=code), patch.object(isolated.claude_cli, 'run', side_effect=replies), patch.object(isolated, 'validate'):
                with self.assertRaises(ActivationFailure) as raised:
                    isolated.preflight('agy.exe', 'isolated-home', 'workspace')
                self.assertEqual(raised.exception.code, code)


class DiagnosticsTests(unittest.TestCase):
    setUp = support.ActivationTests.setUp
    new_engine = support.ActivationTests.new_engine
    tick = support.ActivationTests.tick
    enable = support.ActivationTests.enable
    mature = support.ActivationTests.mature
    status = support.ActivationTests.status
    attempts = support.ActivationTests.attempts
    def test_preflight_failure_is_specific_without_exception_text_or_reservation(self):
        self.enable()
        with patch.object(self.runner, 'prepare', side_effect=ActivationFailure('hooks')):
            self.mature(); self.tick(10)
        self.assertIn('still loads hooks', self.status())
        self.assertNotIn('identity', self.status())
        self.assertEqual(self.runner.sends, 0)
        self.assertEqual(self.attempts(), {})
        self.tick(290)
        self.assertEqual(self.runner.sends, 1)

    def test_unknown_preflight_error_is_not_exposed(self):
        self.enable()
        with patch.object(self.runner, 'prepare', side_effect=ValueError('secret-provider-output')):
            self.mature(); self.tick(10)
        self.assertEqual(self.status(), 'Activation preflight failed. No prompt sent.')

    def test_delivery_failure_survives_restart_and_never_retries(self):
        self.enable()
        with patch.object(self.runner, 'send', side_effect=ActivationFailure('tools')) as send:
            self.mature()
            self.engine = self.new_engine()
            self.tick()
            self.assertIn('reported tool use', self.status())
            self.assertIn('No automatic retry', self.status())
            send.assert_called_once()


if __name__ == '__main__': unittest.main()
