import unittest
import importlib.util
from datetime import datetime, timedelta, timezone

from quota.desktop_views import compact_group, compact_window, display_remaining, exact_remaining, last_success_label, popup_rows, quota_pace, reset_label, selected_window, tray_code
from quota.desktop import DARK_TRAY_TEXT, LIGHT_TRAY_TEXT, DesktopApplication, DesktopSettings, tray_text_color
from quota.desktop_widgets import QuotaGrid


class Vault:
    def __init__(self): self.value = {'enabledProviders': ['codex'], 'accountOrder': ['keep']}
    def load(self): return self.value.copy()
    def save(self, value): self.value = value.copy()


class Lock:
    def __init__(self): self.entered = 0
    def __enter__(self): self.entered += 1
    def __exit__(self, *args): self.entered -= 1


class DesktopViewTests(unittest.TestCase):
    def snapshot(self):
        return {'accounts': [
            {'id': 'codex-one', 'provider': 'codex', 'label': 'one', 'status': 'live', 'groups': [
                {'id': 'codex', 'label': 'Codex', 'buckets': [{'id': 'five', 'label': 'Five-hour window', 'remaining': 82}]}]},
            {'id': 'google-one', 'provider': 'antigravity', 'label': 'separate', 'status': 'stale', 'groups': [
                {'id': 'claude', 'label': 'Claude and GPT models', 'buckets': [{'id': 'week', 'label': 'Weekly window', 'remaining': None}]}]},
        ]}

    def test_default_selection_prefers_reported_remaining(self):
        found, choice = selected_window(self.snapshot())
        self.assertEqual(found[0]['id'], 'codex-one')
        self.assertEqual(choice['bucketId'], 'five')

    def test_saved_selection_keeps_provider_account_and_group(self):
        selected = {'accountId': 'google-one', 'groupId': 'claude', 'bucketId': 'week'}
        found, choice = selected_window(self.snapshot(), selected)
        self.assertEqual(found[0]['label'], 'separate')
        self.assertEqual(choice, selected)

    def test_missing_saved_selection_never_switches_to_another_account(self):
        selected = {'accountId': 'removed', 'groupId': 'anything', 'bucketId': 'anything'}
        found, choice = selected_window(self.snapshot(), selected)
        self.assertIsNone(found)
        self.assertEqual(choice, selected)

    def test_unknown_and_stale_are_explicit(self):
        rows = popup_rows(self.snapshot())
        self.assertEqual(rows[1]['remaining'], 'Unknown')
        self.assertEqual(rows[1]['status'], 'stale')
        self.assertEqual(display_remaining({'unlimited': True}), ('Unlimited', None))

    def test_compact_table_labels_keep_known_window_meaning(self):
        self.assertEqual(compact_group('Gemini Models'), 'Gemini')
        self.assertEqual(compact_group('Claude and GPT models'), 'Claude-GPT')
        self.assertEqual(compact_window({'windowSeconds': 18000}), '5h')
        self.assertEqual(compact_window({'windowSeconds': 604800}), '7d')

    def test_tray_codes_and_exact_tooltip_values_keep_provider_identity(self):
        self.assertEqual(tray_code({'provider': 'codex'}, {}), 'CX')
        self.assertEqual(tray_code({'provider': 'claude'}, {}), 'CL')
        self.assertEqual(tray_code({'provider': 'antigravity'}, {'label': 'Gemini Models'}), 'GM')
        self.assertEqual(tray_code({'provider': 'antigravity'}, {'label': 'Claude and GPT models'}), 'CG')
        self.assertEqual(tray_code({'provider': 'copilot'}, {}), 'CP')
        self.assertEqual(exact_remaining({'remaining': 82.5}), '82.5%')
        self.assertEqual(exact_remaining({'remaining': float('nan')}), 'Unknown')
        self.assertEqual(exact_remaining({'remaining': 12.3456789}), '12.3456789%')
        self.assertEqual(last_success_label(0), '1970-01-01 00:00 UTC')

    def test_tray_palette_follows_windows_light_theme_with_safe_fallback(self):
        self.assertEqual(tray_text_color(lambda: 1), DARK_TRAY_TEXT)
        self.assertEqual(tray_text_color(lambda: 0), LIGHT_TRAY_TEXT)
        self.assertEqual(tray_text_color(lambda: (_ for _ in ()).throw(OSError())), LIGHT_TRAY_TEXT)

    @unittest.skipUnless(importlib.util.find_spec('PIL'), 'Pillow optional desktop dependency')
    def test_tray_icon_keeps_the_code_center_transparent_except_for_glyphs(self):
        app = DesktopApplication.__new__(DesktopApplication)
        account = {'provider': 'codex', 'status': 'live'}
        group = {'label': 'Codex'}
        bucket = {'remaining': 50}
        app._selected = lambda: ((account, group, bucket), None)
        icon = app._icon_image()
        # This interior point was formerly covered by an opaque black disk.
        self.assertEqual(icon.getpixel((20, 20))[3], 0)

    def test_reset_label_does_not_claim_replenishment(self):
        now = datetime(2026, 9, 20, tzinfo=timezone.utc)
        future = (now + timedelta(hours=2, minutes=5)).isoformat()
        self.assertEqual(reset_label(future, now), 'Resets in 2h 05m')
        self.assertEqual(reset_label((now-timedelta(seconds=1)).isoformat(), now), 'Reset time passed')

    def test_desktop_preferences_merge_under_the_monitor_lock(self):
        vault, lock = Vault(), Lock()
        settings = DesktopSettings(vault)
        settings.lock = lock
        settings.save({'desktopOpacity': 72})
        self.assertEqual(lock.entered, 0)
        self.assertEqual(vault.value['desktopOpacity'], 72)
        self.assertEqual(vault.value['enabledProviders'], ['codex'])
        self.assertEqual(vault.value['accountOrder'], ['keep'])

    def test_native_quota_grid_smoke(self):
        import tkinter as tk
        root = tk.Tk(); root.withdraw()
        try:
            grid = QuotaGrid(root, lambda row: None, lambda row: None, lambda row: None)
            grid.pack(fill='both', expand=True)
            grid.set_rows([{'accountId': 'a', 'groupId': 'g', 'bucketId': '5h', 'provider': 'codex', 'account': 'one', 'group': 'Codex', 'window': '5h', 'remaining': '80%', 'numeric': 80, 'timeRemaining': 50, 'status': 'live', 'reset': '2h', 'pace': 'on pace'}])
            root.update_idletasks()
            self.assertEqual(len(grid.cards), 1)
        finally:
            root.destroy()

    def test_native_pace_matches_remaining_schedule(self):
        now = datetime(2026, 9, 20, tzinfo=timezone.utc)
        bucket = {'remaining': 70, 'windowSeconds': 18000,
                  'resetsAt': (now + timedelta(hours=2.5)).isoformat()}
        value, text = quota_pace(bucket, now=now)
        self.assertEqual(value, 50)
        self.assertIn('20.0 points under pace', text)
        self.assertIsNone(quota_pace(bucket, live=False, now=now)[0])
        for reset in (now, now+timedelta(hours=6)):
            self.assertIsNone(quota_pace(dict(bucket, resetsAt=reset.isoformat()), now=now)[0])
        self.assertIsNone(quota_pace(dict(bucket, remaining=float('nan')), now=now)[0])
        self.assertIsNone(quota_pace(dict(bucket, windowSeconds=None), now=now)[0])

    def test_floating_pins_are_independent_of_tray_and_keep_explicit_empty(self):
        app = DesktopApplication.__new__(DesktopApplication)
        vault = Vault()
        app.settings = DesktopSettings(vault)
        tray = dict(accountId='tray', groupId='g', bucketId='week')
        app._selected = lambda: (({}, {}, {}), tray)
        app._refresh_popup = lambda: None
        app._refresh_floating = lambda: None
        self.assertEqual(app._floating_selections(), [tray])
        app.settings.save({'desktopFloatingSelections': []})
        self.assertEqual(app._floating_selections(), [])
        other = dict(accountId='other', groupId='g', bucketId='five')
        app._toggle_floating_pin(other)
        self.assertEqual(app._floating_selections(), [other])
        self.assertNotIn('desktopSelection', vault.value)
        app._toggle_floating_pin(other)
        self.assertEqual(app._floating_selections(), [])


if __name__ == '__main__':
    unittest.main()
