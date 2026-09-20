import math
import tkinter as tk
import unittest

from quota.floating_widgets import FloatingQuotaStrip, finite_percent, row_code, stable_key


class FloatingWidgetHelpersTests(unittest.TestCase):
    def test_percent_clamps_and_preserves_unknown(self):
        self.assertEqual(finite_percent(-2), 0)
        self.assertEqual(finite_percent(101), 100)
        self.assertEqual(finite_percent(45.25), 45.25)
        for value in (None, True, '50', math.nan, math.inf):
            self.assertIsNone(finite_percent(value))

    def test_compact_codes_keep_provider_and_quota_group_identity(self):
        self.assertEqual(row_code({'provider': 'codex'}), 'CX')
        self.assertEqual(row_code({'provider': 'claude'}), 'CL')
        self.assertEqual(row_code({'provider': 'copilot'}), 'CP')
        self.assertEqual(row_code({'provider': 'antigravity', 'group': 'Gemini'}), 'GM')
        self.assertEqual(row_code({'provider': 'antigravity', 'group': 'Claude-GPT'}), 'CG')
        self.assertEqual(row_code({'code': 'USER-SUPPLIED'}), 'USER')
        self.assertEqual(stable_key({'accountId': 'a', 'groupId': 'g', 'bucketId': 'b'}), 'a/g/b')
        self.assertEqual(stable_key({'stableKey': 'saved'}), 'saved')


class FloatingWidgetDrawTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        try:
            cls.root = tk.Tk()
            cls.root.withdraw()
        except tk.TclError as error:
            raise unittest.SkipTest('Tk display unavailable') from error

    @classmethod
    def tearDownClass(cls):
        cls.root.destroy()

    def make_strip(self, rows):
        strip = FloatingQuotaStrip(self.root)
        strip.set_rows(rows)
        self.addCleanup(strip.destroy)
        return strip

    def test_full_quota_and_time_draw_complete_colored_ovals(self):
        strip = self.make_strip([{'numeric': 100, 'remaining': '100%', 'timeRemaining': 100,
                                  'status': 'live', 'provider': 'codex', 'window': '7d'}])
        ovals = [(strip.itemcget(item, 'outline'), float(strip.itemcget(item, 'width')))
                 for item in strip.find_all() if strip.type(item) == 'oval']
        self.assertIn(('#468567', 6.0), ovals)
        self.assertIn(('#506579', 2.0), ovals)

    def test_unknown_unlimited_and_stale_are_drawn_honestly(self):
        strip = self.make_strip([
            {'numeric': None, 'remaining': 'Unknown', 'status': 'live', 'provider': 'codex', 'window': '5h'},
            {'numeric': None, 'remaining': 'Unlimited', 'status': 'live', 'provider': 'copilot', 'window': 'Month'},
            {'numeric': 70, 'remaining': '70%', 'status': 'stale', 'provider': 'claude', 'window': '7d'},
        ])
        texts = [strip.itemcget(item, 'text') for item in strip.find_all() if strip.type(item) == 'text']
        self.assertIn('?', texts)
        self.assertIn('unknown', texts)
        self.assertIn('∞', texts)
        self.assertEqual(texts.count('unknown'), 1)
        self.assertIn('stale', texts)
        stale_outlines = [strip.itemcget(item, 'outline') for item in strip.find_withtag('quota-2')
                          if strip.type(item) in ('oval', 'arc')]
        self.assertIn('#8b949c', stale_outlines)

    def test_empty_strip_reserves_message_width(self):
        strip = self.make_strip([])
        self.assertEqual(strip.requested_width, 130)
        texts = [strip.itemcget(item, 'text') for item in strip.find_all() if strip.type(item) == 'text']
        self.assertEqual(texts, ['No pinned quotas'])


if __name__ == '__main__':
    unittest.main()
