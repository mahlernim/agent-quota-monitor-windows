"""Opt-in window activation. Cache observations never count as provider reads."""
import copy
from contextlib import contextmanager
from datetime import datetime
import json
import math
import os
from pathlib import Path
import tempfile
import threading
import time
import uuid
from . import claude_cli, connections, model, providers
from .vault import crypt

DEFAULTS = dict(enabled=False, accounts=[], idleMinutes=30, suggestions=True,
                snoozeUntil=0, dismissed=False)
MAX_GAP = 660


def number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def timestamp(value):
    try:
        parsed = datetime.fromisoformat(value.replace('Z', '+00:00'))
        return parsed.timestamp() if parsed.tzinfo else None
    except (ValueError, TypeError, AttributeError, OverflowError):
        return None


def classify(row, now):
    """Only the directly tested Claude five-hour representation is dispatchable."""
    if row.get('provider') != 'claude':
        return 'unsupported', None
    source = row.get('source', '')
    if source not in ('Official Claude Code session / OAuth usage endpoint', claude_cli.SOURCE):
        return 'unsupported', None
    read = row.get('lastSuccess')
    if row.get('status') != 'live' or row.get('error') or not number(read) or not 0 <= now-read <= MAX_GAP:
        return 'stale', None
    groups = [g for g in row.get('groups', []) if g.get('id') == 'direct']
    if len(groups) != 1:
        return 'unknown', None
    group = groups[0]
    buckets = group.get('buckets', [])
    if len({b.get('id') for b in buckets}) != len(buckets):
        return 'unknown', None
    five = next((b for b in buckets if b.get('id') == 'five_hour'), {})
    weekly = next((b for b in buckets if b.get('id') == 'seven_day'), {})
    if (five.get('disabled') is True or five.get('available') is False or
            weekly.get('disabled') is True or weekly.get('available') is False or
            not number(weekly.get('remaining')) or not 0 < weekly['remaining'] <= 100 or
            not number(five.get('remaining')) or not 0 <= five['remaining'] <= 100 or
            five.get('windowSeconds') != 18000 or weekly.get('windowSeconds') != 604800):
        return 'unavailable', None
    reset = timestamp(five.get('resetsAt'))
    if reset is not None and reset > now:
        return 'running', reset
    if five.get('resetsAt') is None and five.get('remaining') == 100 and five.get('inactiveReported') is True:
        if group.get('extraUsageEnabled') is not False:
            return 'billing_unknown', None
        return 'inactive', None
    return 'unknown', None


class Journal:
    """One Windows file lock protects encrypted atomic settings and reservations."""
    def __init__(self, path):
        self.path = Path(path)
        self.guard = threading.RLock()

    @contextmanager
    def transaction(self):
        import msvcrt
        with self.guard:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            with self.path.with_suffix('.lock').open('a+b') as lock:
                if lock.seek(0, 2) == 0:
                    lock.write(b'0')
                    lock.flush()
                lock.seek(0)
                msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
                try:
                    value = (json.loads(crypt(self.path.read_bytes(), True)) if self.path.exists() else
                             dict(version=1, preferences=copy.deepcopy(DEFAULTS), attempts={}, history=[], suggested=[]))
                    if not isinstance(value, dict) or value.get('version') != 1:
                        raise ValueError('Unsupported activation journal')
                    self.validate(value)
                    yield value
                finally:
                    lock.seek(0)
                    msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)

    @staticmethod
    def validate(value):
        prefs = value['preferences']
        if (not isinstance(prefs, dict) or set(prefs) != set(DEFAULTS) or
                any(type(prefs[k]) is not bool for k in ('enabled', 'suggestions', 'dismissed')) or
                type(prefs['idleMinutes']) is not int or prefs['idleMinutes'] not in (30, 60, 120) or
                not number(prefs['snoozeUntil']) or prefs['snoozeUntil'] < 0 or
                not isinstance(prefs['accounts'], list) or
                any(not isinstance(k, str) for k in prefs['accounts']) or
                not isinstance(value['attempts'], dict) or not isinstance(value['history'], list) or
                not isinstance(value['suggested'], list) or any(not isinstance(k, str) for k in value['suggested'])):
            raise ValueError('Invalid activation journal')
        for attempt in value['attempts'].values():
            if (not isinstance(attempt, dict) or not isinstance(attempt.get('id'), str) or
                    attempt.get('state') not in ('reserved', 'sent', 'uncertain', 'confirmed') or
                    not number(attempt.get('at')) or attempt.get('window') != 'five_hour' or
                    (attempt['state'] == 'confirmed' and not number(attempt.get('reset')))):
                raise ValueError('Invalid activation receipt')
        if any(not isinstance(h, dict) or not isinstance(h.get('accountId'), str) or not number(h.get('at')) for h in value['history']):
            raise ValueError('Invalid activation history')

    def save(self, value):
        self.validate(value)
        encrypted = crypt(json.dumps(value, allow_nan=False).encode())
        # Never send until this succeeds. Existing corrupt files are not reset.
        with tempfile.NamedTemporaryFile(dir=self.path.parent, prefix='activation-', suffix='.tmp', delete=False) as f:
            temp = Path(f.name)
            f.write(encrypted)
            f.flush()
            os.fsync(f.fileno())
        os.replace(temp, self.path)


