"""Read existing official sessions. Never refresh, overwrite, or export credentials."""
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import time
import urllib.error
import urllib.request
from . import model
from .retry import MAX_TIMESTAMP, delay_seconds

HOME = Path.home()
ROOT = Path(__file__).resolve().parents[1]
# Windows socket errors for a network that is down or unreachable.
UNREACHABLE = frozenset((10050, 10051, 10065))


class ReadError(Exception):
    def __init__(self, code, retry_after=0):
        self.code = code if isinstance(code, str) and re.fullmatch(r'[a-z][a-z0-9_]{0,95}', code) else 'reader_failed'
        self.retry_after = delay_seconds(retry_after)
        super().__init__(self.code)


def request(url, headers=None, body=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(url, data=data, headers={'Accept': 'application/json', 'Content-Type': 'application/json', **(headers or {})})
    # Disable redirects so credentials never follow a provider redirect.
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *args, **kwargs):
            return None
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    try:
        with opener.open(req, timeout=12) as response:
            data = response.read(2 * 1024 * 1024 + 1)
            if len(data) > 2 * 1024 * 1024:
                raise ReadError('response_too_large')
            parsed = json.loads(data)
            if not isinstance(parsed, dict):
                raise ReadError('schema_changed')
            return parsed
    except urllib.error.HTTPError as err:
        retry = err.headers.get('Retry-After', '')
        try:
            # Avoid Python's integer-string limit without losing a valid huge wait.
            digits = retry.strip().lstrip('0') or '0'
            wait = MAX_TIMESTAMP + 1 if digits.isascii() and digits.isdigit() and len(digits) > 12 else max(0, int(retry))
        except ValueError:
            from email.utils import parsedate_to_datetime
            import time
            try:
                wait = max(0, parsedate_to_datetime(retry).timestamp() - time.time())
            except Exception:
                wait = 0
        raise ReadError('sign_in_required' if err.code in (401, 403) else 'rate_limited' if err.code == 429 else 'provider_http_' + str(err.code), wait) from None
    except urllib.error.URLError as err:
        # These failures happen before any request reaches the provider.
        if isinstance(err.reason, (socket.gaierror, ConnectionRefusedError)) or (
                isinstance(err.reason, OSError) and getattr(err.reason, 'winerror', None) in UNREACHABLE):
            raise ReadError('network_unavailable') from None
        raise ReadError('connection_or_response_error') from None
    except (OSError, ValueError):
        raise ReadError('connection_or_response_error') from None


def load(path):
    try:
        return json.loads(Path(path).read_text(encoding='utf-8-sig'))
    except (OSError, ValueError):
        raise ReadError('local_session_unavailable') from None


def account(provider, subject, label, source, reader, identity_status='provider identity'):
    return dict(id=model.identity(provider, subject), provider=provider, label=label,
                source=source, identityStatus=identity_status, read=reader)


def codex_account():
    path = Path(os.environ.get('CODEX_HOME', HOME / '.codex')) / 'auth.json'
    d = load(path)
    tokens = d.get('tokens') or {}
    subject = tokens.get('account_id')
    if not subject or not tokens.get('access_token'):
        raise ReadError('sign_in_required')
    def read():
        fresh = load(path).get('tokens') or {}
        if fresh.get('account_id') != subject:
            raise ReadError('identity_changed')
        raw = request('https://chatgpt.com/backend-api/wham/usage',
                      {'Authorization': 'Bearer ' + fresh['access_token'], 'ChatGPT-Account-ID': subject})
        if raw.get('account_id') != subject:
            raise ReadError('identity_mismatch')
        return model.codex(raw), raw.get('email', 'Codex account')
    result = account('codex', subject, 'Codex account', 'Official Codex session / usage endpoint', read)
    result['sessionRevision'] = path.stat().st_mtime_ns
    result['sessionRenewedAt'] = codex_renewed(d.get('last_refresh'))
    return result


def codex_renewed(value):
    """Non-secret renewal time in seconds from auth.json, or None. The token is never decoded."""
    if not isinstance(value, str):
        return None
    parsed = model.timestamp(value)
    if parsed is None:
        return None
    from datetime import datetime
    seconds = datetime.fromisoformat(parsed).timestamp()
    return seconds if 0 < seconds <= MAX_TIMESTAMP else None


def claude_expiry(oauth):
    """Non-secret expiry metadata in seconds, or None when unreported."""
    value = oauth.get('expiresAt') if isinstance(oauth, dict) else None
    if type(value) not in (int, float) or not 0 < value / 1000 <= MAX_TIMESTAMP:
        return None
    return value / 1000


def claude_account():
    path = HOME / '.claude/.credentials.json'
    expires = claude_expiry(load(path).get('claudeAiOauth'))
    metadata = load(HOME / '.claude.json').get('oauthAccount') or {}
    subject = metadata.get('accountUuid')
    if not subject:
        raise ReadError('stable_identity_unavailable')
    def read():
        current = load(HOME / '.claude.json').get('oauthAccount') or {}
        if current.get('accountUuid') != subject:
            raise ReadError('identity_changed')
        oauth = load(path).get('claudeAiOauth') or {}
        if not oauth.get('accessToken'):
            raise ReadError('sign_in_required')
        # Only Claude Code renews its session. Never send a token it has let expire.
        expiry = claude_expiry(oauth)
        if expiry is not None and time.time() >= expiry:
            raise ReadError('session_expired')
        headers = {'Authorization': 'Bearer ' + oauth['accessToken'], 'anthropic-beta': 'oauth-2025-04-20', 'anthropic-version': '2023-06-01'}
        profile = request('https://api.anthropic.com/api/oauth/profile', headers).get('account') or {}
        if profile.get('uuid') != subject:
            raise ReadError('identity_mismatch')
        raw = request('https://api.anthropic.com/api/oauth/usage', headers)
        return model.claude(raw), profile.get('email', current.get('emailAddress', 'Claude account'))
    result = account('claude', subject, metadata.get('emailAddress', 'Claude account'), 'Official Claude Code session / OAuth usage endpoint', read)
    result['sessionRevision'] = path.stat().st_mtime_ns
    result['sessionExpiresAt'] = expires
    return result


