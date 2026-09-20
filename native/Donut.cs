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
        var tint = ToBrush(item.Color, Color.FromRgb(86, 100, 119));
        if (!item.Stale && item.Remaining is double remaining)
            DrawProgress(dc, center, outerRadius, 6, Clamp(remaining), tint);
        if (!item.Stale && item.TimeRemaining is double time)
        {
            DrawTrack(dc, center, innerRadius, 2, track);
            DrawProgress(dc, center, innerRadius, 2, Clamp(time), Brushes.Gray);
        }
        var text = item.Unlimited ? "∞" : item.Remaining is double value ? value.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "?";
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var fontSize = text.Length >= 5 ? 8.0 : 10.0;
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface,
            fontSize, ToBrush(item.NumberColor, Colors.Black), dip);
        while (formatted.Width > innerRadius * 2 - 3 && fontSize > 6)
        {
            fontSize -= 1;
            formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface,
                fontSize, ToBrush(item.NumberColor, Colors.Black), dip);
        }
        dc.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
    }

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