class ClaudeRunner:
    def capability(self):
        command = connections.client_command('claude')
        if not command:
            return False, 'Install Claude Code to use activation.'
        executable = command[0]
        code, version = claude_cli.run(executable, ['--version'], timeout=5)
        # Version-pinned until restrictions are verified on another version.
        if code or version.strip() != '2.1.280 (Claude Code)':
            return False, 'This Claude Code version has not been verified for unattended activation.'
        return True, 'Claude five-hour activation with Haiku. Weekly activation is not available yet.'

    def prepare(self, row):
        ready, _ = self.capability()
        if not ready:
            raise ValueError('Runner unavailable')
        executable = connections.client_command('claude')[0]
        metadata = providers.load(Path.home()/'.claude.json').get('oauthAccount') or {}
        if not all(isinstance(metadata.get(k), str) and metadata[k] for k in ('accountUuid', 'organizationUuid', 'emailAddress')):
            raise ValueError('Unverified identity')
        status = claude_cli.auth_status(executable)
        if (model.identity('claude', metadata['accountUuid']) != row['id'] or
                status.get('orgId') != metadata['organizationUuid'] or status.get('email') != metadata['emailAddress'] or
                status.get('subscriptionType') not in ('pro', 'max')):
            raise ValueError('Subscription identity mismatch')
        return executable

    def send(self, executable):
        args = ['--safe-mode', '--strict-mcp-config', '--mcp-config', '{"mcpServers":{}}',
                '--settings', '{"disableAllHooks":true,"remoteControlAtStartup":false}',
                '--tools', '', '--no-session-persistence', '--model', 'claude-haiku-4-5-20251001',
                '--system-prompt', 'Reply with exactly OK. Do not use tools.',
                '--output-format', 'json', '-p', 'Reply only OK.']
        code, text = claude_cli.run(executable, args, timeout=90)
        result = json.loads(text)
        if code or result.get('is_error') is not False or result.get('num_turns') != 1:
            raise ValueError('Uncertain delivery')
        # Only numeric counters leave the runner. No conversation or credential text.
        return {k: v for k, v in result.get('usage', {}).items()
                if k in ('input_tokens', 'output_tokens', 'cache_read_input_tokens', 'cache_creation_input_tokens') and number(v)}


