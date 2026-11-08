"""Bounded subscription-only invocations. Official clients retain authentication ownership."""
import json
import os
from pathlib import Path
from . import antigravity_cli, claude_cli, connections, model, providers
from .activation_errors import ActivationFailure, error_code

PROMPT = 'Reply only OK. Do not use tools, read files, access URLs, or delegate.'
MODELS = {'gemini': 'gemini-3.8-flash-low', 'claude-gpt': 'gpt-oss-120b-medium',
          'gemini-argon': 'gemini-4-argon-low'}


class NotLaunched(Exception):
    """Raised only before any child process starts, so the reservation can be released."""


def environment():
    denied = ('OPENAI_', 'CODEX_', 'ANTHROPIC_', 'HERDR_',
              'GEMINI_API_', 'GOOGLE_API_', 'GOOGLE_GEMINI_', 'GOOGLE_GENAI_',
              'GOOGLE_APPLICATION_', 'GOOGLE_CLOUD_')
    env = {k: v for k, v in os.environ.items() if k.upper() == 'CODEX_HOME' or not k.upper().startswith(denied)}
    env['AGY_CLI_DISABLE_AUTO_UPDATE'] = 'true'
    return env


def workspace():
    path = Path(os.environ['LOCALAPPDATA']) / 'QuotaDashboard/activation-workspace'
    path.mkdir(parents=True, exist_ok=True)
    if path.is_symlink() or path.is_junction() or any(path.iterdir()):
        raise ValueError('Activation workspace is not empty')
    return str(path)


def counters(result):
    from .activation import number
    return {k: v for k, v in result.get('usage', {}).items()
            if k in ('input_tokens', 'output_tokens', 'cached_input_tokens',
                     'cache_read_input_tokens', 'cache_creation_input_tokens',
                     'inputTokens', 'outputTokens', 'totalTokens') and number(v)}


class CodexRunner:
    def capability(self):
        command = connections.client_command('codex')
        if not command:
            return False, 'Install the official Codex client to use weekly activation.'
        code, help_text = claude_cli.run(command[0], ['exec', '--help'], timeout=5, env=environment())
        if code or not all(flag in help_text for flag in ('--ignore-user-config', '--ephemeral', '--sandbox', '--json')):
            return False, 'Update Codex to a version with isolated ephemeral execution.'
        return True, 'A short GPT-6 Luna subscription prompt with low reasoning.'

    @staticmethod
    def identity(row):
        home = Path(os.environ.get('CODEX_HOME', Path.home()/'.codex'))
        auth = providers.load(home/'auth.json')
        tokens = auth.get('tokens') or {}
        if (auth.get('auth_mode') not in (None, 'chatgpt') or auth.get('OPENAI_API_KEY') or
                not tokens.get('access_token') or not isinstance(tokens.get('account_id'), str) or
                model.identity('codex', tokens['account_id']) != row['id']):
            raise ValueError('Codex subscription identity mismatch')
        return home

    def prepare(self, row):
        if not self.capability()[0]:
            raise ValueError('Codex runner unavailable')
        self.identity(row)
        return connections.client_command('codex')[0], row['id'], workspace()

    def send(self, prepared):
        executable, identity, cwd = prepared
        # Re-read immediately before launch, without copying, refreshing or changing auth.
        try:
            self.identity({'id': identity})
        except Exception as error:
            raise NotLaunched('Codex identity changed') from error
        args = ['exec', '--ignore-user-config', '--ignore-rules', '--ephemeral',
                '--skip-git-repo-check', '--sandbox', 'read-only', '--json', '--color', 'never',
                '--model', 'gpt-6-luna']
        for setting in ('forced_login_method="chatgpt"', 'model_provider="openai"',
                        'model_reasoning_effort="low"', 'approval_policy="never"',
                        'web_search="disabled"', 'features.shell_tool=false',
                        'features.hooks=false', 'features.apps=false', 'features.multi_agent=false',
                        'features.unified_exec=false', 'project_doc_max_bytes=0',
                        'history.persistence="none"'):
            args.extend(['-c', setting])
        code, text = claude_cli.run(executable, [*args, PROMPT], timeout=90, env=environment(), cwd=cwd)
        events = [json.loads(line) for line in text.splitlines() if line.strip()]
        completed = [e for e in events if e.get('type') == 'turn.completed']
        if code or len(completed) != 1 or any(e.get('type') in ('error', 'turn.failed') for e in events):
            raise ValueError('Uncertain Codex delivery')
        return counters(completed[0])


class AntigravityRunner:
    def capability(self):
        antigravity_cli.command()
        return True, 'A short Flash, Argon Low, or GPT-OSS subscription prompt using an isolated text-only CLI profile. Normal plugins and settings stay unchanged.'

    @staticmethod
    def identity(row):
        antigravity_cli.check_auth_mode()
        access, revision = antigravity_cli.credential()
        subject, _ = antigravity_cli.profile(access)
        if model.identity('antigravity', 'cli-google-sub\n'+subject) != row['id']:
            raise ValueError('Antigravity subscription identity mismatch')
        return revision

    def prepare(self, row):
        from . import antigravity_activation as isolated
        stage = 'identity'
        try:
            revision = self.identity(row)
            stage = 'client'
            executable = antigravity_cli.command()[0]
            stage = 'profile'
            home, cwd = isolated.profile()
            stage = 'probe'
            isolated.preflight(executable, home, cwd)
            return executable, MODELS[row['activationGroup']], revision, cwd, home
        except Exception as error:
            raise ActivationFailure(error_code(error, stage)) from error

    def send(self, prepared):
        from . import antigravity_activation as isolated
        executable, selected, revision, cwd, home = prepared
        stage = 'profile'
        try:
            isolated.validate(home, cwd)
            stage = 'identity'
            antigravity_cli.check_auth_mode()
            current = antigravity_cli.credential()[1]
        except Exception as error:
            failure = NotLaunched('Antigravity pre-launch check failed')
            failure.code = error_code(error, stage)
            raise failure from error
        if current != revision:
            failure = NotLaunched('Antigravity identity changed')
            failure.code = 'identity'
            raise failure
        args = ['--agent', isolated.AGENT_NAME, '-p', PROMPT, '--model', selected, '--mode', 'plan', '--disable-slash-commands',
                '--output-format', 'stream-json', '--print-timeout', '60s']
        code, text = claude_cli.run(executable, args, timeout=75, env=isolated.child_environment(home), cwd=cwd)
        try:
            events = [json.loads(line) for line in text.splitlines() if line.strip()]
        except ValueError as error:
            raise ActivationFailure('delivery') from error
        results = [e['result'] for e in events if e.get('event') == 'result']
        if any(e.get('step_update', {}).get('step_type') in ('tool', 'subagent') for e in events):
            raise ActivationFailure('tools')
        if code or len(results) != 1 or results[0].get('status') != 'SUCCESS':
            raise ActivationFailure('delivery')
        return counters(results[0])
