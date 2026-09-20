"""Bounded official-client login handoffs. No credential or OAuth ownership."""
import copy
import os
from pathlib import Path
import shutil
import subprocess
import threading
import time
import uuid

PROVIDERS = ('codex', 'claude', 'antigravity')
ACTIVE = ('starting', 'waiting', 'verifying')


def _environment_path(name):
    value = os.environ.get(name)
    return Path(value) if value else None


def _newest(root, pattern):
    try:
        paths = list(root.glob(pattern))
        return sorted(paths, key=lambda p: p.stat().st_mtime, reverse=True)
    except OSError:
        # Client updates can replace their install tree during discovery.
        return []


def client_command(provider):
    local = _environment_path('LOCALAPPDATA')
    roaming = _environment_path('APPDATA')
    if provider == 'antigravity':
        candidates = [local / 'Programs/Antigravity/Antigravity.exe'] if local else []
        args = []
    elif provider == 'claude':
        candidates = [Path.home() / '.local/bin/claude.exe']
        if local:
            candidates += _newest(local / 'npm-cache/_npx', '*/node_modules/@anthropic-ai/claude-code-win32-*/claude.exe')
        args = ['auth', 'login', '--claudeai']
    elif provider == 'codex':
        candidates = _newest(roaming / 'npm/node_modules/@openai/codex', '**/codex.exe') if roaming else []
        args = ['login']
    else:
        raise ValueError('Unknown provider')
    installed = shutil.which(provider + '.exe')
    if installed:
        candidates.insert(0, Path(installed))
    for path in candidates:
        if path.is_file():
            return [str(path.resolve()), *args]
    return None


def launch(command):
    # Launch only resolved local executables with fixed arguments, never a shell.
    # Output may contain auth URLs or codes, so discard it rather than logging it.
    return subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                            stderr=subprocess.DEVNULL, cwd=Path.home(),
                            creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))


class Connections:
    def __init__(self, monitor, resolver=client_command, launcher=launch, clock=time.time):
        self.monitor, self.resolver, self.launcher, self.clock = monitor, resolver, launcher, clock
        self.lock = threading.RLock()
        self.job = None
        self.process = None
        self.stop = threading.Event()
        self.worker = None
        self.clients = {p: bool(self.resolver(p)) for p in PROVIDERS}

    def snapshot(self):
        with self.lock:
            return dict(clients=self.clients.copy(), job=copy.deepcopy(self.job))

    def start(self, provider, account_id=None):
        if not isinstance(provider, str) or provider not in PROVIDERS:
            raise ValueError('Choose a supported provider.')
        with self.lock:
            if self.worker and self.worker.is_alive():
                raise RuntimeError('A connection is already in progress.')
            rows = self.monitor.snapshot()['accounts']
            target = next((a for a in rows if a['id'] == account_id and a['provider'] == provider), None)
            if account_id is not None and target is None:
                raise ValueError('This account is no longer visible. Refresh the dashboard.')
            command = self.resolver(provider)
            if not command:
                raise ValueError('Official client not found. Install it, then restart the dashboard.')
            self.stop = threading.Event()
            now = self.clock()
            self.job = dict(id=uuid.uuid4().hex, provider=provider, accountId=account_id,
                            expectedLabel=target['label'] if target else None,
                            state='starting', startedAt=now, deadline=now+600,
                            message='Opening the official client…')
            self.worker = threading.Thread(target=self._run, args=(command, copy.deepcopy(self.job), self.stop), daemon=True)
            self.worker.start()
            return copy.deepcopy(self.job)

    def _set(self, job_id, state, message):
        with self.lock:
            if self.job and self.job['id'] == job_id and self.job['state'] in ACTIVE:
                self.job.update(state=state, message=message)

    def cancel(self, job_id):
        with self.lock:
            if not self.job or self.job['id'] != job_id:
                raise ValueError('Connection has already changed.')
            self.stop.set()
            message = 'Stopped waiting. Antigravity stays open and signed in.' if self.job['provider'] == 'antigravity' else 'Stopped waiting. Existing sign-ins are preserved. You can close the authorization tab.'
            self._set(job_id, 'cancelled', message)

    def _verify(self, job):
        rows = [a for a in self.monitor.snapshot()['accounts']
                if a['provider'] == job['provider'] and a.get('lastSuccess', 0)
                and a['lastSuccess'] >= job['startedAt'] and a['status'] == 'live']
        target = next((a for a in rows if a['id'] == job['accountId']), None)
        # Placeholders have no verified identity to compare. Never compare only email.
        placeholder = job['accountId'] is None or job['accountId'].endswith('-pending')
        if target or (placeholder and rows):
            row = target or rows[0]
            self._set(job['id'], 'connected', 'Quota verified for ' + row['label'] + '.')
            return True
        if rows:
            self._set(job['id'], 'different_account', 'The client is signed in as ' + rows[0]['label'] + '. The original account remains separate.')
            return True
        return False

    def _run(self, command, job, stop):
        process = None
        job_id = job['id']
        try:
            if stop.is_set():
                return
            process = self.launcher(command)
            desktop = job['provider'] == 'antigravity'
            rediscovered = False
            with self.lock:
                self.process = process
            self._set(job_id, 'waiting', 'Complete sign-in in Antigravity, then leave the app open.' if desktop else 'Complete sign-in in the browser opened by the official client. This renews that client’s session too.')
            while not stop.wait(1):
                if self.clock() >= job['deadline']:
                    self._set(job_id, 'timed_out', 'Connection timed out. Existing sign-ins are preserved. Retry when ready.')
                    break
                status = process.poll()
                if not desktop and status is not None:
                    if status != 0:
                        self._set(job_id, 'failed', 'The official client could not finish sign-in. Retry, or complete sign-in in its terminal.')
                        break
                    self._set(job_id, 'verifying', 'Sign-in finished. Waiting for an account-verified quota read. Provider cooldowns still apply.')
                if desktop or status == 0:
                    if not rediscovered:
                        self.monitor.next_discovery = 0
                        rediscovered = True
                    self.monitor.refresh()
                    if self._verify(job):
                        break
        except Exception:
            self._set(job_id, 'failed', 'Could not complete the official-client connection. No credentials were copied or logged.')
        finally:
            # Never close Antigravity, which may be the user's existing desktop session.
            if process and job['provider'] != 'antigravity' and process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
            with self.lock:
                self.process = None

    def close(self):
        with self.lock:
            if self.job:
                self.cancel(self.job['id'])
            worker = self.worker
        # Join outside the state lock so the worker can finish its cleanup.
        if worker and worker is not threading.current_thread():
            worker.join(timeout=15)
