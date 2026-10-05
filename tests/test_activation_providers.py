import copy
import json
import unittest
from unittest.mock import patch
from quota import model
from quota.activation import Activation, Journal
from quota.activation_windows import AG_SOURCE, CODEX_SOURCE, classify, targets
from quota.activation_runners import AntigravityRunner, CodexRunner, environment
from tests.test_activation import MemoryJournal, Monitor, Runner, row


def antigravity(now, subject='account-a'):
    return dict(id=model.identity('antigravity', subject), provider='antigravity', label='same@example.test',
                source=AG_SOURCE, status='live', error=None, lastSuccess=now,
                groups=[dict(id=g, buckets=[model.bucket(w, w, 100, seconds, now+seconds)
                       for w, seconds in [('5h', 18000), ('weekly', 604800)]]) for g in ('gemini', 'claude-gpt')])


def codex(now):
    return dict(id=model.identity('codex', 'account-a'), provider='codex', label='same@example.test',
                source=CODEX_SOURCE, status='live', error=None, lastSuccess=now,
                groups=model.codex({'rate_limit': {'primary_window':
                    dict(used_percent=0, limit_window_seconds=604800, reset_at=now+604800)}}))


class ProviderActivationTests(unittest.TestCase):
    def setup(self, provider):
        self.now = 1700000000
        self.monitor = Monitor(provider(self.now))
        self.journal, self.runner = MemoryJournal(), Runner()
        self.engine = Activation(self.monitor, self.journal, self.runner, lambda:self.now, lambda:self.now)

    def enable(self, indices):
        self.engine.configure(dict(enabled=True, accounts=[targets(self.monitor.rows[0])[i]['activationKey'] for i in indices]))

    def tick(self, seconds=300, moving=True, fresh=True):
        self.now += seconds
        for account in self.monitor.rows:
            if fresh:
                account['lastSuccess'] = self.now
                if moving:
                    for group in account['groups']:
                        for bucket in group['buckets']:
                            bucket['resetsAt'] = model.timestamp(self.now+bucket['windowSeconds'])
        self.engine.tick()

    def mature(self):
        self.engine.tick()
        for _ in range(6): self.tick()

    def test_group_selections_and_old_claude_choices_do_not_expand(self):
        self.setup(row)
        self.enable([0])
        self.monitor.rows.extend([antigravity(self.now), codex(self.now)])
        self.engine.tick()
        view = self.engine.snapshot()
        self.assertEqual(len(view['accounts']), 7)
        self.assertEqual(sum(a['selected'] for a in view['accounts']), 1)
        self.assertEqual(view['preferences']['accounts'], [self.monitor.rows[0]['id']])
        self.assertEqual([a['window'] for a in view['accounts'] if a['provider']=='codex'], ['weekly'])

    def test_antigravity_groups_run_separately_and_windows_coalesce(self):
        self.setup(antigravity)
        self.enable([0, 1, 2, 3])
        self.mature()
        self.assertEqual(self.runner.sends, 1)
        receipts = self.journal.data['attempts']
        self.assertEqual(len(receipts), 2)
        self.assertEqual({r['group'] for r in receipts.values()}, {'gemini'})
        self.assertEqual(len({r['id'] for r in receipts.values()}), 1)
        self.tick()
        self.assertEqual(self.runner.sends, 2)
        self.assertEqual(len(self.journal.data['attempts']), 4)
        self.tick()
        self.assertEqual(self.runner.sends, 2)

    def test_codex_weekly_needs_moving_deadlines_not_rounded_full_quota(self):
        self.setup(codex)
        self.enable([0])
        self.engine.tick()
        for _ in range(8): self.tick(moving=False)
        self.assertEqual(self.runner.sends, 0)
        self.monitor.rows[0]['groups'][0]['buckets'][0]['remaining'] = 99.999999
        for _ in range(8): self.tick()
        self.assertEqual(self.runner.sends, 0)
        self.monitor.rows[0]['groups'][0]['buckets'][0]['remaining'] = 100
        for _ in range(8): self.tick()
        self.assertEqual(self.runner.sends, 1)

    def test_duplicate_cache_source_failure_and_long_latency_break_evidence(self):
        self.setup(antigravity)
        self.enable([0])
        self.engine.tick()
        for _ in range(3): self.tick()
        self.tick(fresh=False)
        self.monitor.rows[0]['error'] = 'temporary failure'
        self.tick()
        self.monitor.rows[0]['error'] = None
        for _ in range(5): self.tick()
        self.assertEqual(self.runner.sends, 0)
        self.monitor.rows[0]['source'] = 'another source'
        self.tick()
        self.monitor.rows[0]['source'] = AG_SOURCE
        self.tick(900)
        self.assertEqual(self.runner.sends, 0)
        b = self.monitor.rows[0]['groups'][0]['buckets'][0]
        b['resetsAt'] = model.timestamp(self.now+18000-120)
        self.assertEqual(classify(targets(self.monitor.rows[0])[0], self.now)[0], 'running')

    def test_disabled_exhausted_missing_and_duplicate_windows_block(self):
        self.setup(antigravity)
        original = copy.deepcopy(self.monitor.rows[0])
        for mutation in ('disabled', 'exhausted', 'missing', 'duplicate', 'duration'):
            account = copy.deepcopy(original)
            buckets = account['groups'][0]['buckets']
            if mutation == 'disabled': buckets[0]['disabled'] = True
            if mutation == 'exhausted': buckets[1]['remaining'] = 0
            if mutation == 'missing': buckets.pop()
            if mutation == 'duplicate': buckets.append(copy.deepcopy(buckets[0]))
            if mutation == 'duration': buckets[0]['windowSeconds'] = 604800
            self.assertNotIn(classify(targets(account)[0], self.now)[0], ('inactive','full_deadline'))

    def test_two_post_reads_confirm_each_window_and_shared_daily_count(self):
        self.setup(antigravity)
        self.enable([1])
        self.mature()
        self.assertEqual(self.runner.sends, 1)
        self.assertEqual(len(self.journal.data['attempts']), 2)
        self.tick(moving=False)
        self.assertTrue(all(r['state']=='sent' for r in self.journal.data['attempts'].values()))
        self.tick(moving=False)
        self.assertTrue(all(r['state']=='confirmed' for r in self.journal.data['attempts'].values()))
        ends = {r['window']:r['reset'] for r in self.journal.data['history']}
        self.assertEqual(ends['weekly']-ends['5h'], 604800-18000)
        self.assertEqual(self.engine._recent(self.journal.data, targets(self.monitor.rows[0])[0], self.now), 1)
        self.assertEqual(len(self.journal.data['preferences']['accounts']), 1)

    def test_uncertain_group_blocks_other_window_and_restart_but_not_other_group(self):
        self.setup(antigravity)
        self.enable([0])
        self.runner.fail = True
        self.mature()
        self.enable([1, 2])
        self.runner.fail = False
        self.engine = Activation(self.monitor, self.journal, self.runner, lambda:self.now, lambda:self.now)
        self.mature()
        self.assertEqual(self.runner.sends, 2)
        for _ in range(10): self.tick()
        self.assertEqual(self.runner.sends, 2)

    def test_legacy_uncertain_claude_receipt_blocks_weekly_without_migration(self):
        self.setup(row)
        self.monitor.rows[0]['groups'][0]['buckets'][1].update(remaining=100,resetsAt=None,inactiveReported=True)
        key = self.monitor.rows[0]['id']
        self.journal.data['attempts'][key] = dict(id='legacy', state='uncertain', at=self.now-86400, window='five_hour')
        self.enable([1])
        self.engine.tick()
        for _ in range(8): self.tick(moving=False)
        self.assertEqual(self.runner.sends, 0)
        Journal.validate(self.journal.data)

    def test_claude_weekly_explicit_null_starts_both_without_second_prompt(self):
        self.setup(row)
        self.monitor.rows[0]['groups'][0]['buckets'][1].update(remaining=100,resetsAt=None,inactiveReported=True)
        self.enable([1])
        self.engine.tick()
        for _ in range(6): self.tick(moving=False)
        self.assertEqual(self.runner.sends, 1)
        self.assertEqual(len(self.journal.data['attempts']), 2)


