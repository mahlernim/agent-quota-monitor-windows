import copy
from contextlib import contextmanager
import json
import os
from pathlib import Path
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer
from unittest.mock import patch

from quota.activation import Activation, ClaudeRunner, DEFAULTS, Journal, classify
from quota import model
from quota.server import handler
from quota.vault import crypt


class MemoryJournal:
    def __init__(self):
        self.data = dict(version=1, preferences=copy.deepcopy(DEFAULTS), attempts={}, history=[], suggested=[])
        self.lock = threading.RLock()
        self.fail = False

    @contextmanager
    def transaction(self):
        with self.lock:
            yield copy.deepcopy(self.data)

    def save(self, value):
        if self.fail:
            raise OSError('Synthetic storage failure')
        Journal.validate(value)
        self.data = copy.deepcopy(value)


class Runner:
    def __init__(self):
        self.sends = 0
        self.ready = True
        self.before = lambda: None
        self.fail = False

    def capability(self): return self.ready, 'Synthetic runner'
    def prepare(self, row):
        self.before()
        return 'synthetic'
    def send(self, executable):
        self.sends += 1
        if self.fail:
            raise TimeoutError('Synthetic uncertain delivery')
        return dict(input_tokens=1, output_tokens=1)


class Monitor:
    def __init__(self, row): self.rows = [row]
    def snapshot(self): return dict(accounts=copy.deepcopy(self.rows))


def row(now=1700000000, subject='account-a'):
    return dict(id=model.identity('claude', subject), provider='claude', label='same@example.test',
                source='Official Claude Code session / OAuth usage endpoint', status='live', error=None,
                lastSuccess=now, groups=model.claude(dict(
                    five_hour=dict(utilization=0, resets_at=None),
                    seven_day=dict(utilization=20, resets_at=model.timestamp(now+604800)),
                    extra_usage=dict(is_enabled=False))))


