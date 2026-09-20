"""Native Windows tray shell for the local quota dashboard.

It owns the same loopback server and monitor as the web entry point. Provider
credentials remain solely with the official clients and existing DPAPI vaults.
"""
import argparse
import contextlib
import ctypes
from ctypes import wintypes
import os
from pathlib import Path
import queue
import threading
import time
import webbrowser

from .connections import Connections
from .desktop_views import compact_group, compact_window, display_remaining, exact_remaining, popup_rows, selected_window, tray_code
from .monitor import Monitor
from .server import handler
from .vault import Vault


APP_DIR = Path(os.environ.get('LOCALAPPDATA', '.')) / 'QuotaDashboard'
MUTEX_NAME = 'Local\\AgentQuotaMonitorWindows.Singleton'
ACTIVATION_EVENT = 'Local\\AgentQuotaMonitorWindows.Activate'
LIGHT_TRAY_TEXT = '#f2f2f2'
DARK_TRAY_TEXT = '#1f2933'


def tray_text_color(theme_reader=None):
    """Choose a code color for the Windows notification-area theme.

    The registry read is optional and failures use the light-text fallback so
    a missing or locked Personalize key cannot prevent the tray from starting.
    """
    try:
        if theme_reader is None:
            import winreg
            key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, r'Software\Microsoft\Windows\CurrentVersion\Themes\Personalize')
            try:
                theme_reader = lambda: winreg.QueryValueEx(key, 'SystemUsesLightTheme')[0]
                value = theme_reader()
            finally:
                winreg.CloseKey(key)
        else:
            value = theme_reader()
        return DARK_TRAY_TEXT if value == 1 else LIGHT_TRAY_TEXT
    except (ImportError, OSError, ValueError, TypeError):
        return LIGHT_TRAY_TEXT


class DesktopSettings:
    """DPAPI-backed desktop preferences, deliberately sharing the settings file."""
    def __init__(self, vault=None):
        self.vault = vault or Vault(APP_DIR / 'settings.dpapi')
        self.lock = None

    def load(self):
        try:
            value = self.vault.load()
            return value if isinstance(value, dict) else {}
        except Exception:
            return {}

    def save(self, updates):
        # Monitor persists account layout with the same vault. Sharing its lock
        # makes a desktop selection or opacity change a merge, not a lost write.
        with (self.lock if self.lock is not None else contextlib.nullcontext()):
            value = self.load()
            value.update(updates)
            self.vault.save(value)


def make_monitor(settings):
    preferences = settings.load()
    desired = preferences.get('desiredGoogleAccounts', [])
    desired = desired if isinstance(desired, list) else []
    monitor = Monitor(Vault(), desired_google=desired, settings_vault=settings.vault)
    settings.lock = monitor.lock
    return monitor


