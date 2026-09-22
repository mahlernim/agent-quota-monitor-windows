using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace AgentQuotaMonitor;

/// <summary>Compact native WPF quota monitor. The host owns polling and persistence.</summary>
public sealed class MainWindow : Window
{
    private readonly WrapPanel _updateBanner = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(8, 0, 8, 4) };
    private readonly TextBlock _backendError = new() { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.Firebrick, Margin = new Thickness(8, 0, 8, 6) };
    private readonly Action<QuotaItem> _selectTray;
    private readonly Action<QuotaItem> _togglePin;
    private readonly WrapPanel _groups = new() { Margin = new Thickness(6) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Dictionary<string, CardView> _cards = new();
    private IReadOnlyList<QuotaItem> _items = Array.Empty<QuotaItem>();
    private string? _selectedKey;
    private ISet<string> _pins = new HashSet<string>();

    public MainWindow(Action<QuotaItem> selectTray, Action<QuotaItem> togglePin, Action showFloating,
        Action refresh, Action quit, Action accounts)
    {
        _selectTray = selectTray;
        _togglePin = togglePin;
        Title = "Agent Quota Monitor";
        Width = 760;
        Height = 360;
        MinWidth = 460;
        MinHeight = 200;
        WindowStyle = WindowStyle.SingleBorderWindow;
        var root = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)) };
        Content = root;
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(4, 3, 4, 2) };
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        toolbar.Children.Add(Button("Refresh", refresh));
        toolbar.Children.Add(Button("Floating", showFloating));
        toolbar.Children.Add(Button("Settings", accounts));
        toolbar.Children.Add(Button("Quit", quit));
        DockPanel.SetDock(_updateBanner, Dock.Top); root.Children.Add(_updateBanner);
        DockPanel.SetDock(_backendError, Dock.Top); root.Children.Add(_backendError);
        _scroll.Content = _groups;
        root.Children.Add(_scroll);
    }

    internal void ShowBackendError(string message)
    {
        _backendError.Text = message;
        _backendError.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void SetUpdate(UpdateService updates)
    {
        _updateBanner.Children.Clear();
        var release = updates.Available;
        _updateBanner.Visibility = release is null ? Visibility.Collapsed : Visibility.Visible;
        if (release is null) return;
        _updateBanner.Children.Add(Label($"Version {release.Tag.TrimStart('v')} is available", true));
        void Open() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(release.Url) { UseShellExecute = true });
        _updateBanner.Children.Add(Button("Download update", Open));
        _updateBanner.Children.Add(Button("Later", updates.Later));
        _updateBanner.Children.Add(Button("Skip this version", updates.Skip));
        _updateBanner.Children.Add(Button("Release notes", Open));
    }

    public void SetData(IReadOnlyList<QuotaItem> items, string? selectedKey, ISet<string> pins)
    {
        _items = items ?? Array.Empty<QuotaItem>();
        _selectedKey = selectedKey;
        _pins = pins ?? new HashSet<string>();
        var structure = string.Join("|", _items.Select(item => $"{item.AccountId}/{item.GroupId}/{item.Key}"));
        if (_cards.Count == _items.Count && structure == _structure)
        {
            foreach (var item in _items) UpdateCard(_cards[item.Key], item);
            return;
        }
        _structure = structure;
        RenderGroups();
    }

    private string _structure = "";

    private void RenderGroups()
    {
        var focus = Keyboard.FocusedElement as FrameworkElement;
        var focusCard = _cards.Values.FirstOrDefault(card => card.Select == focus || card.Pin == focus);
        var focusWasPin = focusCard?.Pin == focus;
        var offset = _scroll.VerticalOffset;
        _groups.Children.Clear();
        _cards.Clear();
        foreach (var group in _items.GroupBy(item => (item.AccountId, item.Provider)))
        {
            var section = new Border { BorderBrush = Brushes.Transparent, Margin = new Thickness(2), Padding = new Thickness(2) };
            var panel = new StackPanel();
            section.Child = panel;
            panel.Children.Add(Label($"{group.First().Provider} · {group.First().Account}", true));
            var cards = new WrapPanel();
            foreach (var item in group) cards.Children.Add(CreateCard(item).Border);
            panel.Children.Add(cards);
            _groups.Children.Add(section);
        }
        if (_groups.Children.Count == 0)
            _groups.Children.Add(Label("No readable quota windows yet. Open Settings to connect an official client.", false));
        Dispatcher.BeginInvoke(() =>
        {
            _scroll.ScrollToVerticalOffset(offset);
            if (focusCard is not null && _cards.TryGetValue(focusCard.Item.Key, out var replacement))
                (focusWasPin ? (Control)replacement.Pin : replacement.Select).Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private CardView CreateCard(QuotaItem item)
    {
        var border = new Border
        {
            Width = 82, Margin = new Thickness(1), Padding = new Thickness(1),
            Background = Brushes.White, Focusable = false
        };
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var overlay = new Grid();
        border.Child = overlay;
        overlay.Children.Add(stack);
        var donut = new Donut();
        var select = new Button { Content = donut, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Focusable = true };
        stack.Children.Add(select);
        var window = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11, FontWeight = FontWeights.SemiBold };
        var status = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, FontSize = 8 };
        stack.Children.Add(window); stack.Children.Add(status);
        var glyph = PinGlyph();
        var pin = PinButton(glyph);
        overlay.Children.Add(pin);
        var card = new CardView(item, border, donut, select, window, status, pin, glyph);
        select.Click += (_, _) => _selectTray(card.Item);
        pin.Click += (_, _) => _togglePin(card.Item);
        _cards.Add(item.Key, card);
        UpdateCard(card, item);
        return card;
    }

    private void UpdateCard(CardView card, QuotaItem item)
    {
        card.Item = item;
        var selected = item.Key == _selectedKey;
        card.Border.BorderBrush = selected ? Brushes.DodgerBlue : new SolidColorBrush(Color.FromRgb(203, 213, 225));
        card.Border.BorderThickness = new Thickness(selected ? 2 : 1);
        card.Border.ToolTip = item.Tooltip;
        card.Select.ToolTip = item.Tooltip + "\nOuter ring quota · Gray inner ring time\nClick to show this quota in the system tray";
        card.Donut.Item = item;
        card.Window.Text = item.Provider == "copilot" ? item.Group.Contains("Inline") ? "Inline" : item.Group.Contains("Premium") ? "Premium" : "Included" : item.Code + " · " + item.Window;
        card.Status.Text = item.Stale ? (selected ? "STALE · Tray" : "STALE") : selected ? "Tray" : "";
        card.Status.Visibility = card.Status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        card.Status.Foreground = item.Stale ? Brushes.DarkGoldenrod : Brushes.SlateGray;
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

    private sealed class CardView
    {
        public CardView(QuotaItem item, Border border, Donut donut, Button select, TextBlock window, TextBlock status, Button pin, Path pinGlyph) =>
            (Item, Border, Donut, Select, Window, Status, Pin, PinGlyph) = (item, border, donut, select, window, status, pin, pinGlyph);
        public QuotaItem Item { get; set; }
        public Border Border { get; }
        public Donut Donut { get; }
        public Button Select { get; }
        public TextBlock Window { get; }
        public TextBlock Status { get; }
        public Button Pin { get; }
        public Path PinGlyph { get; }
    }

    // Lucide "pin", ISC licensed, drawn on its original 24 unit grid inside a Viewbox
    // so the stroke scales with the glyph.
    private const string PinPath = "M12 17v5 M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1z";

    private static readonly Brush PinnedBrush = Freeze(Color.FromRgb(124, 58, 237));
    private static readonly Brush UnpinnedBrush = Freeze(Color.FromRgb(100, 116, 139));

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

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
            Width = 19, Height = 19, Margin = new Thickness(0, -3, -3, 0),
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
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Freeze(Color.FromRgb(226, 232, 240))));
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

    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(1), Padding = new Thickness(9, 4, 9, 4), MinHeight = 26,
            Background = Brushes.Transparent, Foreground = Brushes.DarkSlateGray, BorderThickness = new Thickness(0), Cursor = Cursors.Hand };
        var template = new ControlTemplate(typeof(Button));
        var frame = new FrameworkElementFactory(typeof(Border));
        frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        frame.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        frame.AppendChild(content); template.VisualTree = frame;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(225, 232, 238))));
        template.Triggers.Add(hover); button.Template = template;
        button.Click += (_, _) => action();
        return button;
    }
}
