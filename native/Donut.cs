using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AgentQuotaMonitor;

/// <summary>Vector WPF quota donut. Outer is remaining quota, inner is remaining time.</summary>
public sealed class Donut : FrameworkElement
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(QuotaItem), typeof(Donut),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public QuotaItem? Item
    {
        get => (QuotaItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public static readonly DependencyProperty MainPresentationProperty = DependencyProperty.Register(
        nameof(MainPresentation), typeof(bool), typeof(Donut),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool MainPresentation
    {
        get => (bool)GetValue(MainPresentationProperty);
        set => SetValue(MainPresentationProperty, value);
    }

    public Donut()
    {
        Width = Height = 56;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var item = Item;
        if (item is null) return;
        var side = Math.Min(ActualWidth, ActualHeight);
        if (side < 12) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var outerRadius = side / 2 - 4;
        var innerRadius = outerRadius - 4;
        var track = new SolidColorBrush(Color.FromRgb(222, 227, 233));
        track.Freeze();
        DrawTrack(dc, center, outerRadius, 6, track);
        var tint = ToBrush(MainPresentation ? RingAppearance.For(item) : item.Color, Color.FromRgb(86, 100, 119));
        if (!item.Stale && !item.Disabled && item.Remaining is double remaining)
            DrawProgress(dc, center, outerRadius, 6, Clamp(remaining), tint);
        if (!item.Stale && !item.Disabled && item.TimeRemaining is double time)
        {
            DrawTrack(dc, center, innerRadius, 2, track);
            DrawProgress(dc, center, innerRadius, 2, Clamp(time), Brushes.Gray);
        }
        var text = LabelFor(item);
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var fontSize = text.Length >= 5 ? 8.0 : 10.0;
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface,
            fontSize, ToBrush(item.NumberColor, Colors.Black), dip);
        while (formatted.Width > innerRadius * 2 - 3 && fontSize > 6)
        {
            fontSize -= 1;
            formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface,
                fontSize, ToBrush(item.NumberColor, Colors.Black), dip);
        }
        var countdown = MainPresentation ? item.Countdown(DateTimeOffset.UtcNow) : "";
        if (countdown.Length == 0 || innerRadius < 8)
            dc.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
        else
        {
            var regular = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var reset = new FormattedText(countdown, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, regular,
                9, ToBrush("#637996", Colors.Gray), dip);
            // Keep a two-DIP gap and fit the text rectangle inside the inner circle, including its corners.
            const double gap = 2;
            var radius = innerRadius - 2;
            var percentageBounds = formatted.BuildGeometry(new Point()).Bounds;
            var resetBounds = reset.BuildGeometry(new Point()).Bounds;
            var height = percentageBounds.Height + gap + resetBounds.Height;
            var width = Math.Max(percentageBounds.Width, resetBounds.Width);
            var scale = Math.Min(1, radius / Math.Sqrt(width * width / 4 + height * height / 4));
            for (var attempt = 0; scale < 0.999 && attempt < 8; attempt++)
            {
                fontSize *= scale * 0.99;
                formatted.SetFontSize(fontSize);
                reset.SetFontSize(9 * fontSize / (text.Length >= 5 ? 8.0 : 10.0));
                percentageBounds = formatted.BuildGeometry(new Point()).Bounds;
                resetBounds = reset.BuildGeometry(new Point()).Bounds;
                height = percentageBounds.Height + gap + resetBounds.Height;
                width = Math.Max(percentageBounds.Width, resetBounds.Width);
                scale = Math.Min(1, radius / Math.Sqrt(width * width / 4 + height * height / 4));
            }
            var top = center.Y - height / 2;
            dc.DrawText(formatted, new Point(center.X - percentageBounds.Width / 2 - percentageBounds.X, top - percentageBounds.Y));
            dc.DrawText(reset, new Point(center.X - resetBounds.Width / 2 - resetBounds.X, top + percentageBounds.Height + gap - resetBounds.Y));
        }
    }

    internal static string LabelFor(QuotaItem item) => item.WeeklyLimitReached ? "0%" : item.Disabled ? "Disabled" : item.Unlimited ? "∞" : item.Remaining is double value ? QuotaItem.Percent(value) : "?";

    private static double Clamp(double value) => Math.Max(0, Math.Min(100, value));

    private static Brush ToBrush(string? hex, Color fallback)
    {
        try { return (Brush)new BrushConverter().ConvertFromString(hex ?? fallback.ToString())!; }
        catch { return new SolidColorBrush(fallback); }
    }

    private static void DrawTrack(DrawingContext dc, Point center, double radius, double thickness, Brush brush) =>
        dc.DrawEllipse(null, new Pen(brush, thickness), center, radius, radius);

    private static void DrawProgress(DrawingContext dc, Point center, double radius, double thickness, double percent, Brush brush)
    {
        if (percent <= 0) return;
        if (percent >= 100)
        {
            dc.DrawEllipse(null, new Pen(brush, thickness), center, radius, radius);
            return;
        }
        var start = new Point(center.X, center.Y - radius);
        var radians = (percent * 3.6 - 90) * Math.PI / 180;
        var end = new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(radius, radius), 0, percent > 50, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(brush, thickness), geometry);
    }
}
