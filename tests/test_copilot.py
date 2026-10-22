import copy
import json
import subprocess
import unittest
from unittest.mock import Mock, patch
from quota import copilot, model, providers
from quota.monitor import Monitor


def fixture():
    return {'id':123,'login':'sample-user','sku':'free_limited_copilot','reset':'2026-10-01T00:00:00Z',
            'models':[{'id':'auto'}], 'pools':[
                {'id':'chat','entitlement':200,'remaining':180.5,'remainingPercentage':90.25,'hasQuota':True,'tokenBasedBilling':True},
                {'id':'completions','entitlement':2000,'remaining':1990,'remainingPercentage':99.5,'hasQuota':True,'tokenBasedBilling':True},
                {'id':'premium_interactions','entitlement':0,'remaining':0,'remainingPercentage':0,'hasQuota':False,'tokenBasedBilling':True}]}


class Store:
    def __init__(self,data=None): self.data=data or {}
    def load(self): return copy.deepcopy(self.data)
    def save(self,value): self.data=copy.deepcopy(value)


class CopilotTests(unittest.TestCase):
    def test_node_security_floors_allow_patched_supported_releases(self):
        for version in ('v22.23.4', 'v22.24.0', 'v24.22.1', 'v24.23.0', 'v26.12.1', 'v26.13.0'):
            with self.subTest(version=version), patch.object(copilot.subprocess, 'run',
                    return_value=Mock(returncode=0, stdout=version + '\n')) as run:
                copilot.validate_node('C:/tools/node.exe')
                self.assertEqual(run.call_args.args[0], ['C:/tools/node.exe', '--version'])
                self.assertEqual(run.call_args.kwargs['timeout'], 5)
                self.assertEqual(run.call_args.kwargs['stdin'], subprocess.DEVNULL)
                self.assertEqual(run.call_args.kwargs['stderr'], subprocess.DEVNULL)
                self.assertEqual(run.call_args.kwargs['creationflags'], getattr(subprocess, 'CREATE_NO_WINDOW', 0))

    def test_node_security_check_rejects_old_unsupported_and_unverified_releases(self):
        versions = ('v22.23.3', 'v24.22.0', 'v26.12.0', 'v20.99.0', 'v23.99.0', 'v25.99.0',
                    'v27.0.0', 'v24.22.1-rc.1', 'v24.22.1-private', 'node v24.22.1', '', 'private diagnostic')
        for version in versions:
            with self.subTest(version=version), patch.object(copilot.subprocess, 'run',
                    return_value=Mock(returncode=0, stdout=version)):
                with self.assertRaises(providers.ReadError) as error:
                    copilot.validate_node('C:/tools/node.exe')
                self.assertEqual(str(error.exception), 'copilot_node_update_required')

    def test_node_probe_failure_is_redacted(self):
        failures = (OSError('private diagnostic'), subprocess.TimeoutExpired('private command', 5),
                    subprocess.CalledProcessError(1, 'private command', stderr='private diagnostic'))
        for failure in failures:
            with self.subTest(failure=type(failure)), patch.object(copilot.subprocess, 'run', side_effect=failure):
                with self.assertRaises(providers.ReadError) as error:
                    copilot.validate_node('C:/tools/node.exe')
                self.assertEqual(str(error.exception), 'copilot_node_update_required')

    def test_unsupported_node_stops_before_the_bridge_starts(self):
        with patch.dict(copilot.os.environ, {'LOCALAPPDATA': 'C:/local'}), \
                patch('quota.providers.refresh_path'), patch.object(copilot.shutil, 'which', return_value='C:/tools/node.exe'), \
                patch.object(copilot.Path, 'is_file', return_value=True), \
                patch.object(copilot.subprocess, 'run', return_value=Mock(returncode=0, stdout='v24.22.0\n')), \
                patch.object(copilot.subprocess, 'Popen') as launch:
            self.assertFalse(copilot.setup_ready())
            with self.assertRaises(providers.ReadError) as error:
                copilot.read_snapshot()
            self.assertEqual(error.exception.code, 'copilot_node_update_required')
            launch.assert_not_called()

    def test_monthly_units_and_provider_reset(self):
        groups=model.copilot(fixture())
        b=groups[0]['buckets'][0]
        self.assertEqual((b['remaining'],b['amountRemaining'],b['unit']),(90.25,180.5,'credits'))
        self.assertIsNone(b['windowSeconds'])
        self.assertEqual(b['windowKind'],'monthly')
        self.assertEqual(b['resetsAt'],'2026-10-01T00:00:00+00:00')
        self.assertEqual(groups[1]['buckets'][0]['unit'],'suggestions')
        premium=groups[2]['buckets'][0]
        self.assertFalse(premium['available'])
        self.assertEqual((premium['remaining'],premium['amountRemaining'],premium['entitlement']),(0,0,0))
        self.assertEqual(groups[0]['models'],['auto'])

    def test_unknown_unlimited_and_exhausted_are_different(self):
        for value in (None,True,'12',float('nan'),-5,101):
            raw=fixture(); raw['pools'][0]['remainingPercentage']=value
            self.assertIsNone(model.copilot(raw)[0]['buckets'][0]['remaining'])
        raw=fixture(); raw['pools'][0].update(unlimited=True,entitlement=-1)
        b=model.copilot(raw)[0]['buckets'][0]
        self.assertTrue(b['unlimited']); self.assertIsNone(b['remaining']); self.assertIsNone(b['entitlement'])
        raw['pools'][0].update(unlimited=False,remainingPercentage=0,remaining=0,entitlement=200,hasQuota=False)
        b=model.copilot(raw)[0]['buckets'][0]
        self.assertEqual((b['remaining'],b['amountRemaining'],b['entitlement']),(0,0,200))
        self.assertFalse(b['available'])

    def test_partial_pool_preserves_valid_siblings_and_unknown_availability(self):
        raw=fixture(); raw['pools'][0]={'id':'chat'}
        groups=model.copilot(raw)
        self.assertEqual(len(groups),3)
        unknown=groups[0]['buckets'][0]
        self.assertIsNone(unknown['remaining']); self.assertIsNone(unknown['available'])
        self.assertIsNone(unknown['entitlement']); self.assertFalse(unknown['unlimited'])
        self.assertEqual(groups[1]['buckets'][0]['remaining'],99.5)

    def test_legacy_requests_not_relabelled_as_credits(self):
        raw=fixture(); raw['pools'][0]['tokenBasedBilling']=False
        self.assertEqual(model.copilot(raw)[0]['buckets'][0]['unit'],'requests')

    def test_discovery_is_local_and_identity_mismatch_is_rejected(self):
        with patch.object(copilot,'descriptor_vault',return_value=Store({'id':123,'login':'same-label'})), patch.object(copilot,'read_snapshot') as read:
            a=copilot.copilot_account(); read.assert_not_called()
            raw=fixture(); raw['id']=456; raw['login']='same-label'; read.return_value=raw
            with self.assertRaises(providers.ReadError) as err: a['read']()
            self.assertEqual(err.exception.code,'identity_changed')

    def test_removal_skips_reader_and_failures_keep_plan_and_quota(self):
        with patch.object(copilot,'descriptor_vault',return_value=Store({'id':123,'login':'sample-user'})), patch.object(copilot,'read_snapshot',return_value=fixture()) as read:
            a=copilot.copilot_account(); now=[1000]
            m=Monitor(Store(),lambda:now[0]); m.refresh_one(a)
            original=m.rows[a['id']]['groups']
            m.refresh_one(a); self.assertEqual(read.call_count,1)
            for i,code in enumerate(('sign_in_required','rate_limited','schema_changed','copilot_node_update_required')):
                now[0]=m.rows[a['id']]['nextAttempt']+1
                read.side_effect=providers.ReadError(code,1000 if code=='rate_limited' else 0)
                m.refresh_one(a); row=m.rows[a['id']]
                self.assertEqual(row['groups'],original); self.assertEqual(row['lastSuccess'],1000)
                self.assertEqual(row['error'],code); self.assertEqual(row['status'],'stale')
                if code=='rate_limited': self.assertGreaterEqual(row['nextAttempt'],now[0]+1000)
            m.remove_account(a['id']); now[0]+=4000; count=read.call_count
            m.refresh_one(a); self.assertEqual(read.call_count,count)

    def test_bridge_failure_is_redacted(self):
        process=Mock(returncode=1)
        process.communicate.return_value=(json.dumps({'error':'copilot_reader_failed'}).encode(),None)
        with patch.object(copilot,'command',return_value=['node','bridge']),patch.object(copilot.subprocess,'Popen',return_value=process):
            with self.assertRaises(providers.ReadError) as err: copilot.read_snapshot()
            self.assertEqual(str(err.exception),'copilot_reader_failed')

    def test_timeout_cleanup_falls_back_without_leaking_errors(self):
        process=Mock(pid=1234)
        process.communicate.side_effect=subprocess.TimeoutExpired('private command',45)
        process.poll.return_value=None
        process.wait.return_value=1
        with patch.object(copilot,'command',return_value=['node','bridge']),patch.object(copilot.subprocess,'Popen',return_value=process),patch.object(copilot.subprocess,'run',side_effect=OSError('private diagnostic')):
            with self.assertRaises(providers.ReadError) as err: copilot.read_snapshot()
            self.assertEqual(str(err.exception),'copilot_read_timeout')
            process.kill.assert_called_once()

    def test_cleanup_wait_failure_is_normalized(self):
        process=Mock(pid=1234)
        process.communicate.side_effect=subprocess.TimeoutExpired('private command',45)
        process.wait.side_effect=subprocess.TimeoutExpired('private wait',5)
        process.poll.return_value=None
        with patch.object(copilot,'command',return_value=['node','bridge']),patch.object(copilot.subprocess,'Popen',return_value=process),patch.object(copilot.subprocess,'run'):
            with self.assertRaises(providers.ReadError) as err: copilot.read_snapshot()
            self.assertEqual(str(err.exception),'copilot_cleanup_failed')


if __name__ == '__main__': unittest.main()