class ActivationTests(unittest.TestCase):
    def setUp(self):
        self.now = 1700000000
        self.mono = self.now
        self.monitor = Monitor(row(self.now))
        self.journal, self.runner = MemoryJournal(), Runner()
        self.engine = self.new_engine()

    def new_engine(self):
        return Activation(self.monitor, self.journal, self.runner, lambda:self.now, lambda:self.mono)

    def tick(self, seconds=300, fresh=True):
        self.now += seconds
        self.mono += seconds
        if fresh:
            for item in self.monitor.rows: item['lastSuccess'] = self.now
        self.engine.tick()

    def enable(self):
        self.engine.configure(dict(enabled=True, accounts=[self.monitor.rows[0]['id']]))

    def mature(self, count=6):
        self.engine.tick()
        for _ in range(count): self.tick()

    def test_off_by_default_and_new_accounts_never_inherit_opt_in(self):
        self.mature(36)
        self.assertEqual(self.runner.sends, 0)
        self.enable()
        original = self.monitor.rows[0]['id']
        self.monitor.rows = [row(self.now, 'new-account')]
        self.mature()
        self.assertEqual(self.runner.sends, 0)
        self.assertEqual(self.journal.data['preferences']['accounts'], [original])
        self.assertEqual(self.new_engine().journal.data['preferences'], self.journal.data['preferences'])

    def test_one_prompt_after_continuous_delay_and_never_for_repeated_cache(self):
        self.enable()
        self.engine.tick()
        for _ in range(6): self.tick(fresh=False)
        self.assertEqual(self.runner.sends, 0)
        self.tick()
        self.mature(5)
        self.assertEqual(self.runner.sends, 0)
        self.tick()
        self.assertEqual(self.runner.sends, 1)
        self.mature(12)
        self.assertEqual(self.runner.sends, 1)

    def test_failure_source_change_clock_jump_and_sleep_break_streak(self):
        for kind in ('failure', 'source', 'clock', 'sleep'):
            with self.subTest(kind=kind):
                self.setUp(); self.enable(); self.mature(5)
                if kind == 'failure':
                    self.monitor.rows[0]['error'] = 'rate_limited'; self.tick()
                    self.monitor.rows[0]['error'] = None
                elif kind == 'source':
                    self.monitor.rows[0]['source'] = 'unknown'; self.tick()
                    self.monitor.rows[0]['source'] = row()['source']
                elif kind == 'clock': self.now += 120
                else: self.tick(1000)
                self.tick()
                self.assertEqual(self.runner.sends, 0)

    def test_fixed_full_window_and_unknown_quota_never_activate(self):
        mutations = [lambda a:a['groups'][0]['buckets'][0].update(resetsAt=model.timestamp(self.now+18000)),
                     lambda a:a['groups'][0]['buckets'][0].update(remaining=99.999999),
                     lambda a:a['groups'][0]['buckets'][1].update(remaining=0),
                     lambda a:a['groups'][0]['buckets'][0].update(disabled=True),
                     lambda a:a['groups'][0].update(extraUsageEnabled=True),
                     lambda a:a['groups'][0].update(extraUsageEnabled=None),
                     lambda a:a.update(provider='antigravity'),
                     lambda a:a.update(provider='codex')]
        for mutate in mutations:
            self.setUp(); self.enable(); mutate(self.monitor.rows[0]); self.mature()
            self.assertEqual(self.runner.sends, 0)

    def test_missing_or_malformed_raw_reset_is_not_inactive(self):
        for raw in ({'utilization':0}, {'utilization':0,'resets_at':'bad'}, {'utilization':False,'resets_at':None}):
            sample = row(self.now)
            sample['groups'][0]['buckets'][0] = model.claude({'five_hour':raw})[0]['buckets'][0]
            self.assertNotEqual(classify(sample,self.now)[0], 'inactive')

    def test_disabled_runner_no_send_and_same_email_different_identity_isolated(self):
        self.enable(); self.runner.ready = False; self.mature()
        self.assertEqual(self.runner.sends, 0)
        self.runner.ready = True
        self.monitor.rows.append(row(self.now, 'account-b'))
        self.tick(); self.mature()
        self.assertEqual(self.runner.sends, 1)
        self.assertEqual(list(self.journal.data['attempts']), [self.monitor.rows[0]['id']])

    def test_uncertain_delivery_persists_across_restart_and_duplicate_engines(self):
        self.enable(); self.runner.fail = True; self.mature()
        self.assertEqual(next(iter(self.journal.data['attempts'].values()))['state'], 'uncertain')
        self.runner.fail = False
        self.engine = self.new_engine(); self.mature(12)
        self.assertEqual(self.runner.sends, 1)
        other = self.new_engine()
        other.observations = copy.deepcopy(self.engine.observations)
        other._dispatch(self.monitor.rows[0])
        self.assertEqual(self.runner.sends, 1)

    def test_reservation_precedes_send_and_storage_failure_blocks(self):
        self.enable(); self.mature(5)
        self.journal.fail = True
        with self.assertRaises(OSError): self.tick()
        self.assertEqual(self.runner.sends, 0)
        self.journal.fail = False
        original = self.runner.send
        def send(exe):
            self.assertEqual(next(iter(self.journal.data['attempts'].values()))['state'], 'reserved')
            return original(exe)
        self.runner.send = send
        self.tick()
        self.assertEqual(self.runner.sends, 1)

    def test_disable_or_identity_failure_during_preflight_blocks(self):
        self.enable()
        self.runner.before = lambda:self.engine.configure({'enabled':False})
        self.mature()
        self.assertEqual(self.runner.sends, 0)
        self.enable()
        def fail(): raise ValueError('Synthetic account mismatch')
        self.runner.before = fail
        self.tick()
        self.assertEqual(self.runner.sends, 0)
        self.assertFalse(self.engine.snapshot()['storageError'])
        self.assertEqual(self.journal.data['attempts'], {})

    def test_stop_during_preflight_never_sends(self):
        self.enable(); self.runner.before = self.engine.close; self.mature()
        self.assertEqual(self.runner.sends, 0)

    def test_daily_cap_and_changed_latest_read_block_dispatch(self):
        self.enable()
        self.journal.data['history'] = [dict(accountId=self.monitor.rows[0]['id'],at=self.now-60) for _ in range(5)]
        self.mature()
        self.assertEqual(self.runner.sends, 0)
        self.journal.data['history'] = []
        self.runner.before = lambda:self.monitor.rows[0].update(status='stale')
        self.tick()
        self.assertEqual(self.runner.sends, 0)

    def test_two_post_reads_confirm_and_next_cycle_requires_new_delay(self):
        self.enable(); self.mature()
        reset = self.now+18000
        five = self.monitor.rows[0]['groups'][0]['buckets'][0]
        five.update(resetsAt=model.timestamp(reset))
        self.tick()
        attempt = lambda:next(iter(self.journal.data['attempts'].values()))
        self.assertEqual(attempt()['state'], 'sent')
        self.tick(fresh=False)
        self.assertEqual(attempt()['state'], 'sent')
        self.tick()
        self.assertEqual(attempt()['state'], 'confirmed')
        self.tick(reset-self.now+1)
        five.update(resetsAt=None)
        self.tick(); self.mature(5)
        self.assertEqual(self.runner.sends, 1)
        self.tick()
        self.assertEqual(self.runner.sends, 2)
        self.assertEqual(len(self.journal.data['history']),2)
        self.assertEqual(self.journal.data['history'][0]['state'],'confirmed')
        self.assertEqual(self.journal.data['history'][1]['state'],'sent')

    def test_three_hour_suggestion_snooze_dismissal_and_restart(self):
        self.mature(35)
        self.assertIsNone(self.engine.snapshot()['suggestion'])
        self.tick()
        self.assertIsNotNone(self.engine.snapshot()['suggestion'])
        self.engine.configure({'dismiss':'shown'}); self.tick()
        self.assertIsNone(self.engine.snapshot()['suggestion'])
        self.engine.configure({'dismiss':'later'})
        self.assertEqual(self.journal.data['preferences']['snoozeUntil'], self.now+7*86400)
        self.engine = self.new_engine(); self.mature(36)
        self.assertIsNone(self.engine.snapshot()['suggestion'])
        self.tick(7*86400); self.mature(36)
        self.assertIsNotNone(self.engine.snapshot()['suggestion'])
        self.engine.configure({'dismiss':'never'})
        self.engine = self.new_engine(); self.mature(36)
        self.assertIsNone(self.engine.snapshot()['suggestion'])
        self.assertEqual(self.runner.sends, 0)
        self.engine.configure({'suggestions':True})
        self.tick()
        self.assertIsNotNone(self.engine.snapshot()['suggestion'])
        self.assertFalse(self.journal.data['preferences']['enabled'])

    def test_invalid_preferences_never_change_existing_settings(self):
        for bad in ({'enabled':'yes'}, {'accounts':['unknown']}, {'idleMinutes':0}, {'dismiss':'retry'}, {'model':'expensive'}):
            with self.assertRaises(ValueError): self.engine.configure(bad)
            self.assertEqual(self.journal.data['preferences'], DEFAULTS)


