using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace AgentQuotaMonitor;

public sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private Icon? _ownedIcon;
    private bool _disposed;

    public TrayController(Action showMain, Action showSettings, Action toggleFloating, Action quit)
    {
        ArgumentNullException.ThrowIfNull(showMain);
        ArgumentNullException.ThrowIfNull(showSettings);
        ArgumentNullException.ThrowIfNull(toggleFloating);
        ArgumentNullException.ThrowIfNull(quit);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show monitor", null, (_, _) => showMain());
        menu.Items.Add("Settings", null, (_, _) => showSettings());
        menu.Items.Add("Toggle floating monitor", null, (_, _) => toggleFloating());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => quit());

        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Text = "Agent Quota Monitor",
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => showMain();
        SetQuota(null);
    }

    public void SetQuota(QuotaItem? item)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Icon next = CreateIcon(item);
        Icon? previous = _ownedIcon;
        _ownedIcon = next;
        _notifyIcon.Icon = next;
        _notifyIcon.Text = BuildTooltip(item);
        previous?.Dispose();
    }

    private static Icon CreateIcon(QuotaItem? item)
    {
        using var bitmap = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(System.Drawing.Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(7, 7, 50, 50);
            using var track = new Pen(System.Drawing.Color.FromArgb(215, 221, 226), 8) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawEllipse(track, bounds);

            double? remaining = ValidPercent(item?.Remaining);
            System.Drawing.Color ring = RingColor(item, remaining);
            if (remaining is > 0)
            {
                using var quota = new Pen(ring, 8) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                if (remaining >= 100)
                    graphics.DrawEllipse(quota, bounds);
                else
                    graphics.DrawArc(quota, bounds, -90, (float)(remaining.Value * 3.6));
            }

            double? timeRemaining = ValidPercent(item?.TimeRemaining);
            if (timeRemaining is not null)
            {
                var innerBounds = RectangleF.Inflate(bounds, -5, -5);
                using var innerTrack = new Pen(System.Drawing.Color.FromArgb(225, 230, 234), 2);
                graphics.DrawEllipse(innerTrack, innerBounds);
                if (timeRemaining is > 0)
                {
                    using var time = new Pen(System.Drawing.Color.Gray, 2)
                        { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    if (timeRemaining >= 100)
                        graphics.DrawEllipse(time, innerBounds);
                    else
                        graphics.DrawArc(time, innerBounds, -90, (float)(timeRemaining.Value * 3.6));
                }
            }

            string label = item?.Unlimited == true ? "∞" : (item?.Initials ?? "AQ");
            if (label.Length > QuotaNames.MaxInitials)
                label = label[..QuotaNames.MaxInitials];
            using var font = new Font("Segoe UI", label == "∞" ? 19 : label.Length >= 3 ? 10 : 14, FontStyle.Bold, GraphicsUnit.Pixel);
            bool lightTaskbar = false;
            try { lightTaskbar = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int theme && theme == 1; }
            catch (Exception) { }
            using var brush = new SolidBrush(lightTaskbar ? System.Drawing.Color.FromArgb(34, 43, 51) : System.Drawing.Color.WhiteSmoke);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(label, font, brush, bounds, format);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static System.Drawing.Color RingColor(QuotaItem? item, double? remaining)
    {
        if (item?.Stale == true)
            return System.Drawing.Color.Gray;
        if (!string.IsNullOrWhiteSpace(item?.Color))
        {
            try { return ColorTranslator.FromHtml(item.Color); }
            catch (Exception) { }
        }
        return System.Drawing.Color.FromArgb(70, 133, 103);
    }

    private static double? ValidPercent(double? value) =>
        value is >= 0 and <= 100 && double.IsFinite(value.Value) ? value : null;

    internal static string BuildTooltip(QuotaItem? item)
    {
        if (item is null)
            return "Agent Quota Monitor · No quota selected";
        string percent = item.Unlimited ? "Unlimited" : ValidPercent(item.Remaining) is double value ? QuotaItem.Percent(value) : "Unknown";
        string status = item.Stale ? "stale" : item.Status ?? "unknown status";
        string primary = $"{item.Label} {percent} {status}";
        string account = string.IsNullOrWhiteSpace(item.Account) ? string.Empty : " · " + item.Account;
        string text = (primary + account).Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= 127 ? text : text[..124] + "...";
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _ownedIcon?.Dispose();
        _ownedIcon = null;
        GC.SuppressFinalize(this);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
