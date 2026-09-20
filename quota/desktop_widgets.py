"""Small native Tk widgets for the desktop quota monitor."""
import tkinter as tk
from .ring_render import render_rings


def _number(value):
    return value if isinstance(value, (int, float)) and not isinstance(value, bool) and 0 <= value <= 100 else None


class QuotaGrid(tk.Frame):
    """Scrollable grouped quota cards with mouse and keyboard selection."""
    CARD_WIDTH, CARD_HEIGHT = 106, 82

    def __init__(self, master, on_select, on_hover, on_pin):
        super().__init__(master)
        self.on_select, self.on_hover, self.on_pin = on_select, on_hover, on_pin
        self.canvas = tk.Canvas(self, highlightthickness=0, background='#f8fafc')
        self.scrollbar = tk.Scrollbar(self, orient='vertical', command=self.canvas.yview)
        self.canvas.configure(yscrollcommand=self.scrollbar.set)
        self.canvas.grid(row=0, column=0, sticky='nsew')
        self.scrollbar.grid(row=0, column=1, sticky='ns')
        self.columnconfigure(0, weight=1)
        self.rowconfigure(0, weight=1)
        self.inner = tk.Frame(self.canvas, background='#f8fafc')
        self.window = self.canvas.create_window((0, 0), window=self.inner, anchor='nw')
        self.rows, self.cards, self.selected, self.pinned, self.ring_images = [], [], None, set(), []
        self.inner.bind('<Configure>', self._sync_scroll)
        self.canvas.bind('<Configure>', self._resize)
        self.canvas.bind_all('<MouseWheel>', self._wheel, add='+')

    def _wheel(self, event):
        if event.widget.winfo_toplevel() == self.winfo_toplevel():
            self.canvas.yview_scroll(-1 * (event.delta // 120), 'units')

    def _sync_scroll(self, _=None):
        self.canvas.configure(scrollregion=self.canvas.bbox('all'))

    def _resize(self, event):
        self.canvas.itemconfigure(self.window, width=event.width)
        if self.rows:
            self.after_idle(self._render)

    @staticmethod
    def key(row):
        return (row.get('accountId'), row.get('groupId'), row.get('bucketId'))

    def set_rows(self, rows, selected=None, pinned=None):
        self.rows = list(rows)
        self.selected = self.key(selected) if isinstance(selected, dict) else self.selected
        if pinned is not None:
            self.pinned = {self.key(row) if isinstance(row, dict) else tuple(row) for row in pinned}
        self._render()

    def _render(self):
        focused = self.focus_get()
        focused_key = next((self.key(row) for card, row in self.cards if card is focused), None)
        for child in self.inner.winfo_children():
            child.destroy()
        self.cards = []
        self.ring_images = []
        if not self.rows:
            tk.Label(self.inner, text='No readable quota windows yet. Open the full dashboard to connect an official client.',
                     background='#f8fafc', foreground='#4b5563', anchor='w').pack(fill='x', padx=10, pady=10)
            return
        groups = {}
        for row in self.rows:
            groups.setdefault((row['accountId'], row['groupId']), []).append(row)
        section_columns = 2 if self.canvas.winfo_width() >= 600 else 1
        card_columns = max(1, (self.canvas.winfo_width() // section_columns) // self.CARD_WIDTH)
        for column in range(section_columns):
            self.inner.columnconfigure(column, weight=1)
        for group_index, (_stable, rows) in enumerate(groups.items()):
            provider, account, group = rows[0]['provider'], rows[0]['account'], rows[0]['group']
            section = tk.Frame(self.inner, background='#f8fafc')
            section.grid(row=group_index // section_columns, column=group_index % section_columns,
                         sticky='new', padx=6, pady=(6, 2))
            section.columnconfigure(0, weight=1)
            tk.Label(section, text=f'{provider}  |  {account}  |  {group}', font=('Segoe UI', 9, 'bold'),
                     background='#f8fafc', foreground='#25374a', anchor='w').grid(row=0, column=0, sticky='ew', pady=(0, 2))
            cards = tk.Frame(section, background='#f8fafc')
            cards.grid(row=1, column=0, sticky='ew')
            for column in range(card_columns):
                cards.columnconfigure(column, weight=1)
            for index, row in enumerate(rows):
                card = self._card(cards, row)
                card.grid(row=index // card_columns, column=index % card_columns, padx=3, pady=3, sticky='ew')
        if focused_key:
            card = next((control for control, row in self.cards if self.key(row) == focused_key), None)
            if card:
                card.focus_set()

    def _card(self, master, row):
        card = tk.Canvas(master, width=self.CARD_WIDTH - 8, height=self.CARD_HEIGHT, background='#ffffff',
                         highlightthickness=1, highlightbackground='#cbd5e1', takefocus=1)
        self.cards.append((card, row))
        outer = _number(row.get('numeric'))
        inner = _number(row.get('timeRemaining'))
        stale = row.get('status') != 'live'
        ring = '#94a3b8' if stale or outer is None else '#22a06b' if outer >= 30 else '#d18a17' if outer >= 10 else '#c2413b'
        from PIL import ImageTk
        image = ImageTk.PhotoImage(render_rings(60, outer, inner, stale=stale, quota_color=ring), master=card)
        self.ring_images.append(image)
        card.create_image(50, 31, image=image)
        center = '∞' if row.get('exact') == 'Unlimited' else row.get('remaining', '?') if outer is not None else '?'
        card.create_text(50, 31, text=center, fill='#263442', font=('Segoe UI', 10, 'bold'))
        card.create_text(50, 64, text=row.get('window', 'Window'), fill='#25374a', font=('Segoe UI', 9, 'bold'))
        if stale:
            card.create_text(50, 76, text='STALE', fill='#a16207', font=('Segoe UI', 7, 'bold'))
        elif outer is None:
            card.create_text(50, 76, text='UNKNOWN', fill='#64748b', font=('Segoe UI', 7, 'bold'))
        pinned = self.key(row) in self.pinned
        card.create_text(87, 10, text='★' if pinned else '☆', fill='#2563eb' if pinned else '#64748b',
                         font=('Segoe UI Symbol', 10), tags=('pin',))
        self._paint_selection(card, row)
        card.bind('<Button-1>', lambda event, item=row: self._clicked(event, item))
        card.bind('<FocusIn>', lambda _event, item=row: self._focused(item))
        card.bind('<Enter>', lambda _event, item=row: self.on_hover(item))
        card.bind('<Leave>', lambda _event: self.on_hover(None))
        card.bind('<Left>', lambda _event: self._move(-1))
        card.bind('<Up>', lambda _event: self._move(-1))
        card.bind('<Right>', lambda _event: self._move(1))
        card.bind('<Down>', lambda _event: self._move(1))
        card.bind('<space>', lambda _event, item=row: self._pin(item))
        return card

    def _clicked(self, event, row):
        event.widget.focus_set()
        if event.x >= 76 and event.y <= 22:
            self._pin(row)
        else:
            self.activate(row)

    def _pin(self, row):
        self.on_pin(row)
        return 'break'

    def _paint_selection(self, card, row):
        card.configure(highlightbackground='#2563eb' if self.key(row) == self.selected else '#cbd5e1', highlightthickness=2 if self.key(row) == self.selected else 1)

    def _focused(self, row):
        self.on_hover(row)

    def activate(self, row):
        self.selected = self.key(row)
        for card, item in self.cards:
            self._paint_selection(card, item)
        self.on_hover(row)
        self.on_select(row)

    def _move(self, delta):
        if not self.cards:
            return 'break'
        current = next((i for i, (_, row) in enumerate(self.cards) if self.key(row) == self.selected), 0)
        target = max(0, min(len(self.cards) - 1, current + delta))
        card, row = self.cards[target]
        card.focus_set()
        self.activate(row)
        return 'break'
