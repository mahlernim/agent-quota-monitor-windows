"""Windowed portable entry point and offline packaging smoke check."""
import json
from pathlib import Path
import sys


def self_test(output):
    # No monitor is constructed, no credentials are read, and no network is used.
    import tkinter as tk
    import pystray
    from quota.desktop import DesktopApplication
    from quota.server import WEB
    from quota import copilot
    required = [WEB / name for name in ('index.html', 'app.js', 'style.css', 'icon.svg')]
    required += [Path(copilot.__file__).with_name(name) for name in ('copilot_bridge.mjs', 'copilot_bridge_data.mjs')]
    for path in required:
        if not path.is_file() or not path.stat().st_size:
            raise RuntimeError('Missing packaged resource')
    root = tk.Tk()
    root.withdraw()
    try:
        app = object.__new__(DesktopApplication)
        assert app._window_icon_image().size == (64, 64)
        app._selected = lambda: (({'provider': 'codex', 'status': 'live'}, {'label': 'Codex'}, {'remaining': 50}), None)
        assert app._icon_image().size == (64, 64)
        from quota.desktop_widgets import QuotaGrid
        from quota.floating_widgets import FloatingQuotaStrip
        rows = [dict(accountId='sample', groupId='quota', bucketId='week', provider='codex',
                     account='Sample account', group='Codex', window='7d', remaining='50%',
                     numeric=50, timeRemaining=25, status='live', code='CX')]
        grid = QuotaGrid(root, lambda row: None, lambda row: None, lambda row: None)
        grid.set_rows(rows, selected=rows[0], pinned=rows)
        strip = FloatingQuotaStrip(root)
        strip.set_rows(rows)
        assert strip.requested_width > 0
        root.update()
    finally:
        root.destroy()
    Path(output).write_text(json.dumps({'passed': True, 'frozen': bool(getattr(sys, 'frozen', False)),
                                      'resources': len(required), 'tk': True, 'icons': True,
                                      'providerReads': 0}), encoding='utf-8')


if __name__ == '__main__':
    if len(sys.argv) == 3 and sys.argv[1] == '--self-test-output':
        self_test(sys.argv[2])
    else:
        from quota.desktop import main
        raise SystemExit(main())
