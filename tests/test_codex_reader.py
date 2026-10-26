"""Synthetic Codex schema, identity, transport, and trust-boundary regressions."""
import copy
import ssl
import unittest
import urllib.error
from unittest.mock import Mock, patch

from quota import model, providers
from quota.monitor import Monitor


def usage():
    return dict(account_id='account-a', email='same@example.test',
                rate_limit=dict(primary_window=dict(used_percent=7, limit_window_seconds=604800,
                                                   reset_at=1793513471), secondary_window=None),
                code_review_rate_limit=None, additional_rate_limits=None)


class Store:
    def load(self): return {}
    def save(self, value): self.value = value


class CodexParserTests(unittest.TestCase):
    def test_additive_fields_preserve_existing_quota_and_identity(self):
        raw = usage()
        expected = model.codex(raw)
        raw.update(model_usage=[dict(new_field='unmapped')], credits={}, future_metadata=True)
        raw['rate_limit']['future_window'] = dict(used_percent=99)
        raw['rate_limit']['primary_window']['future_field'] = {'nested': True}
        self.assertEqual(model.codex(raw), expected)
        self.assertEqual(expected[0]['id'], 'codex')
        self.assertEqual(expected[0]['buckets'][0]['id'], 'codex/primary_window')
        self.assertEqual(expected[0]['buckets'][0]['windowSeconds'], 604800)

    def test_optional_absent_null_and_empty_sections(self):
        for field in ('code_review_rate_limit', 'additional_rate_limits'):
            for value in (None, {} if field == 'code_review_rate_limit' else []):
                raw = usage()
                raw[field] = value
                self.assertEqual(model.codex(raw)[0]['buckets'][0]['remaining'], 93)
            raw.pop(field)
            self.assertEqual(model.codex(raw)[0]['buckets'][0]['remaining'], 93)

    def test_named_additional_limits_do_not_depend_on_row_order(self):
        raw = usage()
        raw['additional_rate_limits'] = [dict(limit_name=name, rate_limit=copy.deepcopy(raw['rate_limit']))
                                         for name in ('model-a', 'model-b')]
        expected = {g['id']: g for g in model.codex(raw)}
        raw['additional_rate_limits'].reverse()
        self.assertEqual({g['id']: g for g in model.codex(raw)}, expected)

    def test_changed_known_shapes_and_duplicate_identities_are_rejected(self):
        cases = [None, [], 'json']
        for field in ('rate_limit', 'code_review_rate_limit', 'additional_rate_limits'):
            raw = usage()
            raw[field] = 'changed shape'
            cases.append(raw)
        for field in ('primary_window', 'secondary_window'):
            raw = usage()
            raw['rate_limit'][field] = []
            cases.append(raw)
        for value in ([None], [{}], [dict(limit_name=7)], [dict(limit_name=' ')],
                      [dict(limit_name='model', rate_limit={})] * 2,
                      [dict(limit_name='codex', rate_limit={})]):
            raw = usage()
            raw['additional_rate_limits'] = value
            cases.append(raw)
        for raw in cases:
            with self.subTest(raw=raw), self.assertRaises(ValueError):
                model.codex(raw)

    def test_invalid_values_remain_unknown_and_never_become_full_quota(self):
        for value in (None, True, '0', -1, 101, float('nan'), float('inf'), 10 ** 400):
            raw = usage()
            raw['rate_limit']['primary_window']['used_percent'] = value
            self.assertIsNone(model.codex(raw)[0]['buckets'][0]['remaining'])
        for value in (None, True, '604800', -1, 0, float('nan'), float('inf'), 10 ** 400):
            raw = usage()
            raw['rate_limit']['primary_window']['limit_window_seconds'] = value
            self.assertIsNone(model.codex(raw)[0]['buckets'][0]['windowSeconds'])


