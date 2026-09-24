using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgentQuotaMonitor;

// Documentation images use synthetic data only and never start a provider reader.
internal static class PreviewImages
{
    internal static void Create(string directory)
    {
        Directory.CreateDirectory(directory);
        var rows = new List<QuotaItem>();
        void Add(string provider, string code, string group, string window, double quota, double time) {
            rows.Add(new QuotaItem { Key = code + window, AccountId = provider, GroupId = group,
                Provider = provider, Account = "sample@example.test", Code = code, Group = group,
                Window = window, Remaining = quota, TimeRemaining = time, Status = "live",
                Tooltip = "Sample data for documentation" });
        }
        Add("codex", "CX", "Codex", "5h", 72, 62); Add("codex", "CX", "Codex", "7d", 61, 75);
        Add("claude", "CL", "Direct Claude", "5h", 84, 55); Add("claude", "CL", "Direct Claude", "7d", 35, 80);
        Add("antigravity", "GM", "Gemini Models", "5h", 93.4, 65); Add("antigravity", "GM", "Gemini Models", "7d", 67, 80);
        Add("antigravity", "CG", "Claude and GPT models", "5h", 91, 70); Add("antigravity", "CG", "Claude and GPT models", "7d", 17, 80);
        Add("copilot", "CP", "Included AI credits", "Month", 88, 0);
        var pins = new HashSet<string> { "CX5h", "CL7d", "GM5h", "CG7d" };
        var main = new MainWindow(_ => {}, _ => {}, () => {}, () => {}, () => {}, () => {});
        var accounts = new List<AccountStatus>
        {
            Sample("codex", "live", ""), Sample("claude", "live", ""),
            // A recent desktop reading stays live while the banner explains the closed source.
            Sample("antigravity", "live", "antigravity_cli_unavailable", "Official running Antigravity local service"), Sample("copilot", "live", "")
        };
        main.SetData(rows, "CX5h", pins, accounts);
        Save((FrameworkElement)main.Content, 820, 430, Path.Combine(directory, "main-window.png"));
        var floating = new FloatingWindow(() => {}, () => {});
        floating.SetData(rows.FindAll(q => pins.Contains(q.Key)));
        Save((FrameworkElement)floating.Content, 242, 68, Path.Combine(directory, "floating-monitor.png"));
    }
    private static AccountStatus Sample(string provider, string status, string error, string source = "Synthetic preview data") =>
        new(provider, provider, "sample@example.test", status, source, "Verified stable identity", error, 1790000000, 1790000300);

    private static void Save(FrameworkElement element, double width, double height, string path)
    {
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width * 2, (int)height * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