def powershell(script):
    try:
        result = subprocess.run(['powershell.exe', '-NoProfile', '-NonInteractive', '-Command', script],
                                capture_output=True, encoding='utf-8', errors='replace', timeout=18,
                                cwd=ROOT, creationflags=subprocess.CREATE_NO_WINDOW)
        if result.returncode:
            raise ReadError('local_discovery_failed')
        return result.stdout
    except (OSError, subprocess.TimeoutExpired):
        raise ReadError('local_discovery_failed') from None


def antigravity_accounts():
    from .antigravity_cli import cli_account
    try:
        return [cli_account()]
    except ReadError as err:
        cli_error = err
    except (OSError, ValueError):
        cli_error = ReadError('antigravity_cli_failed')
    rows = antigravity_desktop_accounts()
    if not rows:
        # Without a desktop fallback, the CLI result explains the missing card.
        raise cli_error
    return rows


LANGUAGE_SERVERS = frozenset(('language_server.exe', 'language_server_windows_x64.exe'))


def process_names():
    """Return running executable names only, or None when Windows cannot list them."""
    if os.name != 'nt':
        return None
    import ctypes
    from ctypes import wintypes

    class Entry(ctypes.Structure):
        _fields_ = [('dwSize', wintypes.DWORD), ('cntUsage', wintypes.DWORD), ('th32ProcessID', wintypes.DWORD),
                    ('th32DefaultHeapID', ctypes.c_size_t), ('th32ModuleID', wintypes.DWORD),
                    ('cntThreads', wintypes.DWORD), ('th32ParentProcessID', wintypes.DWORD),
                    ('pcPriClassBase', ctypes.c_long), ('dwFlags', wintypes.DWORD), ('szExeFile', ctypes.c_wchar * 260)]
    try:
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.CreateToolhelp32Snapshot.restype = ctypes.c_void_p
        kernel.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
        kernel.Process32FirstW.argtypes = kernel.Process32NextW.argtypes = [ctypes.c_void_p, ctypes.POINTER(Entry)]
        kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        snapshot = kernel.CreateToolhelp32Snapshot(0x2, 0)
        if snapshot is None or snapshot == ctypes.c_void_p(-1).value:
            return None
        names = set()
        try:
            entry = Entry()
            entry.dwSize = ctypes.sizeof(Entry)
            found = kernel.Process32FirstW(snapshot, ctypes.byref(entry))
            while found:
                names.add(entry.szExeFile.lower())
                found = kernel.Process32NextW(snapshot, ctypes.byref(entry))
        finally:
            kernel.CloseHandle(snapshot)
        return names
    except (OSError, AttributeError):
        return None


def antigravity_desktop_accounts():
    names = process_names()
    if names is not None and not names & LANGUAGE_SERVERS:
        # Skip the slower command-line lookup while no language server is running.
        return []
    # Process command lines contain a local RPC secret. They never leave this function.
    script = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; @(Get-CimInstance Win32_Process -Filter \"Name='language_server.exe' OR Name='language_server_windows_x64.exe'\" | Select-Object ProcessId,CommandLine) | ConvertTo-Json -Compress"
    rows = json.loads(powershell(script) or '[]')
    if isinstance(rows, dict):
        rows = [rows]
    result = []
    for row in rows:
        command = row.get('CommandLine') or ''
        flags = {m[0]: next(v for v in m[1:] if v) for m in re.findall(r'--(\w+)(?:=|\s+)(?:"([^"]+)"|\'([^\']+)\'|([^\s]+))', command)}
        directory = flags.get('app_data_dir', '')
        if 'antigravity' not in directory.lower() or not flags.get('csrf_token'):
            continue
        pid = int(row['ProcessId'])
        ports = powershell(f'Get-NetTCPConnection -State Listen -OwningProcess {pid} -ErrorAction SilentlyContinue | Select-Object -ExpandProperty LocalPort').split()
        for port in set(ports):
            base = f'http://127.0.0.1:{int(port)}/exa.language_server_pb.LanguageServerService/'
            headers = {'X-Codeium-Csrf-Token': flags['csrf_token'], 'Connect-Protocol-Version': '1'}
            body = {'metadata': {'ideName': 'antigravity', 'extensionName': 'antigravity', 'locale': 'en'}}
            try:
                status = request(base + 'GetUserStatus', headers, body).get('userStatus') or {}
                email = status.get('email')
                if not email:
                    continue
            except ReadError:
                continue
            # No provider subject in this RPC. Keep independent sources separate.
            subject = os.path.normcase(directory) + '\n' + email
            def read(base=base, headers=headers, body=body, email=email):
                before = request(base + 'GetUserStatus', headers, body).get('userStatus') or {}
                if before.get('email') != email:
                    raise ReadError('identity_changed')
                raw = request(base + 'RetrieveUserQuotaSummary', headers, body)
                after = request(base + 'GetUserStatus', headers, body).get('userStatus') or {}
                if after.get('email') != email:
                    raise ReadError('identity_changed')
                return model.antigravity(raw), email
            result.append(account('antigravity', subject, email, 'Official running Antigravity local service', read,
                                  'Local session identity only; provider account ID unreported'))
            break
    return result
