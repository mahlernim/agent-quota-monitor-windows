"""Windows worker isolation supported by antigravity-cli maintainer issue #1261.

Only the child USERPROFILE changes. Windows Credential Manager remains owned by
the official client. No credentials or normal configuration are copied.
"""
import json
import os
from pathlib import Path
import re
from . import claude_cli
from .activation_errors import ActivationFailure

AGENT_NAME = 'aqm-activation'
AGENT = '''---
name: aqm-activation
description: Reply only OK for subscription window activation.
mainAgent: true
subagent: false
inheritCustomizations: false
inheritMcp: false
tools: []
---
# Instructions
Reply only OK. Do not use tools, read files, access URLs, or delegate.
'''


def unlinked(path):
    for part in (path, *path.parents):
        if part.is_symlink() or part.is_junction():
            raise ActivationFailure('profile')


def owned_file(path, value):
    unlinked(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    if not path.exists():
        with path.open('x', encoding='utf-8', newline='\n') as output:
            output.write(value)


def profile():
    root = Path(os.environ['LOCALAPPDATA']) / 'QuotaDashboard/activation-antigravity'
    unlinked(root)
    home, cwd = root/'home', root/'workspace'
    owned_file(home/'.gemini/antigravity-cli/settings.json', '{"useG1Credits":false}')
    owned_file(home/'.gemini/config/config.json', '{"userSettings":{"useAiCredits":false}}')
    owned_file(cwd/'.agents/agents/aqm-activation.md', AGENT)
    # An explicit repository boundary excludes parent workspace customizations.
    unlinked(cwd/'.git')
    (cwd/'.git').mkdir(exist_ok=True)
    validate(home, cwd)
    return str(home), str(cwd)


def validate(home, cwd):
    home, cwd = Path(home), Path(cwd)
    unlinked(home)
    unlinked(cwd)
    expected = {'.agents', '.agents/agents', '.agents/agents/aqm-activation.md', '.git'}
    # Walk without following links. Unknown workspace contents never get removed.
    def walk(path):
        for item in path.iterdir():
            unlinked(item)
            yield item
            if item.is_dir():
                yield from walk(item)
    if {p.relative_to(cwd).as_posix() for p in walk(cwd)} != expected:
        raise ActivationFailure('workspace')
    if (cwd/'.agents/agents/aqm-activation.md').read_text(encoding='utf-8') != AGENT:
        raise ActivationFailure('agent')
    for rel in ('.gemini/config/config.json', '.gemini/antigravity-cli/settings.json'):
        unlinked(home/rel)
    config = json.loads((home/'.gemini/config/config.json').read_text(encoding='utf-8'))
    settings = json.loads((home/'.gemini/antigravity-cli/settings.json').read_text(encoding='utf-8'))
    if (config != {'userSettings': {'useAiCredits': False}} or not isinstance(settings, dict)
            or settings.get('useG1Credits', False) is not False):
        raise ActivationFailure('billing')
    if any(settings.get(k) for k in ('modelProvider', 'customModelsConfig', 'gcp', 'hooks', 'statusLine')):
        raise ActivationFailure('profile')
    gemini = home/'.gemini'
    if any(p.name not in ('config', 'antigravity-cli') for p in gemini.iterdir()):
        raise ActivationFailure('profile')
    config_dir = gemini/'config'
    if any(p.name not in ('config.json', 'mcp_config.json', '.migrated', 'projects') for p in config_dir.iterdir()):
        raise ActivationFailure('profile')
    # Inspect configuration links only. The official CLI creates a cli.log link
    # in its private runtime directory, which is neither loaded nor opened here.
    for _ in walk(config_dir):
        pass
    mcp = config_dir/'mcp_config.json'
    if mcp.exists() and json.loads(mcp.read_text(encoding='utf-8').strip() or '{}') not in ({}, {'mcpServers': {}}):
        raise ActivationFailure('mcp')
    if any((gemini/'antigravity-cli'/name).exists() for name in ('skills', 'hooks.json', 'mcp_config.json', 'AGENTS.md', 'GEMINI.md')):
        raise ActivationFailure('profile')


def child_environment(home):
    from .activation_runners import environment
    env = environment()
    # ADC and alternate authentication must never redirect this subscription run.
    env = {k: v for k, v in env.items() if not k.upper().startswith(('AGY_', 'CLOUDSDK_'))}
    env.update(USERPROFILE=home, AGY_CLI_DISABLE_AUTO_UPDATE='true')
    return env


def preflight(executable, home, cwd):
    env = child_environment(home)
    def run(args):
        code, output = claude_cli.run(executable, args, timeout=25, env=env, cwd=cwd)
        if code:
            raise ActivationFailure('probe')
        return output.strip()
    version = run(['--version'])
    match = re.fullmatch(r'(\d+)\.(\d+)\.(\d+)', version)
    if not match or tuple(map(int, match.groups())) < (1, 3, 6):
        raise ActivationFailure('version')
    if run(['plugins', 'list']) not in ('No plugins installed.', 'No imported plugins.'):
        raise ActivationFailure('plugins')
    if run(['mcp', 'list']) != 'No MCP servers configured.':
        raise ActivationFailure('mcp')
    def command(name):
        try:
            result = json.loads(run(['--agent', AGENT_NAME, '-p', '/'+name, '--output-format', 'json']))
            if (result['status'] != 'SUCCESS' or result['num_turns'] != 0 or
                    result['command']['name'] != name or
                    any(value != 0 for value in result['usage'].values())):
                raise ActivationFailure('probe')
            return result['command']['data']
        except (ValueError, KeyError, TypeError, AttributeError) as error:
            raise ActivationFailure('probe') from error
    if command('hooks').get('hooks') != []:
        raise ActivationFailure('hooks')
    config = command('config').get('config', {})
    if config.get('useG1Credits') is not False or any(config.get(k) for k in ('modelProvider', 'customModelsConfig', 'gcp')):
        raise ActivationFailure('billing')
    if command('agents').get('agents') != [AGENT_NAME]:
        raise ActivationFailure('agent')
    validate(home, cwd)
