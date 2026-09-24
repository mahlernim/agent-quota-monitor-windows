using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AgentQuotaMonitor;

/// <summary>
/// One line of centered text that narrows horizontally, down to MinScale, before falling back to
/// shorter text or an ellipsis. It never draws outside its bounds.
/// </summary>
public sealed class FitText : FrameworkElement
{
    internal const double MinScale = 0.8;
    public string Text { get; set; } = "";
    /// <summary>Used instead of an ellipsis when Text cannot fit even when narrowed.</summary>
    public string? Fallback { get; set; }
    public double FontSize { get; set; } = 11;
    public FontWeight FontWeight { get; set; } = FontWeights.SemiBold;
    public Brush Foreground { get; set; } = Ui.Text;

    internal void Set(string text, string? fallback = null)
    {
        if (text == Text && fallback == Fallback) return;
        Text = text; Fallback = fallback;
        InvalidateMeasure(); InvalidateVisual();
    }

    /// <summary>The text actually drawn and its horizontal scale, for tests.</summary>
    internal (string Text, double Scale) Shown { get; private set; }

    private FormattedText Format(string text) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeight, FontStretches.Normal), FontSize, Foreground,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override Size MeasureOverride(Size available)
    {
        var formatted = Format(Text.Length > 0 ? Text : " ");
        double width = double.IsInfinity(available.Width) ? formatted.Width : Math.Min(formatted.Width, available.Width);
        return new Size(width, formatted.Height);
    }

    internal (string Text, double Scale, bool Trim) Choose(double width)
    {
        foreach (string? candidate in new[] { Text, Fallback })
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            double natural = Format(candidate).Width;
            if (natural <= width) return (candidate, 1, false);
            if (natural * MinScale <= width) return (candidate, width / natural, false);
        }
        return (Text, MinScale, true);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        if (width <= 0) return;
        (string text, double scale, bool trim) = Choose(width);
        var formatted = Format(text);
        if (trim)
        {
            formatted.MaxTextWidth = width / scale;
            formatted.MaxLineCount = 1;
            formatted.Trimming = TextTrimming.CharacterEllipsis;
        }
        Shown = (text, scale);
        double drawn = Math.Min(formatted.WidthIncludingTrailingWhitespace, width / scale) * scale;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, ActualHeight)));
        dc.PushTransform(new TranslateTransform((width - drawn) / 2, 0));
        dc.PushTransform(new ScaleTransform(scale, 1));
        dc.DrawText(formatted, new Point(0, 0));
        dc.Pop(); dc.Pop(); dc.Pop();
    }
}
