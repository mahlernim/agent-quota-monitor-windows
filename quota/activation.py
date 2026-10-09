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
from . import claude_cli, model, providers
from .vault import crypt

DEFAULTS = dict(enabled=False, accounts=[], idleMinutes=30, suggestions=True,
                snoozeUntil=0, dismissed=False)
MAX_GAP = 660
# Confirmation accepts a deadline within this many seconds of the expected end.
TOLERANCE = 600
SECONDS = {'five_hour': 18000, '5h': 18000, 'seven_day': 604800, 'weekly': 604800}
PENDING = ('reserved', 'sent', 'uncertain')


def number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def timestamp(value):
    try:
        parsed = datetime.fromisoformat(value.replace('Z', '+00:00'))
        return parsed.timestamp() if parsed.tzinfo else None
    except (ValueError, TypeError, AttributeError, OverflowError):
        return None


from .activation_windows import classify, target, targets
from .activation_runners import NotLaunched


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
                    attempt.get('state') not in (*PENDING, 'confirmed', 'expired') or
                    not number(attempt.get('at')) or attempt.get('window') not in SECONDS or
                    (attempt['state'] == 'confirmed' and not number(attempt.get('reset'))) or
                    (attempt['state'] == 'expired' and not number(attempt.get('expired')))):
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
        info = claude_cli.client_info(minimum=(2, 1, 280))
        if info['clientState'] == 'missing':
            return False, 'Install Claude Code to use activation.'
        if info['clientState'] != 'supported':
            return False, 'Update Claude Code to use activation.'
        return True, 'A short Haiku subscription prompt. Extra usage must be off.'

    def prepare(self, row):
        info = claude_cli.client_info(minimum=(2, 1, 280))
        if info['clientState'] != 'supported':
            raise ValueError('Runner unavailable')
        executable = info['executable']
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
        from .activation_runners import CodexRunner, AntigravityRunner
        self.runners = {p: runner or r for p, r in [('claude', ClaudeRunner()), ('codex', CodexRunner()), ('antigravity', AntigravityRunner())]}
        self.runner = self.runners['claude']
        self.clock, self.monotonic = clock, monotonic
        self.lock = threading.RLock()
        self.stop = threading.Event()
        self.observations = {}
        self.previous_clock = None
        self.capabilities = {}
        self.next_probe = 0
        self.error = False
        self.busy = False
        self.preflight_failures = {}
        self.barriers = {}
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
            known = {t['activationKey'] for a in self.monitor.snapshot()['accounts'] for t in targets(a)}
            if not isinstance(ids, list) or len(ids)>100 or any(not isinstance(i, str) or i not in known for i in ids) or len(ids)!=len(set(ids)):
                raise ValueError('Select known account windows')
        if 'dismiss' in payload and payload['dismiss'] not in ('later', 'never', 'shown'):
            raise ValueError('Invalid dismissal')
        with self.journal.transaction() as data:
            prefs = data['preferences']
            reset_suggestions = payload.get('suggestions') is True and not prefs['suggestions']
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
            if reset_suggestions:
                prefs['snoozeUntil'] = 0
                data['suggested'] = []
            self.journal.save(data)
        with self.lock:
            self.view['preferences'] = copy.deepcopy(prefs)

    def _observe(self, row, now):
        row = target(row)
        key = row['activationKey']
        state, reset = classify(row, now)
        old = self.observations.get(key, {})
        read = row.get('lastSuccess')
        source = row.get('source')
        if state not in ('inactive', 'running', 'full_deadline'):
            self.observations[key] = dict(state=state, last=read, source=source, count=0)
            return self.observations[key]
        if old.get('last') == read and old.get('source') == source:
            return old
        raw_state = state
        if state == 'full_deadline':
            elapsed = read-old.get('last', read) if number(old.get('last')) else 0
            if (old.get('source') == source and old.get('raw') == 'full_deadline' and
                    30 <= elapsed <= MAX_GAP and number(old.get('reset'))):
                movement = reset-old['reset']
                state = 'inactive' if abs(movement-elapsed) <= 15 else 'running' if abs(movement) <= 15 else 'unknown'
        barrier = self.barriers.get(key)
        continuous = (old.get('source') == source and number(old.get('last')) and
                      0 < read-old['last'] <= MAX_GAP and (old.get('state') == state or
                      (old.get('state') == 'full_deadline' and state in ('inactive', 'running'))) and
                      not (number(barrier) and old.get('since', read) <= barrier < read))
        if state == 'running':
            continuous = continuous and old.get('reset') is not None and abs(old['reset']-reset)<=15
        obs = dict(state=state, raw=raw_state, source=source, last=read, reset=reset,
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
        rows = [t for a in snapshot['accounts'] for t in targets(a)]
        if now >= self.next_probe:
            self.next_probe = now+300
            for provider in {a['provider'] for a in rows}:
                try:
                    self.capabilities[provider] = self.runners[provider].capability()
                except Exception:
                    self.capabilities[provider] = False, 'Activation runner is unavailable.'
        visible = {a['activationKey'] for a in rows}
        self.observations = {k: v for k, v in self.observations.items() if k in visible}
        candidate = None
        with self.journal.transaction() as data:
            prefs = data['preferences']
            dirty = self._expire(data, now)
            summaries = []
            suggestion = None
            for row in rows:
                if row['provider'] not in ('claude', 'codex', 'antigravity'):
                    continue
                key = row['activationKey']
                obs = self._observe(row, now)
                attempt = data['attempts'].get(key)
                if attempt and attempt['state'] in PENDING and obs['state']=='running' and obs.get('count',0)>=2:
                    # Confirmation cannot use the reads from before dispatch.
                    if obs['since']>attempt['at'] and abs(obs['reset']-attempt['at']-row['activationSeconds'])<=TOLERANCE:
                        attempt.update(state='confirmed', reset=obs['reset'])
                        for receipt in data['history']:
                            if receipt.get('id') == attempt['id'] and receipt.get('window') == attempt['window']:
                                receipt.update(attempt)
                        dirty = True
                supported, reason = self.capabilities[row['provider']]
                supported = supported and obs['state'] != 'unsupported'
                states = {'inactive':'Waiting for the inactivity delay.', 'running':'Window is already running.',
                          'stale':'Waiting for fresh provider readings.', 'billing_unknown':'Extra usage must be off and reported by the provider.',
                          'unavailable':'Subscription allowance is unavailable.', 'unknown':'Window state is not established.', 'unsupported':'Activation is not supported for this source.', 'full_deadline':'Waiting for a second fresh reading to distinguish an idle window.'}
                status = states.get(obs['state'], reason)
                eligible = supported and obs['state']=='inactive' and obs.get('count',0)>=2
                waited = max(0, obs.get('last',now)-obs.get('since',now))
                if attempt:
                    status = {'reserved':'A prompt may have been sent. No automatic retry.', 'sent':'Prompt sent. Waiting for two fresh countdown readings.',
                              'uncertain':'Delivery is uncertain. No automatic retry.', 'confirmed':'Last activation confirmed.',
                              'expired':'The last prompt was never confirmed and its window has ended. Waiting for new inactivity.'}.get(attempt['state'], status)
                    eligible = eligible and self._released(attempt, obs.get('since', 0))
                eligible = eligible and not self._group_pending(data, row, key)
                if eligible and not prefs['enabled'] and prefs['suggestions'] and not prefs['dismissed'] and now>=prefs['snoozeUntil'] and waited>=10800 and key not in data['suggested']:
                    suggestion = dict(accountId=key, label=row.get('label',row['provider'])+' · '+row['activationLabel'], message='This quota window is waiting to start. AQM can send a short prompt so its reset countdown begins earlier. This uses subscription allowance.')
                if key in self.preflight_failures:
                    status = 'Subscription identity could not be verified. No prompt sent.'
                if (eligible and prefs['enabled'] and key in prefs['accounts'] and waited>=prefs['idleMinutes']*60 and candidate is None and now>=self.preflight_failures.get(key,0)):
                    if self._recent(data, row, now)<5:
                        candidate = row
                    else:
                        status = 'Daily activation limit reached.'
                summaries.append(dict(accountId=key,stableAccountId=row['id'],label=row.get('label',row['id']),displayLabel=row['activationLabel'],provider=row['provider'],supported=supported,
                                      reason=reason,status=status,window=row['activationWindow'],group=row['activationGroup'],
                                      suggestionEligible=eligible and waited>=10800,
                                      selected=key in prefs['accounts'],lastAttempt=copy.deepcopy(attempt)))
            if dirty:
                self.journal.save(data)
            with self.lock:
                self.view = dict(preferences=copy.deepcopy(prefs),accounts=summaries,suggestion=suggestion)
                self.error = False
        if candidate and not self.stop.is_set():
            self._dispatch(candidate)

    @staticmethod
    def _group_pending(data, row, exclude=None):
        for key, receipt in data['attempts'].items():
            if key == exclude:
                continue
            account = receipt.get('accountId', key)
            group = receipt.get('group', 'direct')
            if account == row['id'] and group == row['activationGroup'] and receipt['state'] in PENDING:
                return True
        return False

    @staticmethod
    def _released(attempt, since):
        """A new streak must begin after the last window this receipt could have started."""
        if not attempt:
            return True
        end = {'confirmed': attempt.get('reset'), 'expired': attempt.get('expired')}.get(attempt['state'])
        return number(end) and number(since) and since > end

    def _expire(self, data, now):
        # An unconfirmed prompt can no longer own a live window once the latest
        # deadline it could have produced has passed. Only then may it stop blocking.
        changed = False
        for receipt in data['attempts'].values():
            end = receipt['at']+SECONDS[receipt['window']]+TOLERANCE
            if receipt['state'] in PENDING and now > end:
                receipt.update(state='expired', expired=end)
                for entry in data['history']:
                    if entry.get('id') == receipt['id'] and entry.get('window') == receipt['window']:
                        entry.update(receipt)
                changed = True
        # Inactivity evidence never spans an expiry, so the next streak starts after it.
        self.barriers = {k: r['expired'] for k, r in data['attempts'].items() if r['state'] == 'expired'}
        return changed

    @staticmethod
    def _recent(data, row, now):
        return len({h.get('id', str(i)) for i, h in enumerate(data['history'])
                    if h['accountId'] == row['id'] and h.get('group', 'direct') == row['activationGroup'] and now-h['at'] < 86400})

    def _dispatch(self, row):
        row = target(row)
        key = row['activationKey']
        runner = self.runners[row['provider']]
        # Identity checks may take time. Revalidate snapshot and settings afterward.
        try:
            executable = runner.prepare(row)
            self.preflight_failures.pop(key, None)
        except Exception:
            self.preflight_failures[key] = self.clock()+300
            return
        if self.stop.is_set():
            return
        now = self.clock()
        latest = next((t for a in self.monitor.snapshot()['accounts'] for t in targets(a) if t['activationKey']==key), None)
        if latest is None or latest['source']!=row['source']:
            return
        if not 0 <= latest['lastSuccess']-row['lastSuccess'] <= MAX_GAP:
            return
        for sibling in targets(latest):
            self._observe(sibling, now)
        if self.observations[key]['state'] != 'inactive':
            return
        with self.journal.transaction() as data:
            prefs = data['preferences']
            obs = self.observations[key]
            if not prefs['enabled'] or key not in prefs['accounts'] or obs['last']-obs['since']<prefs['idleMinutes']*60:
                return
            if not self._released(data['attempts'].get(key), obs['since']):
                return
            if self._recent(data, row, now)>=5 or self._group_pending(data, row, key):
                return
            batch = uuid.uuid4().hex
            reserved, priors = [], {}
            # One prompt can start both windows in this group. Reserve both observed
            # inactive windows, even if only one is selected, without opting the other in.
            for sibling in targets(latest):
                sibling_key = sibling['activationKey']
                sibling_obs = self.observations.get(sibling_key, {})
                if (sibling['activationGroup'] != row['activationGroup'] or
                        sibling_obs.get('state') != 'inactive' or sibling_obs.get('count', 0) < 2):
                    continue
                prior = data['attempts'].get(sibling_key)
                if not self._released(prior, sibling_obs['since']):
                    return
                priors[sibling_key] = copy.deepcopy(prior)
                receipt = dict(id=batch, state='reserved', at=now, episode=sibling_obs['episode'],
                               window=sibling['activationWindow'], group=row['activationGroup'], accountId=row['id'])
                data['attempts'][sibling_key] = receipt
                data['history'].append(copy.deepcopy(receipt))
                reserved.append(sibling_key)
            if key not in reserved:
                return
            attempt = data['attempts'][key]
            self.journal.save(data)
        if self.stop.is_set():
            self._release(attempt['id'], priors)
            return
        with self.lock:
            self.busy=True
        state, usage = 'uncertain', {}
        try:
            # No inference fallback and no resend, including after a timeout.
            usage = runner.send(executable)
            state = 'sent'
        except NotLaunched:
            # The runner proved that no process started, so nothing was delivered.
            state = None
        except Exception:
            pass
        finally:
            with self.lock:
                self.busy=False
        if state is None:
            self.preflight_failures[key] = self.clock()+300
            self._release(attempt['id'], priors)
            return
        with self.journal.transaction() as data:
            if data['attempts'][key]['id']==attempt['id']:
                for reserved_key in reserved:
                    if data['attempts'][reserved_key]['state'] == 'reserved':
                        data['attempts'][reserved_key].update(state=state,usage=usage)
                for receipt in data['history']:
                    if receipt.get('id') == attempt['id'] and receipt.get('state') == 'reserved':
                        receipt.update(state=state,usage=usage)
                self.journal.save(data)

    def _release(self, batch, priors):
        """Undo a reservation whose prompt never started. It does not count toward the daily limit."""
        with self.journal.transaction() as data:
            for key, prior in priors.items():
                current = data['attempts'].get(key)
                if not current or current['id'] != batch or current['state'] != 'reserved':
                    continue
                if prior:
                    data['attempts'][key] = prior
                else:
                    del data['attempts'][key]
            data['history'] = [h for h in data['history'] if not (h.get('id') == batch and h.get('state') == 'reserved')]
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