class Activation:
    def __init__(self, monitor, journal, runner=None, clock=time.time, monotonic=time.monotonic):
        self.monitor, self.journal = monitor, journal
        self.runner = runner or ClaudeRunner()
        self.clock, self.monotonic = clock, monotonic
        self.lock = threading.RLock()
        self.stop = threading.Event()
        self.observations = {}
        self.previous_clock = None
        self.capability = (False, 'Checking activation availability.')
        self.next_probe = 0
        self.error = False
        self.busy = False
        self.preflight_failures = {}
        self.view = dict(preferences=copy.deepcopy(DEFAULTS), accounts=[], suggestion=None)

    def snapshot(self):
        with self.lock:
            return copy.deepcopy(dict(self.view, storageError=self.error, busy=self.busy))

    def configure(self, payload):
        if not isinstance(payload, dict) or not payload:
            raise ValueError('Invalid activation preferences')
        allowed = {'enabled', 'accounts', 'idleMinutes', 'suggestions', 'dismiss'}
        if not set(payload) <= allowed:
            raise ValueError('Invalid activation preference')
        for key in ('enabled', 'suggestions'):
            if key in payload and type(payload[key]) is not bool:
                raise ValueError('Expected boolean')
        if 'idleMinutes' in payload and (type(payload['idleMinutes']) is not int or payload['idleMinutes'] not in (30, 60, 120)):
            raise ValueError('Invalid inactivity delay')
        ids = payload.get('accounts')
        if ids is not None:
            known = {a['id'] for a in self.monitor.snapshot()['accounts'] if a['provider'] == 'claude'}
            if not isinstance(ids, list) or len(ids)>100 or any(not isinstance(i, str) or i not in known for i in ids) or len(ids)!=len(set(ids)):
                raise ValueError('Select known Claude accounts')
        if 'dismiss' in payload and payload['dismiss'] not in ('later', 'never', 'shown'):
            raise ValueError('Invalid dismissal')
        with self.journal.transaction() as data:
            prefs = data['preferences']
            prefs.update({k: v for k, v in payload.items() if k != 'dismiss'})
            action = payload.get('dismiss')
            if action == 'later':
                prefs['snoozeUntil'] = self.clock()+7*86400
                data['suggested'] = []
            elif action == 'never':
                prefs['dismissed'] = True
                prefs['suggestions'] = False
            elif action == 'shown':
                with self.lock:
                    current = self.view.get('suggestion')
                if current and current['accountId'] not in data['suggested']:
                    data['suggested'].append(current['accountId'])
            if payload.get('suggestions') is True:
                prefs['dismissed'] = False
            self.journal.save(data)
        with self.lock:
            self.view['preferences'] = copy.deepcopy(prefs)

    def _observe(self, row, now):
        key = row['id']
        state, reset = classify(row, now)
        old = self.observations.get(key, {})
        read = row.get('lastSuccess')
        source = row.get('source')
        if state not in ('inactive', 'running'):
            self.observations[key] = dict(state=state, last=read, source=source, count=0)
            return self.observations[key]
        if old.get('last') == read and old.get('source') == source:
            return old
        continuous = (old.get('source') == source and number(old.get('last')) and
                      0 < read-old['last'] <= MAX_GAP and old.get('state') == state)
        if state == 'running':
            continuous = continuous and old.get('reset') is not None and abs(old['reset']-reset)<=15
        obs = dict(state=state, source=source, last=read, reset=reset,
                   count=old.get('count', 0)+1 if continuous else 1,
                   since=old['since'] if continuous else read,
                   episode=old.get('episode') if continuous else uuid.uuid4().hex)
        self.observations[key] = obs
        return obs

    def tick(self):
        now, mono = self.clock(), self.monotonic()
        if self.previous_clock:
            wall, steady = self.previous_clock
            if now-wall>MAX_GAP or abs((now-wall)-(mono-steady))>30:
                self.observations.clear()
        self.previous_clock = now, mono
        snapshot = self.monitor.snapshot()
        rows = snapshot['accounts']
        if now >= self.next_probe and any(a['provider']=='claude' for a in rows):
            self.next_probe = now+300
            try:
                self.capability = self.runner.capability()
            except Exception:
                self.capability = False, 'Claude activation runner is unavailable.'
        visible = {a['id'] for a in rows}
        self.observations = {k: v for k, v in self.observations.items() if k in visible}
        candidate = None
        with self.journal.transaction() as data:
            prefs = data['preferences']
            dirty = False
            summaries = []
            suggestion = None
            for row in rows:
                if row['provider'] not in ('claude', 'codex', 'antigravity'):
                    continue
                key = row['id']
                obs = self._observe(row, now)
                attempt = data['attempts'].get(key)
                if attempt and attempt['state'] in ('reserved', 'sent', 'uncertain') and obs['state']=='running' and obs.get('count',0)>=2:
                    # Confirmation cannot use the reads from before dispatch.
                    if obs['since']>attempt['at'] and abs(obs['reset']-attempt['at']-18000)<=600:
                        attempt.update(state='confirmed', reset=obs['reset'])
                        for receipt in data['history']:
                            if receipt.get('id') == attempt['id']:
                                receipt.update(attempt)
                        dirty = True
                supported = row['provider']=='claude' and self.capability[0]
                reason = self.capability[1] if row['provider']=='claude' else 'Automatic activation is not available for this provider. Monitoring is unchanged.'
                states = {'inactive':'Waiting for the inactivity delay.', 'running':'Window is already running.',
                          'stale':'Waiting for fresh provider readings.', 'billing_unknown':'Extra usage must be off and reported by the provider.',
                          'unavailable':'Subscription allowance is unavailable.', 'unknown':'Window state is not established.', 'unsupported':'Activation is not supported for this source.'}
                status = states.get(obs['state'], reason)
                eligible = supported and obs['state']=='inactive' and obs.get('count',0)>=2
                waited = max(0, obs.get('last',now)-obs.get('since',now))
                if attempt:
                    status = {'reserved':'A prompt may have been sent. No automatic retry.', 'sent':'Prompt sent. Waiting for two fresh countdown readings.',
                              'uncertain':'Delivery is uncertain. No automatic retry.', 'confirmed':'Last activation confirmed.'}.get(attempt['state'], status)
                    eligible = eligible and attempt['state']=='confirmed' and now>attempt.get('reset',float('inf')) and obs.get('since',0)>attempt.get('reset',float('inf'))
                if eligible and not prefs['enabled'] and prefs['suggestions'] and not prefs['dismissed'] and now>=prefs['snoozeUntil'] and waited>=10800 and key not in data['suggested']:
                    suggestion = dict(accountId=key, label=row.get('label','Claude'), message='This quota window is waiting to start. AQM can send a short prompt so its reset countdown begins earlier. This uses subscription allowance.')
                if key in self.preflight_failures:
                    status = 'Subscription identity could not be verified. No prompt sent.'
                if (eligible and prefs['enabled'] and key in prefs['accounts'] and waited>=prefs['idleMinutes']*60 and candidate is None and now>=self.preflight_failures.get(key,0)):
                    recent = [h for h in data['history'] if h['accountId']==key and now-h['at']<86400]
                    if len(recent)<5:
                        candidate = row
                    else:
                        status = 'Daily activation limit reached.'
                summaries.append(dict(accountId=key,label=row.get('label',key),provider=row['provider'],supported=supported,
                                      reason=reason,status=status,window='five_hour' if row['provider']=='claude' else None,
                                      suggestionEligible=eligible and waited>=10800,
                                      selected=key in prefs['accounts'],lastAttempt=copy.deepcopy(attempt)))
            if dirty:
                self.journal.save(data)
            with self.lock:
                self.view = dict(preferences=copy.deepcopy(prefs),accounts=summaries,suggestion=suggestion)
                self.error = False
        if candidate and not self.stop.is_set():
            self._dispatch(candidate)

    def _dispatch(self, row):
        # Identity checks may take time. Revalidate snapshot and settings afterward.
        try:
            executable = self.runner.prepare(row)
            self.preflight_failures.pop(row['id'], None)
        except Exception:
            self.preflight_failures[row['id']] = self.clock()+300
            return
        if self.stop.is_set():
            return
        now = self.clock()
        latest = next((a for a in self.monitor.snapshot()['accounts'] if a['id']==row['id']), None)
        if latest is None or latest['source']!=row['source'] or classify(latest,now)[0]!='inactive':
            return
        if not 0 <= latest['lastSuccess']-row['lastSuccess'] <= MAX_GAP:
            return
        key = row['id']
        with self.journal.transaction() as data:
            prefs = data['preferences']
            obs = self.observations[key]
            if not prefs['enabled'] or key not in prefs['accounts'] or obs['last']-obs['since']<prefs['idleMinutes']*60:
                return
            old = data['attempts'].get(key)
            if old and not (old['state']=='confirmed' and obs['since']>old.get('reset',float('inf'))):
                return
            if sum(h['accountId']==key and now-h['at']<86400 for h in data['history'])>=5:
                return
            attempt = dict(id=uuid.uuid4().hex, state='reserved',at=now,episode=obs['episode'],window='five_hour')
            data['attempts'][key]=attempt
            data['history'].append(dict(attempt, accountId=key))
            self.journal.save(data)
        if self.stop.is_set():
            return
        with self.lock:
            self.busy=True
        state, usage = 'uncertain', {}
        try:
            # No inference fallback and no resend, including after a timeout.
            usage = self.runner.send(executable)
            state = 'sent'
        except Exception:
            pass
        finally:
            with self.lock:
                self.busy=False
        with self.journal.transaction() as data:
            if data['attempts'][key]['id']==attempt['id']:
                data['attempts'][key].update(state=state,usage=usage)
                for receipt in data['history']:
                    if receipt.get('id') == attempt['id']:
                        receipt.update(state=state,usage=usage)
                self.journal.save(data)

    def run(self):
        while not self.stop.is_set():
            try:
                self.tick()
            except Exception:
                with self.lock:
                    self.error=True
                    self.view['suggestion']=None
            self.stop.wait(10)

    def close(self):
        self.stop.set()