class RunnerTests(unittest.TestCase):
    def test_only_verified_version_and_subscription_identity_allowed(self):
        runner = ClaudeRunner()
        with patch('quota.activation.connections.client_command', return_value=['synthetic.exe']), patch('quota.activation.claude_cli.run', return_value=(0,'2.1.281 (Claude Code)')):
            self.assertFalse(runner.capability()[0])
        metadata = dict(accountUuid='account-a', organizationUuid='org-a', emailAddress='same@example.test')
        with patch.object(runner,'capability',return_value=(True,'')), patch('quota.activation.connections.client_command',return_value=['synthetic.exe']), patch('quota.activation.providers.load',return_value={'oauthAccount':metadata}), patch('quota.activation.claude_cli.auth_status', return_value=dict(orgId='org-b',email='same@example.test',subscriptionType='pro')):
            with self.assertRaises(ValueError): runner.prepare(row())

    def test_prompt_restrictions_fixed_model_and_no_output_retention(self):
        result = dict(is_error=False,num_turns=1,usage=dict(input_tokens=5,output_tokens=1,private='must not retain'),result='private')
        with patch('quota.activation.claude_cli.run',return_value=(0,json.dumps(result))) as run:
            self.assertEqual(ClaudeRunner().send('synthetic.exe'),dict(input_tokens=5,output_tokens=1))
        args = run.call_args.args[1]
        self.assertIn('--safe-mode',args)
        self.assertIn('--strict-mcp-config',args)
        self.assertIn('--no-session-persistence',args)
        self.assertEqual(args[args.index('--model')+1], 'claude-haiku-4-5-20251001')
        self.assertEqual(args[args.index('--tools')+1], '')
        self.assertEqual(run.call_args.kwargs['timeout'],90)


