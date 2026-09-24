using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AgentQuotaMonitor;

/// <summary>Compact native WPF quota monitor. The host owns polling and persistence.</summary>
public sealed class MainWindow : Window
{
    internal const double CardWidth = 84, CardHeight = 100;
    private const string RingFormat = "AgentQuotaMonitor.Ring", AccountFormat = "AgentQuotaMonitor.Account";
    private readonly WrapPanel _updateBanner = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(8, 0, 8, 4) };
    private readonly TextBlock _backendError = new() { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.Firebrick, Margin = new Thickness(10, 0, 10, 6) };
    private readonly TextBlock _notice = new() { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap,
        Foreground = Ui.Muted, Margin = new Thickness(10, 0, 10, 6) };
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly Action<QuotaItem> _selectTray;
    private readonly Action<QuotaItem> _togglePin;
    private readonly Action<QuotaItem, int> _moveRing;
    private readonly Action<string, int> _moveAccount;
    private readonly Action<AccountStatus, string> _accountAction;
    private readonly Action _openAccounts;
    private readonly Button _refresh;
    private readonly WrapPanel _groups = new() { Margin = new Thickness(6, 0, 6, 6) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Dictionary<string, CardView> _cards = new();
    private readonly Dictionary<string, Border> _sections = new(StringComparer.Ordinal);
    private IReadOnlyList<QuotaItem> _items = Array.Empty<QuotaItem>();
    private IReadOnlyList<AccountStatus> _accounts = Array.Empty<AccountStatus>();
    private string? _selectedKey;
    private ISet<string> _pins = new HashSet<string>();
    private bool _preferencesEnabled = true;
    private bool _reorderBusy;
    private string _structure = "";
    private Point? _dragStart;

    internal MainWindow(Action<QuotaItem> selectTray, Action<QuotaItem> togglePin, Action showFloating,
        Action refresh, Action quit, Action accounts, Action<QuotaItem, int>? moveRing = null,
        Action<string, int>? moveAccount = null, Action<AccountStatus, string>? accountAction = null)
    {
        _selectTray = selectTray;
        _togglePin = togglePin;
        _openAccounts = accounts;
        _moveRing = moveRing ?? ((_, _) => { });
        _moveAccount = moveAccount ?? ((_, _) => { });
        _accountAction = accountAction ?? ((_, _) => { });
        Title = "Agent Quota Monitor";
        Width = 760;
        Height = 380;
        MinWidth = 420;
        MinHeight = 220;
        WindowStyle = WindowStyle.SingleBorderWindow;
        var root = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)) };
        Content = root;
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(4, 4, 6, 2) };
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        _refresh = Ui.IconButton(Icons.Refresh, "Refresh quotas", refresh);
        toolbar.Children.Add(_refresh);
        toolbar.Children.Add(Ui.IconButton(Icons.PictureInPicture, "Show or hide the floating monitor", showFloating));
        toolbar.Children.Add(Ui.IconButton(Icons.Settings, "Settings and accounts", accounts));
        toolbar.Children.Add(Ui.IconButton(Icons.Power, "Quit Agent Quota Monitor", quit));
        DockPanel.SetDock(_updateBanner, Dock.Top); root.Children.Add(_updateBanner);
        DockPanel.SetDock(_backendError, Dock.Top); root.Children.Add(_backendError);
        DockPanel.SetDock(_notice, Dock.Top); root.Children.Add(_notice);
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); _notice.Visibility = Visibility.Collapsed; };
        _scroll.Content = _groups;
        root.Children.Add(_scroll);
    }

    internal void ShowBackendError(string message)
    {
        _backendError.Text = message;
        _backendError.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Brief neutral feedback for an action, such as a copied command.</summary>
    internal void ShowNotice(string message)
    {
        _notice.Text = message;
        _notice.Visibility = Visibility.Visible;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    internal void SetConnectionState(bool ready, bool connecting)
    {
        _preferencesEnabled = ready && !connecting;
        foreach (var card in _cards.Values)
            card.Select.IsEnabled = card.Pin.IsEnabled = _preferencesEnabled;
        string name = connecting ? "Connecting" : ready ? "Refresh" : "Retry connection";
        AutomationProperties.SetName(_refresh, name);
        _refresh.IsEnabled = !connecting;
        _refresh.ToolTip = connecting ? "Connecting to the quota reader" : ready ? "Refresh quotas while respecting provider cooldowns"
            : "Retry connection to the local quota reader and show its cached readings";
    }

    internal void SetReorderBusy(bool busy) => _reorderBusy = busy;

    internal IReadOnlyDictionary<string, CardView> Cards => _cards;

    /// <summary>Account IDs in the order the main window shows them.</summary>
    internal List<string> AccountOrder => _groups.Children.OfType<Border>().Select(border => border.Tag as string).OfType<string>().ToList();

    /// <param name="install">Installs a verified release in place, or null when this copy cannot do that.</param>
    /// <param name="progress">Replaces the actions while an install is being prepared.</param>
    internal void SetUpdate(UpdateService updates, Action? install = null, string? progress = null)
    {
        _updateBanner.Children.Clear();
        var release = updates.Available;
        _updateBanner.Visibility = release is null ? Visibility.Collapsed : Visibility.Visible;
        if (release is null) return;
        _updateBanner.Children.Add(Label($"Version {release.Tag.TrimStart('v')} is available", true));
        if (progress is not null) { _updateBanner.Children.Add(Label(progress, false)); return; }
        void Open() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(release.Url) { UseShellExecute = true });
        if (install is not null && release.Installer) _updateBanner.Children.Add(Ui.Button("Install update", install, true));
        _updateBanner.Children.Add(Ui.Button("Download update", Open));
        _updateBanner.Children.Add(Ui.Button("Later", updates.Later));
        _updateBanner.Children.Add(Ui.Button("Skip this version", updates.Skip));
        _updateBanner.Children.Add(Ui.Button("Release notes", Open));
    }

    /// <param name="accounts">Account status in backend order. Accounts without rings still get a header and problem banner.</param>
    internal void SetData(IReadOnlyList<QuotaItem> items, string? selectedKey, ISet<string> pins, IReadOnlyList<AccountStatus>? accounts = null)
    {
        _items = items ?? Array.Empty<QuotaItem>();
        _accounts = accounts ?? Array.Empty<AccountStatus>();
        _selectedKey = selectedKey;
        _pins = pins ?? new HashSet<string>();
        var structure = string.Join("|", Sections().Select(section => section.Id + ":" + section.Problem?.Summary + ":" +
            string.Join(",", section.Items.Select(item => item.Key))));
        if (_cards.Count == _items.Count && structure == _structure)
        {
            foreach (var item in _items) UpdateCard(_cards[item.Key], item);
            return;
        }
        _structure = structure;
        RenderGroups();
    }

    private sealed record Section(string Id, string Provider, string Account, AccountProblem? Problem, AccountStatus? Status, QuotaItem[] Items);

    private IEnumerable<Section> Sections()
    {
        var byAccount = _items.GroupBy(item => item.AccountId).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (AccountStatus account in _accounts)
        {
            QuotaItem[] rings = byAccount.GetValueOrDefault(account.Id) ?? Array.Empty<QuotaItem>();
            AccountProblem? problem = account.Problem;
            if (rings.Length == 0 && problem is null) continue;
            seen.Add(account.Id);
            yield return new Section(account.Id, account.Provider, account.Label, problem, account, rings);
        }
        foreach ((string id, QuotaItem[] rings) in byAccount)
            if (seen.Add(id)) yield return new Section(id, rings[0].Provider, rings[0].Account, null, null, rings);
    }

    private void RenderGroups()
    {
        var focus = Keyboard.FocusedElement as FrameworkElement;
        var focusCard = _cards.Values.FirstOrDefault(card => card.Select == focus || card.Pin == focus);
        var focusWasPin = focusCard?.Pin == focus;
        var offset = _scroll.VerticalOffset;
        _groups.Children.Clear();
        _cards.Clear();
        _sections.Clear();
        var sections = Sections().ToArray();
        foreach (var section in sections)
        {
            var border = new Border { Margin = new Thickness(2, 2, 10, 6), Padding = new Thickness(2), BorderThickness = new Thickness(2, 0, 0, 0),
                BorderBrush = Brushes.Transparent, Background = Brushes.Transparent, AllowDrop = true, Tag = section.Id };
            var panel = new StackPanel();
            border.Child = panel;
            panel.Children.Add(Header(section));
            if (section.Problem is not null && section.Status is not null) panel.Children.Add(Banner(section.Status, section.Problem));
            if (section.Items.Length > 0)
            {
                var cards = new WrapPanel();
                foreach (var item in section.Items) cards.Children.Add(CreateCard(item).Border);
                panel.Children.Add(cards);
            }
            border.DragOver += (_, e) => AccountDragOver(border, e);
            border.DragLeave += (_, _) => border.BorderBrush = Brushes.Transparent;
            border.Drop += (_, e) => AccountDrop(section.Id, border, e);
            _sections[section.Id] = border;
            _groups.Children.Add(border);
        }
        if (_groups.Children.Count == 0)
            _groups.Children.Add(Label("No readable quota windows yet. Open Settings to connect an official client.", false));
        Dispatcher.BeginInvoke(() =>
        {
            _scroll.ScrollToVerticalOffset(offset);
            if (focusCard is not null && _cards.TryGetValue(focusCard.Item.Key, out var replacement))
                (focusWasPin ? (Control)replacement.Pin : replacement.Select).Focus();
        }, DispatcherPriority.Loaded);
    }

    private FrameworkElement Header(Section section)
    {
        var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center,
            Fill = Ui.Hex(section.Items.FirstOrDefault()?.IdentityColor ?? ProviderColor(section.Provider)) };
        var provider = new StackPanel { Orientation = Orientation.Horizontal };
        provider.Children.Add(dot);
        provider.Children.Add(new TextBlock { Text = ProviderName(section.Provider), FontSize = 11, Foreground = Ui.Muted });
        var header = new StackPanel { Margin = new Thickness(3, 0, 3, 3), Background = Brushes.Transparent, Cursor = Cursors.SizeAll,
            ToolTip = ProviderName(section.Provider) + " · " + section.Account + "\nDrag to reorder accounts. Alt+Up or Alt+Down on a ring also moves its account." };
        header.Children.Add(provider);
        header.Children.Add(new TextBlock { Text = section.Account, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Text,
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = Math.Max(CardWidth * Math.Max(section.Items.Length, 2) - 6, 160), TextTrimming = TextTrimming.CharacterEllipsis });
        header.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(this);
        header.PreviewMouseMove += (_, e) =>
        {
            if (!DragReady(e) || !_preferencesEnabled || _reorderBusy) return;
            _dragStart = null;
            DragDrop.DoDragDrop(header, new DataObject(AccountFormat, section.Id), DragDropEffects.Move);
        };
        return header;
    }

    private FrameworkElement Banner(AccountStatus account, AccountProblem problem)
    {
        var row = new DockPanel { LastChildFill = true };
        if (problem.Action is not null && problem.ActionLabel is not null)
        {
            var action = Ui.Button(problem.ActionLabel, () => _accountAction(account, problem.Action));
            action.Margin = new Thickness(6, 0, 0, 0);
            AutomationProperties.SetName(action, problem.ActionLabel + " for " + ProviderName(account.Provider) + " " + account.Label);
            DockPanel.SetDock(action, Dock.Right);
            row.Children.Add(action);
        }
        var icon = Icons.Create(Icons.Alert, 14, Ui.WarningText);
        icon.Margin = new Thickness(0, 0, 6, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = problem.Summary, FontSize = 11.5, Foreground = Ui.WarningText, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center });
        return new Border
        {
            Child = row, Background = Ui.WarningFill, BorderBrush = Ui.WarningLine, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 4, 5, 4), Margin = new Thickness(1, 0, 1, 5),
            MaxWidth = 380, MinWidth = 230, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = account.Guidance,
            Tag = "problem:" + account.Id
        };
    }

    private CardView CreateCard(QuotaItem item)
    {
        var border = new Border
        {
            Width = CardWidth, Height = CardHeight, Margin = new Thickness(2), CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1.5), Background = Brushes.White, AllowDrop = true, Focusable = false
        };
        var overlay = new Grid();
        border.Child = overlay;
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        overlay.Children.Add(stack);
        var donut = new Donut { Width = 58, Height = 58 };
        var select = new Button { Content = donut, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Focusable = true, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Center };
        select.Template = TransparentTemplate();
        stack.Children.Add(select);
        var label = new FitText { Width = CardWidth - 8, Height = 16, FontSize = 11, Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(label);
        var tray = Icons.Create(Icons.Inbox, 12, Ui.Accent);
        tray.HorizontalAlignment = HorizontalAlignment.Left; tray.VerticalAlignment = VerticalAlignment.Top; tray.Margin = new Thickness(6, 5, 0, 0);
        tray.ToolTip = "Shown in the system tray";
        overlay.Children.Add(tray);
        var stale = new Border
        {
            Background = Ui.WarningFill, BorderBrush = Ui.WarningLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
            Padding = new Thickness(5, 0, 5, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 0, 0), IsHitTestVisible = false,
            Child = new TextBlock { Text = "stale", FontSize = 9, Foreground = Ui.WarningText }
        };
        overlay.Children.Add(stale);
        var insertLeft = new Rectangle { Width = 3, Fill = Ui.Accent, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed, RadiusX = 1.5, RadiusY = 1.5 };
        var insertRight = new Rectangle { Width = 3, Fill = Ui.Accent, HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Collapsed, RadiusX = 1.5, RadiusY = 1.5 };
        overlay.Children.Add(insertLeft); overlay.Children.Add(insertRight);
        var glyph = PinGlyph();
        var pin = PinButton(glyph);
        overlay.Children.Add(pin);
        var card = new CardView(item, border, donut, select, label, tray, stale, pin, glyph, insertLeft, insertRight);
        select.Click += (_, _) => _selectTray(card.Item);
        pin.Click += (_, _) => _togglePin(card.Item);
        select.PreviewKeyDown += (_, e) => CardKey(card, e);
        border.ContextMenu = new ContextMenu();
        border.ContextMenuOpening += (_, _) => FillMenu(border.ContextMenu, card);
        select.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(this);
        select.PreviewMouseMove += (_, e) =>
        {
            if (!DragReady(e) || !_preferencesEnabled || _reorderBusy) return;
            _dragStart = null;
            select.ReleaseMouseCapture();
            DragDrop.DoDragDrop(border, new DataObject(RingFormat, card.Item.Key), DragDropEffects.Move);
        };
        border.DragOver += (_, e) => RingDragOver(card, e);
        border.DragLeave += (_, _) => { card.InsertLeft.Visibility = card.InsertRight.Visibility = Visibility.Collapsed; };
        border.Drop += (_, e) => RingDrop(card, e);
        _cards.Add(item.Key, card);
        UpdateCard(card, item);
        return card;
    }

    private void UpdateCard(CardView card, QuotaItem item)
    {
        card.Item = item;
        card.Select.IsEnabled = card.Pin.IsEnabled = _preferencesEnabled;
        bool selected = item.Key == _selectedKey;
        card.Border.BorderBrush = selected ? Ui.Accent : Ui.Hairline;
        card.Border.Background = selected ? Ui.AccentTint : Brushes.White;
        card.Border.ToolTip = item.Tooltip;
        card.Select.ToolTip = item.Tooltip + "\nOuter ring quota · Gray inner ring time\nClick to show in the tray · Drag to reorder · Right-click for more";
        AutomationProperties.SetName(card.Select, item.Label + " " + item.Account + (selected ? ", shown in tray" : "") + (item.Stale ? ", stale" : ""));
        card.Donut.Item = item;
        card.Label.Set(item.Label, item.ShortLabel);
        card.Label.Foreground = item.Stale ? Ui.Muted : Ui.Text;
        card.Tray.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        card.StaleBadge.Visibility = item.Stale ? Visibility.Visible : Visibility.Collapsed;
        // Filled violet means pinned, hollow slate means not pinned. Neither reuses a
        // provider identity color or the blue tray selection.
        var pinned = _pins.Contains(item.Key);
        card.PinGlyph.Stroke = pinned ? PinnedBrush : UnpinnedBrush;
        card.PinGlyph.Fill = pinned ? PinnedBrush : null;
        card.Pin.ToolTip = pinned ? "Unpin from floating monitor" : "Pin to floating monitor";
        AutomationProperties.SetName(card.Pin, (pinned ? "Unpin " : "Pin ") + item.Provider + " " + item.Account + " " + item.Group + " " + item.Window
            + (pinned ? " from the floating monitor" : " to the floating monitor"));
        AutomationProperties.SetHelpText(card.Pin, "Changes the floating monitor without changing the tray selection.");
    }

    /// <summary>Neighbors inside the same account decide which ring moves are possible.</summary>
    private (int Index, int Count) RingPosition(QuotaItem item)
    {
        var rings = _items.Where(row => row.AccountId == item.AccountId).ToList();
        return (rings.FindIndex(row => row.Key == item.Key), rings.Count);
    }

    private (int Index, int Count) AccountPosition(string accountId)
    {
        var order = _groups.Children.OfType<Border>().Select(border => border.Tag as string).Where(id => id is not null).ToList();
        return (order.IndexOf(accountId), order.Count);
    }

    internal void FillMenu(ContextMenu menu, CardView card)
    {
        menu.Items.Clear();
        var item = card.Item;
        bool enabled = _preferencesEnabled && !_reorderBusy;
        bool pinned = _pins.Contains(item.Key);
        (int ring, int rings) = RingPosition(item);
        (int account, int accounts) = AccountPosition(item.AccountId);
        void Add(string header, bool allowed, Action action, string? gesture = null)
        {
            var entry = new MenuItem { Header = header, IsEnabled = allowed, InputGestureText = gesture ?? "" };
            entry.Click += (_, _) => action();
            menu.Items.Add(entry);
        }
        Add("Show in tray", enabled && item.Key != _selectedKey, () => _selectTray(card.Item));
        Add(pinned ? "Unpin from floating monitor" : "Pin to floating monitor", enabled, () => _togglePin(card.Item));
        menu.Items.Add(new Separator());
        Add("Move left", enabled && ring > 0, () => _moveRing(card.Item, -1), "Alt+Left");
        Add("Move right", enabled && ring >= 0 && ring < rings - 1, () => _moveRing(card.Item, 1), "Alt+Right");
        Add("Move account up", enabled && account > 0, () => _moveAccount(card.Item.AccountId, -1), "Alt+Up");
        Add("Move account down", enabled && account >= 0 && account < accounts - 1, () => _moveAccount(card.Item.AccountId, 1), "Alt+Down");
        menu.Items.Add(new Separator());
        Add("Copy details", true, () => CopyDetails(card.Item));
        Add("Account details", true, _openAccounts);
    }

    private void CopyDetails(QuotaItem item)
    {
        string account = _accounts.FirstOrDefault(row => row.Id == item.AccountId)?.Diagnostics() ?? "";
        string ring = $"Quota · {item.Label} ({item.Group}, {item.Window})\nReading · {item.Tooltip.Split('\n').Skip(1).FirstOrDefault()}\nReset · {item.ResetText}";
        try { Clipboard.SetText((account.Length > 0 ? account + Environment.NewLine : "") + ring); ShowNotice("Details copied. They contain no credentials or account label."); }
        catch (System.Runtime.InteropServices.ExternalException) { ShowNotice("The clipboard is busy. Try again."); }
    }

    private void CardKey(CardView card, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Apps || key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            card.Border.ContextMenu.PlacementTarget = card.Border;
            FillMenu(card.Border.ContextMenu, card);
            card.Border.ContextMenu.IsOpen = true;
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.Alt || !_preferencesEnabled || _reorderBusy) return;
        (int ring, int rings) = RingPosition(card.Item);
        (int account, int accounts) = AccountPosition(card.Item.AccountId);
        switch (key)
        {
            case Key.Left when ring > 0: _moveRing(card.Item, -1); break;
            case Key.Right when ring >= 0 && ring < rings - 1: _moveRing(card.Item, 1); break;
            case Key.Up when account > 0: _moveAccount(card.Item.AccountId, -1); break;
            case Key.Down when account >= 0 && account < accounts - 1: _moveAccount(card.Item.AccountId, 1); break;
            default: return;
        }
        e.Handled = true;
    }

    private bool DragReady(MouseEventArgs e)
    {
        if (_dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed) return false;
        Vector moved = e.GetPosition(this) - start;
        return Math.Abs(moved.X) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(moved.Y) >= SystemParameters.MinimumVerticalDragDistance;
    }

    /// <summary>The offset a dropped ring moves by, or null when the drop is not allowed.</summary>
    internal int? RingOffset(string draggedKey, QuotaItem target, bool after)
    {
        var dragged = _items.FirstOrDefault(row => row.Key == draggedKey);
        if (dragged is null || dragged.AccountId != target.AccountId || dragged.Key == target.Key) return null;
        (int from, _) = RingPosition(dragged);
        (int to, _) = RingPosition(target);
        // Position in the list once the dragged ring is removed, then before or after the target.
        int insert = (to > from ? to - 1 : to) + (after ? 1 : 0);
        int offset = insert - from;
        return offset == 0 ? null : offset;
    }

    private void RingDragOver(CardView card, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        card.InsertLeft.Visibility = card.InsertRight.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(RingFormat) is not string key) return;
        bool after = e.GetPosition(card.Border).X > CardWidth / 2;
        if (RingOffset(key, card.Item, after) is null) return;
        e.Effects = DragDropEffects.Move;
        (after ? card.InsertRight : card.InsertLeft).Visibility = Visibility.Visible;
    }

    private void RingDrop(CardView card, DragEventArgs e)
    {
        card.InsertLeft.Visibility = card.InsertRight.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(RingFormat) is not string key) return;
        e.Handled = true;
        var dragged = _items.FirstOrDefault(row => row.Key == key);
        if (dragged is not null && RingOffset(key, card.Item, e.GetPosition(card.Border).X > CardWidth / 2) is int offset)
            _moveRing(dragged, offset);
    }

    /// <summary>The offset a dropped account moves by, or null when the drop is not allowed.</summary>
    internal int? AccountOffset(string draggedId, string targetId)
    {
        (int from, _) = AccountPosition(draggedId);
        (int to, _) = AccountPosition(targetId);
        return from < 0 || to < 0 || from == to ? null : to - from;
    }

    private void AccountDragOver(Border border, DragEventArgs e)
    {
        if (e.Data.GetData(AccountFormat) is not string id) return;
        e.Handled = true;
        bool allowed = AccountOffset(id, (string)border.Tag) is not null;
        e.Effects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        border.BorderBrush = allowed ? Ui.Accent : Brushes.Transparent;
    }

    private void AccountDrop(string targetId, Border border, DragEventArgs e)
    {
        border.BorderBrush = Brushes.Transparent;
        if (e.Data.GetData(AccountFormat) is not string id) return;
        e.Handled = true;
        if (AccountOffset(id, targetId) is int offset) _moveAccount(id, offset);
    }

    internal static string ProviderName(string provider) => provider switch
    {
        "codex" => "OpenAI Codex", "claude" => "Anthropic Claude", "antigravity" => "Google Antigravity", "copilot" => "GitHub Copilot", _ => provider
    };

    private static string ProviderColor(string provider) => provider switch
    {
        "codex" => "#168f87", "claude" => "#c46843", "antigravity" => "#287bc1", "copilot" => "#488b74", _ => "#64748b"
    };

    internal sealed class CardView
    {
        public CardView(QuotaItem item, Border border, Donut donut, Button select, FitText label, FrameworkElement tray, FrameworkElement staleBadge,
            Button pin, Path pinGlyph, Rectangle insertLeft, Rectangle insertRight) =>
            (Item, Border, Donut, Select, Label, Tray, StaleBadge, Pin, PinGlyph, InsertLeft, InsertRight) =
            (item, border, donut, select, label, tray, staleBadge, pin, pinGlyph, insertLeft, insertRight);
        public QuotaItem Item { get; set; }
        public Border Border { get; }
        public Donut Donut { get; }
        public Button Select { get; }
        public FitText Label { get; }
        public FrameworkElement Tray { get; }
        public FrameworkElement StaleBadge { get; }
        public Button Pin { get; }
        public Path PinGlyph { get; }
        public Rectangle InsertLeft { get; }
        public Rectangle InsertRight { get; }
    }

    // Lucide "pin", ISC licensed, drawn on its original 24 unit grid inside a Viewbox
    // so the stroke scales with the glyph.
    private const string PinPath = "M12 17v5 M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1z";

    private static readonly Brush PinnedBrush = Ui.Freeze(Color.FromRgb(124, 58, 237));
    private static readonly Brush UnpinnedBrush = Ui.Freeze(Color.FromRgb(100, 116, 139));

    private static Path PinGlyph() => new()
    {
        Data = Geometry.Parse(PinPath),
        StrokeThickness = 2,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        Width = 24,
        Height = 24,
        Stretch = Stretch.None
    };

    private static ControlTemplate TransparentTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        template.VisualTree = content;
        return template;
    }

    /// <summary>
    /// A 19 unit hit target whose visible chrome is inset to 13 units, so the glyph and any
    /// hover highlight stay in the card corner outside the quota ring.
    /// </summary>
    private static Button PinButton(Path glyph)
    {
        var button = new Button
        {
            Content = new Viewbox { Width = 12, Height = 12, Child = glyph },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Width = 19, Height = 19, Margin = new Thickness(0, 1, 1, 0),
            Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Cursor = Cursors.Hand,
            ToolTip = "Pin to floating monitor"
        };
        var template = new ControlTemplate(typeof(Button));
        var hitTarget = new FrameworkElementFactory(typeof(Grid));
        hitTarget.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
        var chrome = new FrameworkElementFactory(typeof(Border));
        chrome.SetValue(Border.MarginProperty, new Thickness(3));
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(content);
        hitTarget.AppendChild(chrome);
        template.VisualTree = hitTarget;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Ui.Pressed));
        template.Triggers.Add(hover);
        button.Template = template;
        return button;
    }

    private static TextBlock Label(string text, bool strong) => new()
    {
        Text = text, Margin = new Thickness(3, 1, 9, 1), VerticalAlignment = VerticalAlignment.Center,
        FontSize = strong ? 11 : 12, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85))
    };
}
