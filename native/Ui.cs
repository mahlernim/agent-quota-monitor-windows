using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AgentQuotaMonitor;

/// <summary>Shared compact controls for the main window and Settings.</summary>
internal static class Ui
{
    internal static readonly Brush Text = Freeze(Color.FromRgb(37, 49, 61));
    internal static readonly Brush Muted = Freeze(Color.FromRgb(100, 116, 139));
    internal static readonly Brush Hairline = Freeze(Color.FromRgb(203, 213, 225));
    internal static readonly Brush Hover = Freeze(Color.FromRgb(238, 242, 246));
    internal static readonly Brush Pressed = Freeze(Color.FromRgb(226, 232, 240));
    internal static readonly Brush Accent = Freeze(Color.FromRgb(37, 99, 235));
    internal static readonly Brush AccentTint = Freeze(Color.FromRgb(239, 246, 255));
    internal static readonly Brush WarningText = Freeze(Color.FromRgb(133, 79, 11));
    internal static readonly Brush WarningFill = Freeze(Color.FromRgb(254, 247, 231));
    internal static readonly Brush WarningLine = Freeze(Color.FromRgb(240, 214, 160));

    internal static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    internal static Brush Hex(string hex)
    {
        try { return Freeze((Color)ColorConverter.ConvertFromString(hex)); }
        catch (FormatException) { return Muted; }
    }

    /// <summary>A 26 unit text button with 6 unit corners, a hairline border, and a soft hover fill.</summary>
    internal static Button Button(string text, Action action, bool primary = false)
    {
        var button = Apply(new Button { Content = text }, primary);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>Applies the shared compact style to an existing button without changing its content or handlers.</summary>
    internal static T Apply<T>(T button, bool primary = false) where T : Button
    {
        button.MinHeight = 26;
        button.Padding = new Thickness(10, 3, 10, 3);
        if (button.Margin == default) button.Margin = new Thickness(2, 1, 2, 1);
        button.Background = primary ? Accent : Brushes.White;
        button.Foreground = primary ? Brushes.White : Text;
        button.BorderBrush = primary ? Accent : Hairline;
        button.BorderThickness = new Thickness(1);
        button.Cursor = Cursors.Hand;
        button.FontSize = 12;
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Template = Template(primary ? Accent : Hover, primary ? Accent : Pressed, 6);
        return button;
    }

    /// <summary>A small rounded status chip, such as "live" or "stale".</summary>
    internal static Border Chip(string text, bool healthy) => new()
    {
        Background = healthy ? Freeze(Color.FromRgb(234, 243, 222)) : WarningFill,
        BorderBrush = healthy ? Freeze(Color.FromRgb(192, 221, 151)) : WarningLine,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 0, 7, 1),
        Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = healthy ? Freeze(Color.FromRgb(39, 80, 10)) : WarningText }
    };

    /// <summary>An icon-only button. The tooltip names its function and doubles as the accessible name.</summary>
    internal static Button IconButton(string icon, string tooltip, Action action, double size = 16, double box = 28)
    {
        var button = new Button
        {
            Content = Icons.Create(icon, size, Muted), Width = box, Height = box, Padding = new Thickness(0),
            Margin = new Thickness(1), Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1), Cursor = Cursors.Hand, ToolTip = tooltip
        };
        ToolTipService.SetInitialShowDelay(button, 300);
        AutomationProperties.SetName(button, tooltip);
        button.Template = Template(Hover, Pressed, 6);
        button.Click += (_, _) => action();
        return button;
    }

    private static ControlTemplate Template(Brush hover, Brush pressed, double radius)
    {
        var template = new ControlTemplate(typeof(Button));
        var frame = new FrameworkElementFactory(typeof(Border)) { Name = "frame" };
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        frame.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        frame.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        frame.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        frame.AppendChild(content);
        template.VisualTree = frame;
        var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(Border.BackgroundProperty, hover, "frame"));
        var down = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
        down.Setters.Add(new Setter(Border.BackgroundProperty, pressed, "frame"));
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "frame"));
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        template.Triggers.Add(over); template.Triggers.Add(down); template.Triggers.Add(focus); template.Triggers.Add(disabled);
        return template;
    }
}