class ActivationApiTests(unittest.TestCase):
    def test_loopback_origin_validation_and_persistent_choices(self):
        journal, monitor = MemoryJournal(), Monitor(row())
        engine = Activation(monitor,journal,Runner())
        server = ThreadingHTTPServer(('127.0.0.1',0),handler(monitor,0))
        port = server.server_address[1]
        server.RequestHandlerClass = handler(monitor,port,activation=engine)
        threading.Thread(target=server.serve_forever,daemon=True).start()
        base = f'http://127.0.0.1:{port}'
        def post(body, headers):
            request = urllib.request.Request(base+'/api/activation',data=json.dumps(body).encode(),headers=headers)
            try:
                with urllib.request.urlopen(request) as response: return response.status
            except urllib.error.HTTPError as response: return response.code
        headers = {'Content-Type':'application/json','Origin':base,'X-Quota-Request':'refresh'}
        try:
            self.assertEqual(post({'enabled':True},{}),403)
            self.assertEqual(post({'enabled':True},dict(headers,Origin='https://example.test')),403)
            self.assertEqual(post({'enabled':'true'},headers),400)
            self.assertEqual(post({'enabled':True,'accounts':[monitor.rows[0]['id']]},headers),200)
            with urllib.request.urlopen(base+'/api/status') as response:
                self.assertTrue(json.load(response)['activation']['preferences']['enabled'])
            journal.fail=True
            self.assertEqual(post({'enabled':False},headers),503)
            self.assertTrue(journal.data['preferences']['enabled'])
        finally:
            server.shutdown(); server.server_close()


@unittest.skipUnless(os.name=='nt','Windows DPAPI and file locks')
class DurableJournalTests(unittest.TestCase):
    def test_encrypted_roundtrip_lock_and_corruption_fail_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)/'activation.dpapi'
            first, second = Journal(path), Journal(path)
            with first.transaction() as value:
                value['preferences'].update(enabled=True,accounts=['synthetic-account'])
                first.save(value)
                with self.assertRaises(OSError):
                    with second.transaction(): pass
            self.assertNotIn(b'synthetic-account', path.read_bytes())
            with second.transaction() as value:
                self.assertTrue(value['preferences']['enabled'])
                self.assertEqual(value['preferences']['accounts'],['synthetic-account'])
            # A truncated but parseable document must not erase the duplicate guard.
            del value['attempts']
            path.write_bytes(crypt(json.dumps(value).encode()))
            with self.assertRaises(Exception):
                with second.transaction(): pass
            path.write_bytes(b'corrupt')
            with self.assertRaises(Exception):
                with second.transaction(): pass
            self.assertEqual(path.read_bytes(),b'corrupt')

    def test_malformed_stored_preferences_cannot_enable_dispatch(self):
        data = MemoryJournal().data
        for key, value in (('enabled','true'),('idleMinutes',-1),('accounts',None)):
            malformed = copy.deepcopy(data)
            malformed['preferences'][key] = value
            with self.assertRaises(ValueError): Journal.validate(malformed)


if __name__ == '__main__': unittest.main()