class CodexReaderTests(unittest.TestCase):
    def setUp(self):
        self.session = dict(tokens=dict(account_id='account-a', access_token='synthetic-token'))
        self.session_load = patch.object(providers, 'load', side_effect=lambda _: copy.deepcopy(self.session))
        self.session_load.start()
        self.addCleanup(self.session_load.stop)
        self.tls = patch.object(providers, 'codex_tls_context', return_value=Mock())
        self.tls.start()
        self.addCleanup(self.tls.stop)
        self.stat = patch.object(providers.Path, 'stat', return_value=Mock(st_mtime_ns=1))
        self.stat.start()
        self.addCleanup(self.stat.stop)
        request_patch = patch.object(providers, 'request', return_value=usage())
        self.request = request_patch.start()
        self.addCleanup(request_patch.stop)
        self.account = providers.codex_account()

    def test_identity_mismatch_is_checked_before_parsing(self):
        self.request.return_value = dict(usage(), account_id='account-b')
        with patch.object(model, 'codex') as parse, self.assertRaises(providers.ReadError) as error:
            self.account['read']()
        self.assertEqual(error.exception.code, 'identity_mismatch')
        parse.assert_not_called()

    def test_session_identity_change_and_missing_token_do_not_send_a_request(self):
        for session, code in ((dict(account_id='account-b', access_token='synthetic-token'), 'identity_changed'),
                              (dict(account_id='account-a'), 'sign_in_required')):
            self.session['tokens'] = session
            with self.assertRaises(providers.ReadError) as error:
                self.account['read']()
            self.assertEqual(error.exception.code, code)
        self.request.assert_not_called()

    def test_structure_failure_is_schema_changed_and_retains_last_good_quota(self):
        now = [1000]
        monitor = Monitor(Store(), clock=lambda: now[0])
        monitor.refresh_one(self.account)
        before = copy.deepcopy(monitor.rows[self.account['id']])
        self.request.return_value['additional_rate_limits'] = [None]
        now[0] = before['nextAttempt']
        monitor.refresh_one(self.account)
        after = monitor.rows[self.account['id']]
        self.assertEqual(after['error'], 'schema_changed')
        self.assertEqual(after['status'], 'stale')
        self.assertEqual(after['lastSuccess'], before['lastSuccess'])
        self.assertEqual(after['groups'], before['groups'])

    def test_certificate_failure_is_stale_with_no_second_transport(self):
        now = [1000]
        monitor = Monitor(Store(), clock=lambda: now[0])
        monitor.refresh_one(self.account)
        old = copy.deepcopy(monitor.rows[self.account['id']])
        self.request.reset_mock()
        self.request.side_effect = providers.ReadError('certificate_verification_failed')
        now[0] = old['nextAttempt']
        monitor.refresh_one(self.account)
        after = monitor.rows[self.account['id']]
        self.assertEqual(after['error'], 'certificate_verification_failed')
        self.assertEqual(after['groups'], old['groups'])
        self.assertEqual(after['status'], 'stale')
        self.request.assert_called_once()


class CodexTrustTests(unittest.TestCase):
    def test_only_server_auth_roots_are_loaded_without_cached_intermediates(self):
        context = Mock()
        certs = [(b'root-a', 'x509_asn', True),
                 (b'root-b', 'x509_asn', {ssl.Purpose.SERVER_AUTH.oid}),
                 (b'client-only', 'x509_asn', {ssl.Purpose.CLIENT_AUTH.oid}),
                 (b'container', 'pkcs_7_asn', True)]
        with patch.object(providers.os, 'name', 'nt'), \
                patch.object(ssl, 'enum_certificates', return_value=certs, create=True) as enumerate_certs, \
                patch.object(ssl, 'DER_cert_to_PEM_cert', side_effect=lambda c: c.decode()+'\n'), \
                patch.object(ssl, 'SSLContext', return_value=context):
            self.assertIs(providers.codex_tls_context(), context)
        enumerate_certs.assert_called_once_with('ROOT')
        context.load_verify_locations.assert_called_once_with(cadata='root-a\nroot-b\n')

    def test_empty_or_unavailable_root_store_fails_closed(self):
        for value in ([], OSError('synthetic inaccessible store')):
            with patch.object(providers.os, 'name', 'nt'), \
                    patch.object(ssl, 'enum_certificates', create=True) as enumerate_certs:
                if isinstance(value, Exception):
                    enumerate_certs.side_effect = value
                else:
                    enumerate_certs.return_value = value
                with self.assertRaises(providers.ReadError) as error:
                    providers.codex_tls_context()
                self.assertEqual(error.exception.code, 'certificate_verification_failed')

    @unittest.skipUnless(hasattr(ssl, 'enum_certificates'), 'Windows trust store')
    def test_real_context_requires_certificate_and_hostname_validation(self):
        context = providers.codex_tls_context()
        self.assertEqual(context.verify_mode, ssl.CERT_REQUIRED)
        self.assertTrue(context.check_hostname)
        self.assertIsNone(context.keylog_filename)
        self.assertGreater(context.cert_store_stats()['x509_ca'], 0)

    def test_tls_errors_are_distinct_and_not_retried(self):
        for error in (ssl.SSLCertVerificationError(1, 'synthetic expired certificate'),
                      urllib.error.URLError(ssl.SSLCertVerificationError(1, 'synthetic hostname mismatch'))):
            opener = Mock()
            opener.open.side_effect = error
            with patch.object(providers.urllib.request, 'build_opener', return_value=opener):
                with self.assertRaises(providers.ReadError) as raised:
                    providers.request('https://example.invalid/usage')
            self.assertEqual(raised.exception.code, 'certificate_verification_failed')
            opener.open.assert_called_once()

    def test_unreadable_json_and_known_nonobject_json_are_schema_failures(self):
        for payload in (b'<html>synthetic gateway response</html>', b'[]', b'null', b'{broken'):
            response = Mock()
            response.__enter__ = Mock(return_value=response)
            response.__exit__ = Mock(return_value=False)
            response.read.return_value = payload
            opener = Mock()
            opener.open.return_value = response
            with patch.object(providers.urllib.request, 'build_opener', return_value=opener):
                with self.assertRaises(providers.ReadError) as error:
                    providers.request('https://example.invalid/usage')
            self.assertEqual(error.exception.code, 'schema_changed')


if __name__ == '__main__':
    unittest.main()