class DesktopApplication:
    def __init__(self, port=8765, refresh_seconds=60, settings=None, activation_event=None):
        self.port = port
        self.refresh_seconds = refresh_seconds
        self.settings = settings or DesktopSettings()
        self.monitor = make_monitor(self.settings)
        self.connections = Connections(self.monitor)
        self.stop = threading.Event()
        self.server = None
        self.root = None
        self.popup = None
        self.floating = None
        self.tray = None
        self.events = queue.Queue()
        self.activation_event = activation_event
        self.window_icons = []

    @property
    def url(self):
        return f'http://127.0.0.1:{self.port}'

    def start_services(self):
        from http.server import ThreadingHTTPServer
        self.server = ThreadingHTTPServer(('127.0.0.1', self.port), handler(self.monitor, self.port, self.connections))
        threading.Thread(target=self.server.serve_forever, daemon=True, name='quota-loopback').start()
        threading.Thread(target=self._poll, daemon=True, name='quota-poll').start()

    def _poll(self):
        while not self.stop.is_set():
            self.monitor.refresh()
            self.events.put('refresh')
            self.stop.wait(self.refresh_seconds)

    def _activation_listener(self):
        """Raise the existing process instead of starting a second shell."""
        kernel = kernel32()
        while not self.stop.is_set():
            if kernel.WaitForSingleObject(self.activation_event, 750) == 0:
                self.events.put('show-popup')

    def open_dashboard(self):
        webbrowser.open(self.url)

    def _selected(self):
        choice = self.settings.load().get('desktopSelection')
        return selected_window(self.monitor.snapshot(), choice)

    def _remember_selection(self, choice):
        if choice:
            self.settings.save({'desktopSelection': choice})

    def tray_tooltip(self):
        found, choice = self._selected()
        if not found:
            return 'Agent Quota Monitor Windows: quota unavailable'
        account, group, bucket = found
        value = exact_remaining(bucket)
        status = account.get('status', 'pending').upper()
        # Windows may truncate tray text, so quota state must come first.
        return f'{tray_code(account, group)} | {compact_window(bucket)} | {value} | {status} | {account.get("label", "account")} | {group.get("label", "group")} | {account.get("provider", "provider")}'[:127]

    def _window_icon_image(self):
        """Static robot-in-donut icon for the native windows, independent of quota state."""
        from PIL import Image, ImageDraw
        image = Image.new('RGBA', (64, 64), (0, 0, 0, 0))
        draw = ImageDraw.Draw(image)
        draw.ellipse((4, 4, 60, 60), outline='#5f9de0', width=6)
        draw.rounded_rectangle((19, 23, 45, 43), radius=5, fill='#d7e4f5', outline='#4777ad', width=2)
        draw.line((32, 16, 32, 23), fill='#4777ad', width=2)
        draw.ellipse((29, 13, 35, 19), fill='#5f9de0')
        draw.ellipse((24, 29, 28, 33), fill='#25374a')
        draw.ellipse((36, 29, 40, 33), fill='#25374a')
        draw.line((26, 38, 38, 38), fill='#4777ad', width=2)
        return image

    def _set_window_icon(self, window):
        from PIL import Image, ImageTk
        source = self._window_icon_image()
        icons = [ImageTk.PhotoImage(source.resize((size, size), Image.Resampling.LANCZOS), master=window)
                 for size in (16, 32, 64)]
        self.window_icons.extend(icons)
        window.iconphoto(True, *icons)
        if os.name == 'nt':
            # Windows may retain a generic title-bar icon with Tk iconphoto.
            # An ICO supplies explicit small and large native icon resources.
            icon_path = APP_DIR / 'robot-ring.ico'
            if not getattr(self, '_native_icon_saved', False):
                APP_DIR.mkdir(parents=True, exist_ok=True)
                source.save(icon_path, format='ICO', sizes=[(16, 16), (32, 32), (64, 64)])
                self._native_icon_saved = True
            window.iconbitmap(str(icon_path))

    def _icon_image(self):
        from PIL import Image, ImageDraw, ImageFont
        image = Image.new('RGBA', (64, 64), (0, 0, 0, 0))
        draw = ImageDraw.Draw(image)
        found, choice = self._selected()
        percent = display_remaining(found[2])[1] if found else None
        status = found[0].get('status') if found else 'pending'
        track = '#7a7a7a'
        fill = '#5fbe73' if percent is not None and percent >= 30 else '#e2a23c' if percent is not None and percent >= 10 else '#d75a5a'
        draw.ellipse((5, 5, 59, 59), outline=track, width=7)
        if percent is not None and status == 'live':
            draw.arc((5, 5, 59, 59), start=-90, end=-90 + round(percent * 3.6), fill=fill, width=7)
        else:
            # Broken gray arcs are deliberately distinct from a live donut.
            for start in range(-90, 270, 60):
                draw.arc((5, 5, 59, 59), start=start, end=start + 34, fill='#a0a0a0', width=7)
        code = tray_code(found[0], found[1]) if found else '?'
        try:
            font = ImageFont.truetype(str(Path(os.environ.get('WINDIR', r'C:\Windows')) / 'Fonts' / 'segoeuib.ttf'), 24)
        except OSError:
            font = ImageFont.load_default()
        x0, y0, x1, y1 = draw.textbbox((0, 0), code, font=font)
        draw.text(((64 - (x1 - x0)) / 2 - x0, (64 - (y1 - y0)) / 2 - y0 - 1), code, fill=tray_text_color(), font=font)
        return image

    def _update_tray(self):
        if self.tray:
            self.tray.icon = self._icon_image()
            self.tray.title = self.tray_tooltip()

    def show_popup(self, *_):
        self.events.put('show-popup')

    def toggle_floating(self, *_):
        self.events.put('toggle-floating')

    def hide_windows(self, *_):
        self.events.put('hide')

    def quit(self, *_):
        self.events.put('quit')

    def _start_tray(self):
        import pystray
        menu = pystray.Menu(
            pystray.MenuItem('Show quota monitor', self.show_popup, default=True),
            pystray.MenuItem('Open full dashboard', lambda *_: self.open_dashboard()),
            pystray.MenuItem('Toggle floating monitor', self.toggle_floating),
            pystray.MenuItem('Hide windows', self.hide_windows),
            pystray.MenuItem('Quit', self.quit),
        )
        self.tray = pystray.Icon('agent-quota-monitor-windows', self._icon_image(), self.tray_tooltip(), menu)
        threading.Thread(target=self.tray.run, daemon=True, name='quota-tray').start()

    def _build_popup(self):
        import tkinter as tk
        from tkinter import ttk
        popup = tk.Toplevel(self.root)
        self._set_window_icon(popup)
        popup.title('Agent Quota Monitor Windows')
        popup.geometry(self.settings.load().get('desktopPopupGeometry', '680x320'))
        popup.protocol('WM_DELETE_WINDOW', popup.withdraw)
        popup.bind('<Escape>', lambda *_: popup.withdraw())
        popup.columnconfigure(0, weight=1)
        title = tk.Label(popup, text='Agent Quota Monitor Windows', font=('Segoe UI', 11, 'bold'))
        title.grid(row=0, column=0, sticky='w', padx=6, pady=(6, 2))
        columns = ('status', 'provider', 'account', 'group', 'window', 'left', 'reset')
        body = ttk.Treeview(popup, columns=columns, show='headings', selectmode='browse', height=10)
        headings = {'status': 'Status', 'provider': 'Provider', 'account': 'Account', 'group': 'Group',
                    'window': 'Window', 'left': 'Left', 'reset': 'Reset'}
        widths = {'status': 52, 'provider': 80, 'account': 150, 'group': 115, 'window': 65, 'left': 50, 'reset': 80}
        for column in columns:
            body.heading(column, text=headings[column])
            body.column(column, width=widths[column], minwidth=42, stretch=column == 'account')
        body.grid(row=1, column=0, sticky='nsew', padx=6, pady=2)
        body.bind('<<TreeviewSelect>>', self._choose_popup_row)
        body.bind('<Motion>', self._hover_popup_row)
        body.bind('<Leave>', lambda *_: self._set_popup_detail())
        popup.rowconfigure(1, weight=1)
        controls = tk.Frame(popup)
        controls.grid(row=2, column=0, sticky='ew', padx=6, pady=(2, 6))
        tk.Button(controls, text='Refresh', command=lambda: threading.Thread(target=self.monitor.refresh, daemon=True).start()).pack(side='left')
        tk.Button(controls, text='Full dashboard', command=self.open_dashboard).pack(side='left', padx=6)
        tk.Button(controls, text='Floating monitor', command=self._toggle_floating_ui).pack(side='left')
        tk.Button(controls, text='Quit', command=self.quit).pack(side='left', padx=6)
        opacity = self.settings.load().get('desktopOpacity', 85)
        scale = tk.Scale(controls, from_=35, to=100, orient='horizontal', label='Opacity', command=self._set_opacity)
        scale.set(max(35, min(100, opacity)))
        scale.pack(side='right')
        detail = tk.Label(popup, anchor='w', justify='left', height=2, font=('Segoe UI', 9), foreground='#4b5563')
        detail.grid(row=3, column=0, sticky='ew', padx=6, pady=(0, 2))
        self.popup, self.popup_body = popup, body
        self.popup_detail = detail
        self._remember_geometry('desktopPopupGeometry', popup)
        self._refresh_popup()

    def _refresh_popup(self):
        if not self.popup:
            return
        previous = self.popup_body.selection()
        selected_key = self.settings.load().get('desktopSelection')
        self._repainting_popup = True
        for item in self.popup_body.get_children():
            self.popup_body.delete(item)
        rows = popup_rows(self.monitor.snapshot())
        self.popup_rows = rows
        if not rows:
            self.popup_body.insert('', 'end', iid='empty', values=('Pending', '', 'No readable quota windows yet', '', '', '', ''))
        for index, row in enumerate(rows):
            item = f'quota-{index}'
            self.popup_body.insert('', 'end', iid=item, values=(row['status'].upper(), row['provider'], row['account'], row['group'], row['window'], row['remaining'], row['reset']))
            key = dict(accountId=row['accountId'], groupId=row['groupId'], bucketId=row['bucketId'])
            if key == selected_key:
                self.popup_body.selection_set(item)
        # Keep a visible selection for the current default without making it a
        # persistent choice. Only an explicit row click calls _remember_selection.
        if not self.popup_body.selection() and rows:
            found, choice = self._selected()
            if found:
                for index, row in enumerate(rows):
                    if choice == dict(accountId=row['accountId'], groupId=row['groupId'], bucketId=row['bucketId']):
                        self.popup_body.selection_set(f'quota-{index}')
                        break
        self._repainting_popup = False
        self._set_popup_detail()
        self._update_tray()

    def _set_popup_detail(self, row=None, heading='Selected'):
        if not row:
            selected = self.popup_body.selection() if self.popup_body else ()
            if selected and selected[0] != 'empty':
                row = self.popup_rows[int(selected[0].rsplit('-', 1)[1])]
        if row:
            self.popup_detail.config(text=f"{heading}  {row['group']} | {row['window']} | {row['exact']} remaining | {row['status'].upper()} | reset {row['reset']}\n{row['account']} | last success {row['lastSuccess']}")
        else:
            self.popup_detail.config(text='Select a quota row to use it in the tray and floating monitor.')

    def _hover_popup_row(self, event):
        item = self.popup_body.identify_row(event.y)
        if item and item != 'empty':
            self._set_popup_detail(self.popup_rows[int(item.rsplit('-', 1)[1])], heading='Details')

    def _choose_popup_row(self, *_):
        if getattr(self, '_repainting_popup', False):
            return
        selection = self.popup_body.selection()
        if not selection or selection[0] == 'empty' or not getattr(self, 'popup_rows', None):
            return
        index = int(selection[0].rsplit('-', 1)[1])
        row = self.popup_rows[index]
        self._remember_selection(dict(accountId=row['accountId'], groupId=row['groupId'], bucketId=row['bucketId']))
        self._set_popup_detail(row)
        self._refresh_floating()
        self._update_tray()

    def _set_opacity(self, value):
        opacity = max(35, min(100, int(float(value))))
        self.settings.save({'desktopOpacity': opacity})
        if self.floating:
            self.floating.attributes('-alpha', opacity / 100)

    def _toggle_floating_ui(self):
        import tkinter as tk
        if self.floating and self.floating.winfo_viewable():
            self.floating.withdraw()
            self.settings.save({'desktopFloating': False})
            return
        if not self.floating:
            floating = tk.Toplevel(self.root)
            floating.title('Quota')
            self._set_window_icon(floating)
            floating.geometry(self.settings.load().get('desktopFloatingGeometry', '300x115'))
            floating.attributes('-topmost', True)
            floating.protocol('WM_DELETE_WINDOW', self._toggle_floating_ui)
            label = tk.Label(floating, font=('Segoe UI', 11, 'bold'), justify='left', anchor='w')
            label.pack(fill='both', expand=True, padx=12, pady=10)
            self.floating, self.floating_label = floating, label
            self._remember_geometry('desktopFloatingGeometry', floating)
        opacity = self.settings.load().get('desktopOpacity', 85)
        self.floating.attributes('-alpha', max(35, min(100, opacity)) / 100)
        self.floating.deiconify()
        self.settings.save({'desktopFloating': True})
        self._refresh_floating()

    def _refresh_floating(self):
        if not self.floating:
            return
        found, choice = self._selected()
        if found:
            account, group, bucket = found
            remaining, _ = display_remaining(bucket)
            stale = 'STALE ' if account.get('status') == 'stale' else ''
            self.floating_label.config(text=f'{stale}{remaining}  {account.get("provider", "provider")}  {compact_window(bucket)}\n{account.get("label", "Unreported account")}  |  {compact_group(group.get("label", "Quota"))}')
        else:
            self.floating_label.config(text='Quota unavailable\nOpen dashboard to connect')

    def _remember_geometry(self, key, window):
        timer = {'id': None}
        def save(_=None):
            if timer['id']:
                self.root.after_cancel(timer['id'])
            timer['id'] = self.root.after(500, lambda: self.settings.save({key: window.geometry()}))
        window.bind('<Configure>', save)

    def _process_events(self):
        try:
            while True:
                event = self.events.get_nowait()
                if event == 'quit':
                    self.stop.set()
                    if self.tray:
                        self.tray.stop()
                    self.root.destroy()
                    return
                if event == 'show-popup':
                    self.popup.deiconify(); self.popup.lift()
                elif event == 'toggle-floating':
                    self._toggle_floating_ui()
                elif event == 'hide':
                    self.popup.withdraw()
                    if self.floating: self.floating.withdraw()
                if event in ('refresh', 'show-popup'):
                    self._refresh_popup(); self._refresh_floating()
        except queue.Empty:
            pass
        self.root.after(750, self._process_events)

    def run(self):
        import tkinter as tk
        self.start_services()
        self.root = tk.Tk()
        self._set_window_icon(self.root)
        self.root.withdraw()
        self._build_popup()
        self._start_tray()
        if self.activation_event:
            threading.Thread(target=self._activation_listener, daemon=True, name='quota-activation').start()
        if self.settings.load().get('desktopFloating'):
            self._toggle_floating_ui()
        self._process_events()
        try:
            self.root.mainloop()
        finally:
            self.stop.set()
            self.connections.close()
            if self.server:
                self.server.shutdown(); self.server.server_close()