class ProviderRunnerTests(unittest.TestCase):
    def test_child_environment_removes_paid_routes_without_mutating_parent(self):
        with patch.dict('os.environ', {'OPENAI_API_KEY':'synthetic', 'CODEX_API_KEY':'synthetic', 'GEMINI_API_KEY':'synthetic', 'HERDR_ENV':'1'}):
            env = environment()
            self.assertNotIn('OPENAI_API_KEY', env)
            self.assertNotIn('CODEX_API_KEY', env)
            self.assertNotIn('GEMINI_API_KEY', env)
            self.assertNotIn('HERDR_ENV', env)

    def test_codex_subscription_only_arguments_and_numeric_result(self):
        output = json.dumps(dict(type='turn.completed',usage=dict(input_tokens=30,output_tokens=1,secret='never retain')))
        with patch.object(CodexRunner,'identity'), patch('quota.activation_runners.claude_cli.run',return_value=(0,output)) as run:
            self.assertEqual(CodexRunner().send(('synthetic.exe','codex-a','empty')), dict(input_tokens=30,output_tokens=1))
        args = run.call_args.args[1]
        for flag in ('--ignore-user-config','--ephemeral','read-only','forced_login_method="chatgpt"','features.shell_tool=false'):
            self.assertIn(flag,args)
        self.assertEqual(args[args.index('--model')+1], 'gpt-6-luna')

    def test_codex_rejects_api_and_other_account(self):
        for auth in ({'auth_mode':'apikey'}, {'tokens':{'account_id':'different','access_token':'synthetic'}}):
            with patch('quota.activation_runners.providers.load',return_value=auth):
                with self.assertRaises(ValueError): CodexRunner.identity({'id':'codex-a'})

    def test_antigravity_exact_group_model_plan_mode_and_uncertain_tool_result(self):
        output = json.dumps(dict(event='result',result=dict(status='SUCCESS',usage=dict(input_tokens=12))))
        with patch('quota.activation_runners.antigravity_cli.check_auth_mode'), patch('quota.activation_runners.antigravity_cli.credential',return_value=('synthetic','revision')), patch('quota.activation_runners.claude_cli.run',return_value=(0,output)) as run:
            self.assertEqual(AntigravityRunner().send(('synthetic.exe','gpt-oss-120b-medium','revision','empty')),dict(input_tokens=12))
        args = run.call_args.args[1]
        self.assertIn('plan',args)
        self.assertIn('gpt-oss-120b-medium',args)
        self.assertNotIn('--dangerously-skip-permissions',args)


if __name__ == '__main__': unittest.main()
