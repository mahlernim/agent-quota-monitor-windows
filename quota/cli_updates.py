"""Quiet, public CLI release checks. No quota requests, prompts or installation."""
from itertools import islice
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import threading
import time
import urllib.error
import urllib.request
from email.utils import parsedate_to_datetime

PROVIDERS = ('codex', 'claude', 'antigravity', 'copilot')
DAY = 24 * 60 * 60
MANUAL_INTERVAL = 60
PACKAGES = {'codex': '@openai/codex', 'claude': '@anthropic-ai/claude-code', 'copilot': '@github/copilot'}
AG_MANIFEST = 'https://antigravity-cli-auto-updater-974169037036.us-central1.run.app/manifests/windows_amd64.json'


def version(value):
    if not isinstance(value, str) or not re.fullmatch(r'\d{1,8}\.\d{1,8}\.\d{1,8}', value):
        raise ValueError('Unsupported release version')
    return tuple(map(int, value.split('.')))


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise ValueError('Unexpected metadata redirect')


def latest(provider, channel):
    url = AG_MANIFEST if provider == 'antigravity' else 'https://registry.npmjs.org/-/package/' + PACKAGES[provider] + '/dist-tags'
    request = urllib.request.Request(url, headers={'User-Agent': 'AgentQuotaMonitor', 'Accept': 'application/json'})
    with urllib.request.build_opener(NoRedirect).open(request, timeout=12) as response:
        raw = response.read(65537)
    if len(raw) > 65536:
        raise ValueError('Oversize metadata')
    data = json.loads(raw)
    result = data['version'] if provider == 'antigravity' else data[channel]
    version(result)
    return result


