"""Quota-only reads through Claude Code. The CLI alone owns authentication."""
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import threading

from . import model
from .providers import ReadError, account, load

MIN_VERSION = (2, 1, 281)
MAX_OUTPUT = 1024 * 1024
SOURCE = 'Official Claude Code CLI /usage'
_versions = {}
_version_lock = threading.Lock()


def environment():
    # Reject overrides instead of silently reading a different subscription.
    overrides = ('CLAUDE_CONFIG_DIR', 'CLAUDE_CODE_OAUTH_TOKEN', 'CLAUDE_CODE_OAUTH_TOKEN_FILE',
                 'ANTHROPIC_API_KEY', 'ANTHROPIC_AUTH_TOKEN', 'ANTHROPIC_BASE_URL',
                 'ANTHROPIC_PROFILE', 'CLAUDE_CODE_USE_BEDROCK', 'CLAUDE_CODE_USE_VERTEX',
                 'CLAUDE_CODE_USE_FOUNDRY', 'CLAUDE_CODE_API_KEY_FILE_DESCRIPTOR')
    if any(os.environ.get(name) for name in overrides):
        raise ReadError('claude_cli_auth_unsupported')
    env = dict(os.environ)
    # This broad switch also disables usage retrieval in the tested CLI.
    env.pop('CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC', None)
    env.update(DISABLE_TELEMETRY='1', DISABLE_ERROR_REPORTING='1', DISABLE_AUTOUPDATER='1')
    return env


def _stop(process):
    if process.poll() is None:
        if os.name == 'nt':
            try:
                subprocess.run([str(Path(os.environ['SYSTEMROOT']) / 'System32/taskkill.exe'),
                                '/PID', str(process.pid), '/T', '/F'],
                               stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                               timeout=5, creationflags=subprocess.CREATE_NO_WINDOW)
            except (OSError, subprocess.TimeoutExpired):
                pass
        if process.poll() is None:
            process.kill()
        process.wait(timeout=5)


def run(executable, args, timeout=30):
    """Bound memory and runtime. Never retain CLI output in files or diagnostics."""
    process = None
    reader = None
    output = []
    finished = threading.Event()
    try:
        process = subprocess.Popen([executable, *args], stdin=subprocess.DEVNULL,
                                   stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                   cwd=tempfile.gettempdir(), env=environment(),
                                   creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))

        def capture():
            try:
                output.append(process.stdout.read(MAX_OUTPUT + 1))
            except OSError:
                output.append(b'')
            finally:
                finished.set()

        reader = threading.Thread(target=capture, daemon=True)
        reader.start()
        if not finished.wait(timeout):
            raise ReadError('claude_cli_timeout')
        if len(output[0]) > MAX_OUTPUT:
            raise ReadError('response_too_large')
        try:
            code = process.wait(timeout=1)
        except subprocess.TimeoutExpired:
            raise ReadError('claude_cli_timeout') from None
        # Auth status can return 1 with a useful loggedIn=false JSON document.
        return code, output[0].decode('utf-8-sig')
    except (OSError, UnicodeError):
        raise ReadError('claude_cli_failed') from None
    finally:
        if process:
            _stop(process)
            if reader:
                reader.join(timeout=2)
            process.stdout.close()


def supported_command():
    from .connections import client_command
    command = client_command('claude')
    if not command:
        return None
    executable = command[0]
    try:
        stat = Path(executable).stat()
        key = (executable, stat.st_mtime_ns, stat.st_size)
    except OSError:
        raise ReadError('claude_cli_failed') from None
    with _version_lock:
        if key not in _versions:
            code, text = run(executable, ['--version'], timeout=5)
            match = re.fullmatch(r'(\d+)\.(\d+)\.(\d+) \(Claude Code\)\s*', text)
            if code or not match:
                raise ReadError('claude_cli_failed')
            version = tuple(map(int, match.groups()))
            _versions.clear()
            _versions[key] = MIN_VERSION <= version < (3, 0, 0)
        return executable if _versions[key] else None


