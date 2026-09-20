"""Compact, presentation-only Tk widgets for selected quota windows."""
import math
import tkinter as tk


PROVIDER_CODES = {'codex': 'CX', 'claude': 'CL', 'copilot': 'CP'}
GROUP_CODES = {'Gemini': 'GM', 'Gemini Models': 'GM',
               'Claude-GPT': 'CG', 'Claude and GPT models': 'CG'}


def finite_percent(value):
    """Return a clamped real percentage, preserving unknown as None."""
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        return None
    return max(0.0, min(100.0, float(value)))


def row_code(row):
    """Use the supplied code, or derive a short provider and group identity."""
    supplied = row.get('code')
    if isinstance(supplied, str) and supplied.strip():
        return supplied.strip()[:4]
    group = str(row.get('group', ''))
    return GROUP_CODES.get(group, PROVIDER_CODES.get(row.get('provider'), '?'))


def stable_key(row):
    supplied = row.get('key') or row.get('stableKey')
    if supplied is not None:
        return str(supplied)
    return '/'.join(str(row.get(name, '')) for name in ('accountId', 'groupId', 'bucketId'))


class FloatingQuotaStrip(tk.Canvas):
    """A horizontal strip of selected quota donuts with stable row callbacks."""

    CELL_WIDTH = 58
    HEIGHT = 68
    RING_SIZE = 44
    PAD_X = 7

    def __init__(self, parent, on_hover=None, on_leave=None, **kwargs):
        kwargs.setdefault('background', '#f7f8f9')
        kwargs.setdefault('highlightthickness', 1)
        kwargs.setdefault('highlightbackground', '#b8c1c9')
        kwargs.setdefault('highlightcolor', '#176ca4')
        kwargs.setdefault('takefocus', True)
        kwargs.setdefault('height', self.HEIGHT)
        super().__init__(parent, **kwargs)
        self.on_hover = on_hover or (lambda row: None)
        self.on_leave = on_leave or (lambda: None)
        self.rows = []
        self.selected = None
        self.requested_width = self.PAD_X * 2 + self.CELL_WIDTH
        self.requested_height = self.HEIGHT
        self.configure(width=self.requested_width)
        self.bind('<Leave>', self._leave)
        self.bind('<FocusOut>', self._focus_out)
        self.bind('<Left>', lambda event: self._move(-1))
        self.bind('<Right>', lambda event: self._move(1))
        self.bind('<Home>', lambda event: self._select(0))
        self.bind('<End>', lambda event: self._select(len(self.rows) - 1))
        self.bind('<Return>', self._activate)
        self.bind('<space>', self._activate)
        self.bind('<Escape>', self._leave)

    @property
    def requested_geometry(self):
        return f'{self.requested_width}x{self.requested_height}'

    def set_rows(self, rows):
        selected_key = stable_key(self.rows[self.selected]) if self.selected is not None and self.selected < len(self.rows) else None
        self.rows = [dict(row) for row in (rows or [])]
        if selected_key is not None:
            self.selected = next((i for i, row in enumerate(self.rows) if stable_key(row) == selected_key), None)
        self.requested_width = (self.PAD_X * 2 + self.CELL_WIDTH * len(self.rows)) if self.rows else 130
        self.requested_height = self.HEIGHT
        self.configure(width=self.requested_width, height=self.requested_height,
                       scrollregion=(0, 0, self.requested_width, self.requested_height))
        self._draw()

    def _draw(self):
        self.delete('all')
        if not self.rows:
            self.create_text(self.requested_width / 2, self.HEIGHT / 2, text='No pinned quotas',
                             fill='#727d86', font=('Segoe UI', 8))
            return
        for index, row in enumerate(self.rows):
            self._draw_row(index, row)
        self._draw_selection()

    def _draw_row(self, index, row):
        tag = f'quota-{index}'
        x = self.PAD_X + index * self.CELL_WIDTH + self.CELL_WIDTH / 2
        y = 26
        half = self.RING_SIZE / 2
        quota = finite_percent(row.get('numeric'))
        time_remaining = finite_percent(row.get('timeRemaining'))
        unlimited = str(row.get('remaining', '')).lower() == 'unlimited'
        stale = row.get('status') != 'live'
        track = '#e2e6e9'
        quota_color = '#8b949c' if stale else ('#b67925' if quota is not None and quota <= 10 else '#468567')
        time_color = '#8b949c' if stale else '#506579'
        outer = (x - half, y - half, x + half, y + half)
        self.create_oval(*outer, outline=track, width=6, tags=tag)
        if quota == 100:
            self.create_oval(*outer, outline=quota_color, width=6, tags=tag)
        elif quota is not None and quota > 0:
            self.create_arc(*outer, start=90, extent=-3.6 * quota, style='arc',
                            outline=quota_color, width=6, tags=tag)
        inner = (x - half + 7, y - half + 7, x + half - 7, y + half - 7)
        if time_remaining is not None:
            self.create_oval(*inner, outline='#edf0f2', width=2, tags=tag)
            if time_remaining == 100:
                self.create_oval(*inner, outline=time_color, width=2, tags=tag)
            elif time_remaining > 0:
                self.create_arc(*inner, start=90, extent=-3.6 * time_remaining, style='arc',
                                outline=time_color, width=2, tags=tag)
        center = '∞' if unlimited else ('?' if quota is None else f'{quota:.0f}%')
        self.create_text(x, y, text=center, fill='#69747d' if stale or quota is None else '#20262d',
                         font=('Segoe UI Semibold', 8), tags=tag)
        caption = row_code(row) + str(row.get('window') or '')
        self.create_text(x, 55, text=caption, fill='#77818b' if stale else '#44515c',
                         font=('Segoe UI', 7), tags=tag)
        if stale:
            self.create_text(x, 64, text='stale', fill='#8a6b3f', font=('Segoe UI', 6), tags=tag)
        elif quota is None and not unlimited:
            self.create_text(x, 64, text='unknown', fill='#77818b', font=('Segoe UI', 6), tags=tag)
        self.tag_bind(tag, '<Enter>', lambda event, i=index: self._select(i))
        self.tag_bind(tag, '<Motion>', lambda event, i=index: self._select(i, redraw=False))
        self.tag_bind(tag, '<Button-1>', lambda event, i=index: self._click(i))

    def _click(self, index):
        self.focus_set()
        self._select(index)

    def _select(self, index, redraw=True):
        if not self.rows:
            return 'break'
        index = max(0, min(len(self.rows) - 1, index))
        changed = self.selected != index
        self.selected = index
        if redraw and changed:
            self._draw_selection()
        self.on_hover(self.rows[index])
        return 'break'

    def _draw_selection(self):
        self.delete('selection')
        if self.selected is None or not self.rows:
            return
        left = self.PAD_X + self.selected * self.CELL_WIDTH + 1
        self.create_rectangle(left, 2, left + self.CELL_WIDTH - 2, self.HEIGHT - 3,
                              outline='#7aa4c2', width=1, tags='selection')
        self.tag_lower('selection')

    def _move(self, amount):
        start = self.selected if self.selected is not None else (0 if amount > 0 else len(self.rows) - 1)
        return self._select(start + amount)

    def _activate(self, _event=None):
        if self.selected is None and self.rows:
            self.selected = 0
        if self.selected is not None:
            self._select(self.selected)
        return 'break'

    def _leave(self, _event=None):
        self.selected = None
        self._draw_selection()
        self.on_leave()
        return 'break'

    def _focus_out(self, event):
        if not self.winfo_containing(event.x_root, event.y_root) is self:
            self._leave()