def discover(provider):
    """Resolve known client paths. Only a bounded --version process is permitted."""
    from .connections import client_command
    local = Path(os.environ.get('LOCALAPPDATA', str(Path.home()))).resolve()
    roaming = Path(os.environ.get('APPDATA', str(Path.home()))).resolve()
    method, channel = 'manual', 'latest'
    if provider == 'claude':
        from .claude_cli import client_info
        info = client_info()
        executable = info.get('executable')
        method = info.get('clientUpdateMethod', 'manual')
        # Read only the public release preference, never auth or credential files.
        settings = Path.home() / '.claude/settings.json'
        if settings.exists():
            data = json.loads(settings.read_text(encoding='utf-8'))
            channel = data.get('autoUpdatesChannel', 'latest')
        if channel not in ('latest', 'stable'):
            raise ValueError('Unsupported release channel')
    elif provider == 'codex':
        # Desktop-bundled builds can shadow the standalone CLI on PATH and
        # have a separate release lifecycle. Update the known CLI installation.
        native = local / 'Programs/OpenAI/Codex/bin/codex.exe'
        npm = sorted(islice((roaming / 'npm/node_modules/@openai/codex').glob('**/codex.exe'), 16))
        command = client_command(provider) if not native.is_file() and not npm else None
        executable = str(native) if native.is_file() else str(npm[0]) if npm else command[0] if command else None
    elif provider == 'antigravity':
        from .antigravity_cli import command
        try:
            executable = command()[0]
        except Exception:
            executable = None
    else:
        executable = shutil.which('copilot.exe')
        if not executable:
            npm_candidates = sorted(islice((roaming / 'npm/node_modules/@github/copilot').glob('**/copilot.exe'), 16))
            executable = str(npm_candidates[0]) if npm_candidates else None
        if not executable:
            candidates = sorted((local / 'Microsoft/WinGet/Packages').glob('GitHub.Copilot_*/copilot.exe'))
            executable = str(candidates[-1]) if candidates else None
    if not executable:
        return dict(state='missing', installed='', method='manual', channel=channel, identity='missing')
    path = Path(executable).resolve()
    if provider != 'claude':
        if provider in PACKAGES and path.is_relative_to(roaming / 'npm/node_modules' / PACKAGES[provider]):
            method = 'npm'
        elif provider == 'codex' and path.is_relative_to(local / 'Programs/OpenAI/Codex'):
            method = 'native'
        elif provider == 'antigravity' and path == local / 'agy/bin/agy.exe':
            method = 'native'
        elif provider == 'copilot' and path.is_relative_to(local / 'Microsoft/WinGet/Packages') and path.relative_to(local / 'Microsoft/WinGet/Packages').parts[0].startswith('GitHub.Copilot_'):
            method = 'winget'
    env = os.environ.copy()
    env.update(DISABLE_AUTOUPDATER='1', DISABLE_TELEMETRY='1', DISABLE_ERROR_REPORTING='1', AGY_CLI_DISABLE_AUTO_UPDATE='true')
    if os.name == 'nt':
        from .claude_cli import _windows_executable
        if not _windows_executable(path):
            raise ValueError('Client executable is incomplete')
    result = subprocess.run([str(path), '--version'], stdin=subprocess.DEVNULL, capture_output=True,
                            timeout=5, env=env, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    output = result.stdout[:4096].decode('utf-8', errors='replace')
    match = re.search(r'(?<![\d.])(\d+\.\d+\.\d+)([^\s]*)', output)
    if result.returncode or not match or match[2] not in ('', ')'):
        raise ValueError('Unknown or prerelease client version')
    installed = match[1]
    version(installed)
    stat = path.stat()
    identity = hashlib.sha256(f'{path}|{stat.st_size}|{stat.st_mtime_ns}|{method}|{channel}'.encode()).hexdigest()
    return dict(state='unchecked', installed=installed, method=method, channel=channel, identity=identity)


class CliUpdates:
    def __init__(self, monitor, vault, resolver=discover, fetch=latest, clock=time.time):
        self.monitor, self.vault = monitor, vault
        self.resolver, self.fetch, self.clock = resolver, fetch, clock
        self.lock = threading.RLock()
        self.stop = threading.Event()
        self.busy = False
        self.rows = {}
        self.storage_error = False
        try:
            self.cache = vault.load()
            if not isinstance(self.cache, dict):
                raise ValueError()
        except Exception:
            self.cache = {}
            self.storage_error = True

    def enabled(self):
        with self.monitor.lock:
            enabled = self.monitor.enabled
            if enabled is None:
                enabled = self.monitor.snapshot()['enabledProviders']
            return set(enabled) & set(PROVIDERS)

    def save(self):
        try:
            self.vault.save(self.cache)
            self.storage_error = False
            return True
        except Exception:
            self.storage_error = True
            return False

    def snapshot(self):
        enabled = self.enabled()
        with self.lock:
            return dict(busy=self.busy, storageError=self.storage_error,
                        tools=[dict(provider=p, **{k: v for k, v in self.rows.get(p, dict(state='unchecked')).items()
                                                  if k != 'identity'}) for p in PROVIDERS if p in enabled])

    def request(self):
        with self.lock:
            if self.busy:
                return False
            self.busy = True
        threading.Thread(target=self.check, args=(True,), daemon=True).start()
        return True

    def run(self):
        while not self.stop.is_set():
            with self.lock:
                run = not self.busy
                if run:
                    self.busy = True
            if run:
                self.check(False)
            self.stop.wait(60)

    def check(self, manual=False):
        try:
            for provider in PROVIDERS:
                if self.stop.is_set() or provider not in self.enabled():
                    continue
                now = self.clock()
                try:
                    row = self.resolver(provider)
                    cached = self.cache.get(provider, {})
                    same = cached.get('identity') == row['identity']
                    source_key = '_agyRetryAfter' if provider == 'antigravity' else '_npmRetryAfter'
                    source_ready = now >= self.cache.get(source_key, 0)
                    due = now >= cached.get('nextCheck', 0)
                    manual_due = manual and now >= cached.get('lastAttempt', 0) + MANUAL_INTERVAL and now >= cached.get('retryAfter', 0)
                    if row['state'] != 'missing' and source_ready and (due or manual_due) and provider in self.enabled():
                        # Persist before the request so restarts and failures cannot bypass the daily interval.
                        cached = dict(identity=row['identity'], lastAttempt=now, nextCheck=now + DAY)
                        self.cache[provider] = cached
                        if not self.save():
                            raise OSError('Cache unavailable')
                        try:
                            cached['latest'] = self.fetch(provider, row['channel'])
                            cached['checkedAt'] = now
                        except urllib.error.HTTPError as error:
                            retry = error.headers.get('Retry-After', '')
                            try:
                                deadline = now + float(retry) if retry.isdigit() else parsedate_to_datetime(retry).timestamp()
                            except (ValueError, TypeError, OverflowError):
                                deadline = now + MANUAL_INTERVAL
                            cached['retryAfter'] = max(now + MANUAL_INTERVAL, deadline)
                            self.cache[source_key] = cached['retryAfter']
                            cached['nextCheck'] = max(cached['nextCheck'], cached['retryAfter'])
                            cached['failed'] = True
                        except Exception:
                            cached['failed'] = True
                        if not self.save():
                            cached['failed'] = True
                        same = True
                    if row['state'] != 'missing':
                        if not same:
                            row['state'] = 'unchecked'
                        elif cached.get('failed'):
                            row['state'] = 'failed'
                        elif cached.get('latest') and now < cached.get('nextCheck', 0):
                            row.update(latest=cached['latest'], checkedAt=cached['checkedAt'])
                            row['state'] = 'available' if version(cached['latest']) > version(row['installed']) else 'current'
                    row['nextCheck'] = cached.get('nextCheck', 0)
                    with self.lock:
                        self.rows[provider] = row
                except Exception:
                    with self.lock:
                        self.rows[provider] = dict(state='failed')
        finally:
            with self.lock:
                self.busy = False

    def prepare(self, provider):
        if provider not in self.enabled():
            raise ValueError('Provider is disabled')
        current = self.resolver(provider)
        with self.lock:
            row = self.rows.get(provider, {})
            if (self.busy or provider not in self.enabled() or row.get('state') != 'available'
                    or row.get('identity') != current.get('identity')
                    or self.clock() >= row.get('nextCheck', 0)):
                raise ValueError('Check for updates again before updating')
            return dict(provider=provider, method=row['method'], channel=row['channel'])
