using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AgentQuotaMonitor;

public sealed class FloatingWindow : Window
{
    private readonly Action _openMain;
    private readonly Action _hideRequested;
    private readonly StackPanel _items;

    public FloatingWindow(Action openMain, Action hide)
    {
        _openMain = openMain ?? throw new ArgumentNullException(nameof(openMain));
        _hideRequested = hide ?? throw new ArgumentNullException(nameof(hide));

        Title = "Agent Quota Monitor";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true;
        ShowInTaskbar = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        _items = new StackPanel { Orientation = Orientation.Horizontal };
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(238, 247, 248, 249)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(184, 193, 201)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(4, 3, 4, 3),
            Child = _items
        };

        MouseLeftButtonDown += (_, eventArgs) =>
        {
            if (eventArgs.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };
        MouseDoubleClick += (_, _) => _openMain();
        Closing += HideInsteadOfClose;
        ContextMenu = BuildContextMenu();
    }

    public void SetData(IReadOnlyList<QuotaItem> rows)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetData(rows));
            return;
        }

        _items.Children.Clear();
        if (rows is null || rows.Count == 0)
        {
            _items.Children.Add(new TextBlock
            {
                Text = "No pinned quotas",
                Foreground = Brushes.DimGray,
                FontSize = 11,
                Margin = new Thickness(8, 16, 8, 16)
            });
            return;
        }

        foreach (QuotaItem item in rows)
        {
            var cell = new StackPanel
            {
                Width = 58,
                Orientation = Orientation.Vertical,
                ToolTip = item.Tooltip
            };
            cell.Children.Add(new Donut
            {
                Item = item,
                Width = 48,
                Height = 48,
                HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = item.Tooltip
            });
            cell.Children.Add(new TextBlock
            {
                Text = (item.Code ?? "?") + (item.Window ?? string.Empty),
                Foreground = item.Stale ? Brushes.Gray : Brushes.DarkSlateGray,
                FontSize = 9,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            _items.Children.Add(cell);
        }
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        var show = new MenuItem { Header = "Show monitor" };
        show.Click += (_, _) => _openMain();
        menu.Items.Add(show);

        var opacity = new MenuItem { Header = "Opacity" };
        foreach ((string label, double value) in new[] { ("100%", 1.0), ("85%", 0.85), ("70%", 0.70) })
        {
            var choice = new MenuItem { Header = label, IsCheckable = true, Tag = value };
            choice.Click += (_, _) =>
            {
                Opacity = (double)choice.Tag;
                foreach (object sibling in opacity.Items)
                    if (sibling is MenuItem item)
                        item.IsChecked = ReferenceEquals(item, choice);
            };
            if (value == 1.0)
                choice.IsChecked = true;
            opacity.Items.Add(choice);
        }
        menu.Items.Add(opacity);

        var hide = new MenuItem { Header = "Hide floating monitor" };
        hide.Click += (_, _) => RequestHide();
        menu.Items.Add(hide);
        return menu;
    }

    private void HideInsteadOfClose(object? sender, CancelEventArgs eventArgs)
    {
        eventArgs.Cancel = true;
        RequestHide();
    }

    private void RequestHide()
    {
        Hide();
        _hideRequested();
    }
}