def auth_status(executable):
    code, text = run(executable, ['--safe-mode', 'auth', 'status', '--json'], timeout=10)
    try:
        data = json.loads(text)
    except ValueError:
        raise ReadError('claude_cli_failed') from None
    if not isinstance(data, dict):
        raise ReadError('schema_changed')
    if data.get('loggedIn') is False:
        raise ReadError('sign_in_required')
    if code or data.get('loggedIn') is not True:
        raise ReadError('claude_cli_failed')
    if data.get('authMethod') != 'claude.ai' or data.get('apiProvider') != 'firstParty':
        raise ReadError('claude_cli_auth_unsupported')
    return data


def parse_usage(text):
    """Accept only the built-in command's structured quota, never prose or local costs."""
    try:
        events = [json.loads(line) for line in text.splitlines() if line.strip()]
        if not events or any(not isinstance(e, dict) for e in events):
            raise ValueError()
        results = [e for e in events if e.get('type') == 'result']
        reports = [e for e in events if e.get('type') == 'assistant' and 'usage_report' in e]
        if len(results) != 1 or len(reports) != 1:
            raise ValueError()
        result = results[0]
        if result.get('is_error') is not False or result.get('subtype') != 'success':
            raise ReadError('claude_cli_failed')
        if result.get('num_turns') != 0 or result.get('total_cost_usd') != 0:
            raise ValueError()
        counters = result.get('usage')
        if not isinstance(counters, dict) or any(counters.get(k) != 0 for k in
                ('input_tokens', 'output_tokens', 'cache_creation_input_tokens', 'cache_read_input_tokens')):
            raise ValueError()
        rates = reports[0]['usage_report']['rate_limits']
        if rates is None:
            raise ReadError('quota_not_reported')
        limits = rates['limits']
        if not isinstance(limits, list):
            raise ValueError()
        raw = {}
        for limit in limits:
            if not isinstance(limit, dict):
                raise ValueError()
            name = {'session': 'five_hour', 'weekly_all': 'seven_day'}.get(limit.get('kind'))
            # Unknown windows are not assigned invented durations or identities.
            if name is None:
                continue
            used = model.percent(limit.get('percent'))
            reset = limit.get('resets_at')
            if name in raw or used is None or (reset is not None and model.timestamp(reset) is None):
                raise ValueError()
            raw[name] = dict(utilization=used, resets_at=reset)
        groups = model.claude(raw)
        if not groups:
            raise ReadError('quota_not_reported')
        return groups
    except (ValueError, KeyError, TypeError):
        raise ReadError('schema_changed') from None


def usage(executable):
    args = ['--safe-mode', '--strict-mcp-config', '--mcp-config', '{"mcpServers":{}}',
            '--settings', '{"disableAllHooks":true,"remoteControlAtStartup":false}',
            '--tools', '', '--no-session-persistence', '--output-format', 'stream-json',
            '--verbose', '-p', '/usage']
    code, text = run(executable, args)
    if code:
        raise ReadError('claude_cli_failed')
    return parse_usage(text)


def cli_account(executable, home):
    environment()
    metadata_path = home / '.claude.json'

    def identity():
        data = load(metadata_path).get('oauthAccount') or {}
        if not all(isinstance(data.get(k), str) and data[k] for k in
                   ('accountUuid', 'organizationUuid', 'emailAddress')):
            raise ReadError('stable_identity_unavailable')
        return data

    original = identity()
    subject = original['accountUuid']

    def check_identity():
        current = identity()
        # Email is only a consistency check. The account ID remains the stable UUID.
        if any(current[k] != original[k] for k in ('accountUuid', 'organizationUuid', 'emailAddress')):
            raise ReadError('identity_changed')
        status = auth_status(executable)
        if status.get('orgId') != current['organizationUuid'] or status.get('email') != current['emailAddress']:
            raise ReadError('identity_mismatch')

    def read():
        check_identity()
        groups = usage(executable)
        check_identity()
        return groups, original['emailAddress']

    result = account('claude', subject, original['emailAddress'], SOURCE, read,
                     'Claude Code account UUID; local identity checked before and after usage')
    try:
        # File revision only. This reader never opens the credential file.
        result['sessionRevision'] = (home / '.claude/.credentials.json').stat().st_mtime_ns
    except OSError:
        result['sessionRevision'] = None
    return result
