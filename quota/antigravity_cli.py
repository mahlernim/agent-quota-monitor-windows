"""Official quota-only CLI reader with a verified, independently bound identity."""
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import time
from . import model
from .providers import ReadError, account, request
from .vault import Vault

_identity_retry = {}


class Credential(ctypes.Structure):
    _fields_ = [('Flags', wintypes.DWORD), ('Type', wintypes.DWORD), ('TargetName', wintypes.LPWSTR),
                ('Comment', wintypes.LPWSTR), ('LastWritten', wintypes.FILETIME),
                ('CredentialBlobSize', wintypes.DWORD), ('CredentialBlob', ctypes.POINTER(ctypes.c_ubyte)),
                ('Persist', wintypes.DWORD), ('AttributeCount', wintypes.DWORD), ('Attributes', ctypes.c_void_p),
                ('TargetAlias', wintypes.LPWSTR), ('UserName', wintypes.LPWSTR)]


def credential():
    """Read only the official session. Never write or renew its credentials."""
    if os.name != 'nt':
        raise ReadError('local_session_unavailable')
    api = ctypes.WinDLL('advapi32', use_last_error=True)
    pointer = ctypes.POINTER(Credential)()
    api.CredReadW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.POINTER(ctypes.POINTER(Credential))]
    api.CredReadW.restype = wintypes.BOOL
    api.CredFree.argtypes = [ctypes.c_void_p]
    if not api.CredReadW('gemini:antigravity', 1, 0, ctypes.byref(pointer)):
        raise ReadError('local_session_unavailable')
    try:
        if pointer.contents.CredentialBlobSize > 65536:
            raise ReadError('schema_changed')
        blob = ctypes.string_at(pointer.contents.CredentialBlob, pointer.contents.CredentialBlobSize)
        data = json.loads(blob.decode('utf-8'))
        token = data['token']
        access, refresh = token['access_token'], token['refresh_token']
        if not isinstance(access, str) or not access or not isinstance(refresh, str) or not refresh:
            raise ValueError()
        return access, hashlib.sha256(refresh.encode()).hexdigest()
    except (ValueError, KeyError, TypeError):
        raise ReadError('local_session_unavailable') from None
    finally:
        api.CredFree(pointer)


def command():
    executable = shutil.which('agy.exe')
    if not executable and os.environ.get('LOCALAPPDATA'):
        path = Path(os.environ['LOCALAPPDATA']) / 'agy/bin/agy.exe'
        if path.is_file():
            executable = str(path.resolve())
    if not executable:
        raise ReadError('antigravity_cli_unavailable')
    return [executable, '-p', '/usage', '--print-timeout', '20s']


def profile(access):
    raw = request('https://openidconnect.googleapis.com/v1/userinfo', {'Authorization': 'Bearer ' + access})
    subject, email = raw.get('sub'), raw.get('email')
    if not isinstance(subject, str) or not subject or not isinstance(email, str) or not email or raw.get('email_verified') is not True:
        raise ReadError('stable_identity_unavailable')
    return subject, email


def parse_usage(text):
    groups = {}
    names = {'Gemini Models': 'gemini', 'Claude and GPT models': 'claude-gpt'}
    windows = {'Weekly Limit Remaining': ('weekly', 604800), 'Five Hour Limit Remaining': ('5h', 18000)}
    for line in text.splitlines():
        if not line.strip():
            continue
        fields = line.split('\t')
        if len(fields) != 4:
            raise ReadError('schema_changed')
        name, window, remaining, reset = fields
        if name not in names or window not in windows or not re.fullmatch(r'\d+(?:\.\d+)?%', remaining):
            raise ReadError('schema_changed')
        value = model.percent(float(remaining[:-1]))
        if value is None or model.timestamp(reset) is None:
            raise ReadError('schema_changed')
        key, seconds = windows[window]
        group = groups.setdefault(names[name], dict(id=names[name], label=name, buckets=[]))
        if any(b['id'] == key for b in group['buckets']):
            raise ReadError('schema_changed')
        group['buckets'].append(model.bucket(key, window, value, seconds, reset))
    if not groups:
        raise ReadError('quota_not_reported')
    return list(groups.values())


def usage():
    try:
        # Fixed built-in command only. No model prompt, shell, or inherited workspace.
        result = subprocess.run(command(), stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                stderr=subprocess.DEVNULL, timeout=30, cwd=Path.home(),
                                creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        if result.returncode:
            raise ReadError('antigravity_cli_failed')
        if len(result.stdout) > 65536:
            raise ReadError('response_too_large')
        return parse_usage(result.stdout.decode('utf-8-sig'))
    except subprocess.TimeoutExpired:
        raise ReadError('antigravity_cli_timeout') from None
    except (OSError, UnicodeError):
        raise ReadError('antigravity_cli_failed') from None


def descriptor_vault():
    return Vault(Path(os.environ['LOCALAPPDATA']) / 'QuotaDashboard/antigravity-cli-account.dpapi')


def check_auth_mode():
    path = Path.home() / '.gemini/antigravity-cli/settings.json'
    try:
        settings = json.loads(path.read_text(encoding='utf-8-sig')) if path.exists() else {}
        if not isinstance(settings, dict) or settings.get('modelProvider') or os.environ.get('GEMINI_API_KEY') or os.environ.get('GOOGLE_GEMINI_BASE_URL'):
            raise ReadError('antigravity_cli_auth_unsupported')
    except (OSError, ValueError):
        raise ReadError('antigravity_cli_auth_unsupported') from None


def cli_account():
    check_auth_mode()
    command()
    access, revision = credential()
    vault = descriptor_vault()
    binding = vault.load()
    if not isinstance(binding, dict):
        binding = {}
    if binding.get('revision') != revision or not binding.get('subject') or not binding.get('email'):
        fingerprint = hashlib.sha256(access.encode()).hexdigest()
        retry = _identity_retry.get(fingerprint)
        if retry and time.monotonic() < retry[0]:
            raise ReadError(retry[1])
        try:
            subject, email = profile(access)
        except ReadError as err:
            _identity_retry.clear()
            _identity_retry[fingerprint] = (time.monotonic() + max(300, err.retry_after), err.code)
            raise
        _identity_retry.clear()
        if credential()[1] != revision:
            raise ReadError('identity_changed')
        binding = dict(subject=subject, email=email, revision=revision)
        vault.save(binding)
    subject = binding['subject']

    def read():
        check_auth_mode()
        if credential()[1] != revision:
            raise ReadError('identity_changed')
        groups = usage()
        check_auth_mode()
        fresh_access, fresh_revision = credential()
        current_subject, email = profile(fresh_access)
        if current_subject != subject or credential()[1] != fresh_revision:
            raise ReadError('identity_changed')
        # The official CLI may rotate its own refresh token. Preserve stable identity.
        vault.save(dict(subject=subject, email=email, revision=fresh_revision))
        return groups, email + ' · CLI'

    result = account('antigravity', 'cli-google-sub\n' + subject, binding['email'] + ' · CLI',
                     'Official Antigravity CLI /usage (desktop app not required)', read,
                     'Google account ID verified; independent CLI session')
    result['sessionRevision'] = revision
    result['accountEmail'] = binding['email']
    return result