def single_instance():
    if os.name != 'nt':
        return None, None
    kernel = kernel32()
    mutex = kernel.CreateMutexW(None, False, MUTEX_NAME)
    if not mutex:
        raise OSError('Could not create desktop monitor mutex')
    if ctypes.get_last_error() == 183:
        kernel.CloseHandle(mutex)
        event = kernel.OpenEventW(2, False, ACTIVATION_EVENT)
        if event:
            kernel.SetEvent(event)
            kernel.CloseHandle(event)
        return False, None
    event = kernel.CreateEventW(None, False, False, ACTIVATION_EVENT)
    if not event:
        kernel.CloseHandle(mutex)
        raise OSError('Could not create desktop activation event')
    return mutex, event


def main():
    parser = argparse.ArgumentParser(description='Agent Quota Monitor Windows')
    parser.add_argument('--port', type=int, default=8765)
    args = parser.parse_args()
    if not 1024 <= args.port <= 65535:
        parser.error('Choose a port between 1024 and 65535')
    mutex, activation_event = single_instance()
    if mutex is False:
        print('Agent Quota Monitor Windows is already running.', flush=True)
        return 0
    try:
        DesktopApplication(args.port, activation_event=activation_event).run()
    finally:
        if activation_event:
            kernel32().CloseHandle(activation_event)
        if mutex:
            kernel32().CloseHandle(mutex)
    return 0


def kernel32():
    """Configure pointer-sized Win32 handles before using them on 64-bit Python."""
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.CreateMutexW.argtypes = (wintypes.LPVOID, wintypes.BOOL, wintypes.LPCWSTR)
    kernel.CreateMutexW.restype = wintypes.HANDLE
    kernel.CreateEventW.argtypes = (wintypes.LPVOID, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR)
    kernel.CreateEventW.restype = wintypes.HANDLE
    kernel.OpenEventW.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR)
    kernel.OpenEventW.restype = wintypes.HANDLE
    kernel.SetEvent.argtypes = (wintypes.HANDLE,)
    kernel.SetEvent.restype = wintypes.BOOL
    kernel.WaitForSingleObject.argtypes = (wintypes.HANDLE, wintypes.DWORD)
    kernel.WaitForSingleObject.restype = wintypes.DWORD
    kernel.CloseHandle.argtypes = (wintypes.HANDLE,)
    kernel.CloseHandle.restype = wintypes.BOOL
    return kernel


if __name__ == '__main__':
    raise SystemExit(main())
