import copy
import threading
import time
from . import providers, model
from .copilot import copilot_account

INTERVAL = 300
SUPPORTED_PROVIDERS = ('codex', 'claude', 'antigravity', 'copilot')


class Monitor:
    def __init__(self, vault, clock=time.time, desired_google=(), settings_vault=None):
        self.vault, self.clock = vault, clock
        self.settings_vault = settings_vault
        self.hidden = set()
        self.order = []
        self.enabled = None
        if settings_vault:
            preferences = settings_vault.load()
            self.hidden = set(preferences.get('hiddenAccountIds', []))
            self.order = preferences.get('accountOrder', [])
            configured = preferences.get('enabledProviders')
            if isinstance(configured, list):
                self.enabled = set(configured) & set(SUPPORTED_PROVIDERS)
        self.desired_google = tuple(desired_google)
        self.lock = threading.RLock()
        self.refresh_lock = threading.Lock()
        self.storage_error = False
        try:
            self.rows = vault.load()
            # Early preview snapshots included product attribution as a quota.
            for row in self.rows.values():
                if row.get('provider') == 'claude':
                    for group in row.get('groups', []):
                        group['buckets'] = [b for b in group.get('buckets', []) if not b.get('id', '').endswith('_breakdown')]
        except Exception:
            self.rows = {}
            self.storage_error = True
        self.next_discovery = 0

    def set_providers(self, enabled):
        if not isinstance(enabled, list) or any(not isinstance(p, str) or p not in SUPPORTED_PROVIDERS for p in enabled):
            raise ValueError('Invalid provider selection')
        with self.lock:
            if self.settings_vault:
                settings = self.settings_vault.load()
                settings['enabledProviders'] = sorted(set(enabled))
                self.settings_vault.save(settings)
            self.enabled = set(enabled)
            self.next_discovery = 0

    def _save_hidden(self, hidden):
        if self.settings_vault:
            settings = self.settings_vault.load()
            settings['hiddenAccountIds'] = sorted(hidden)
            self.settings_vault.save(settings)
        self.hidden = hidden

    def remove_account(self, account_id):
        with self.lock:
            row = next((a for a in self.snapshot()['accounts'] if a['id'] == account_id), None)
            if row is None:
                return False
            hidden = self.hidden | {account_id}
            if row['provider'] == 'antigravity':
                # A requested-connection placeholder must not replace the removed card.
                hidden.add(model.identity('requested-google', row['label']))
            self._save_hidden(hidden)
            return True

    def restore_accounts(self):
        with self.lock:
            self._save_hidden(set())

    def save_layout(self, order, removed):
        if not isinstance(order, list) or not isinstance(removed, list):
            raise ValueError('Invalid layout')
        if any(not isinstance(key, str) for key in order + removed):
            raise ValueError('Invalid account ID')
        if len(set(order)) != len(order) or len(set(removed)) != len(removed):
            raise ValueError('Duplicate account ID')
        with self.lock:
            visible = {a['id']: a for a in self.snapshot()['accounts']}
            if set(order) & set(removed) or set(order) | set(removed) != set(visible):
                return False
            hidden = self.hidden | set(removed)
            for key in removed:
                row = visible[key]
                if row['provider'] == 'antigravity':
                    hidden.add(model.identity('requested-google', row['label']))
            if self.settings_vault:
                settings = self.settings_vault.load()
                settings.update(hiddenAccountIds=sorted(hidden), accountOrder=order)
                self.settings_vault.save(settings)
            self.hidden, self.order = hidden, list(order)
            return True

    def refresh_one(self, account):
        now = self.clock()
        key = account['id']
        with self.lock:
            if key in self.hidden or (self.enabled is not None and account['provider'] not in self.enabled):
                return
            old = copy.deepcopy(self.rows.get(key, {}))
        renewed = old.get('error') == 'sign_in_required' and account.get('sessionRevision') != old.get('sessionRevision')
        if not renewed and now < old.get('nextAttempt', 0):
            return
        row = {k: v for k, v in account.items() if k != 'read'}
        row.update(groups=old.get('groups', []), lastSuccess=old.get('lastSuccess'), lastAttempt=now)
        try:
            groups, label = account['read']()
            if not groups or not any(b.get('remaining') is not None or b.get('unlimited') is True or b.get('amountRemaining') is not None or b.get('entitlement') is not None for g in groups for b in g['buckets']):
                raise providers.ReadError('quota_not_reported')
            row.update(groups=groups, label=label, lastSuccess=now, status='live', error=None,
                       failures=0, nextAttempt=now+INTERVAL)
        except Exception as err:
            code = err.code if isinstance(err, providers.ReadError) else 'reader_failed'
            failures = old.get('failures', 0)+1
            delay = max(min(3600, INTERVAL * 2 ** min(failures-1, 4)), getattr(err, 'retry_after', 0))
            row.update(status='stale' if row['lastSuccess'] else 'pending', error=code, failures=failures,
                       nextAttempt=now+delay)
        with self.lock:
            self.rows[key] = row

    def refresh(self):
        if not self.refresh_lock.acquire(blocking=False):
            return False
        try:
            now = self.clock()
            if now < self.next_discovery:
                return False
            self.next_discovery = now + 60
            found = []
            discovery_errors = {}
            for provider, fn in [('codex', providers.codex_account), ('claude', providers.claude_account), ('antigravity', providers.antigravity_accounts), ('copilot', copilot_account)]:
                if self.enabled is not None and provider not in self.enabled:
                    continue
                try:
                    result = fn()
                    found.extend(result if isinstance(result, list) else [result])
                except Exception as err:
                    discovery_errors[provider] = err.code if isinstance(err, providers.ReadError) else 'local_discovery_failed'
            ids = {a['id'] for a in found}
            with self.lock:
                for key, row in list(self.rows.items()):
                    if key not in ids:
                        row.update(status='stale' if row.get('lastSuccess') else 'pending',
                                   error=discovery_errors.get(row['provider'], 'local_session_unavailable'))
                for provider in ('codex', 'claude', 'antigravity', 'copilot'):
                    placeholder = provider + '-pending'
                    if any(a['provider'] == provider for a in found):
                        self.rows.pop(placeholder, None)
                    elif self.enabled is not None and provider in self.enabled and not any(r['provider'] == provider for r in self.rows.values()):
                        self.rows[placeholder] = dict(id=placeholder, provider=provider, label='Sign-in needed', source='No readable official session', identityStatus='Unverified', groups=[], status='pending', error=discovery_errors.get(provider, 'local_session_unavailable'), lastSuccess=None)
            for account in {a['id']: a for a in found}.values():
                self.refresh_one(account)
            with self.lock:
                try:
                    self.vault.save(self.rows)
                    self.storage_error = False
                except Exception:
                    self.storage_error = True
            return True
        finally:
            self.refresh_lock.release()

    def snapshot(self):
        with self.lock:
            rows = copy.deepcopy(list(self.rows.values()))
            hidden = self.hidden.copy()
            order = {key: i for i, key in enumerate(self.order)}
            enabled = self.enabled.copy() if self.enabled is not None else None
        now = self.clock()
        # Keep the established CLI card on failures rather than revive desktop duplicates.
        # Legacy rows and pins remain on disk without merging identities by email.
        if any(r['provider'] == 'antigravity' and r.get('source', '').startswith('Official Antigravity CLI')
               and r.get('lastSuccess') and r['id'] not in hidden for r in rows):
            rows = [r for r in rows if r.get('source') != 'Official running Antigravity local service']
        for label in self.desired_google:
            # This is a requested connection, not a verified or merged account.
            if not any(r['provider'] == 'antigravity' and (r['label'] == label or r.get('accountEmail') == label) for r in rows):
                rows.append(dict(id=model.identity('requested-google', label), provider='antigravity', label=label,
                                 source='Requested Google account', identityStatus='Not connected; identity unverified',
                                 groups=[], status='pending', error='independent_sign_in_needed', lastSuccess=None))
        rows = [row for row in rows if row['id'] not in hidden and
                (row['provider'] in enabled if enabled is not None else not row['id'].endswith('-pending'))]
        for row in rows:
            age = now-row['lastSuccess'] if row.get('lastSuccess') else None
            row['ageSeconds'] = age
            if row['status'] == 'live' and (age is None or age > INTERVAL*2):
                row['status'] = 'stale'
        return dict(accounts=sorted(rows, key=lambda r: (order.get(r['id'], len(order)), r['provider'], r['id'])), now=now,
                    hasRemovedAccounts=bool(hidden),
                    supportedProviders=list(SUPPORTED_PROVIDERS),
                    enabledProviders=sorted(enabled if enabled is not None else {r['provider'] for r in rows}),
                    refreshing=self.refresh_lock.locked(), pollSeconds=INTERVAL, storageError=self.storage_error,
                    googleVerifiedCount=0, googleRequestedCount=len(self.desired_google), googleSessionCount=sum(r['provider']=='antigravity' and r['status']=='live' for r in rows))
