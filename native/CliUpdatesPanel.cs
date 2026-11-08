using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;

namespace AgentQuotaMonitor;

internal sealed class CliUpdatesPanel : StackPanel
{
    private readonly HttpClient _http;
    private readonly Func<OfficialInstall, bool> _confirm;
    private readonly Action<OfficialInstall> _start;
    private readonly Action<string> _open;
    private readonly Func<string, bool> _enabled;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Muted, FontSize = 12 };
    private readonly Button _check = Ui.Apply(new Button { Content = "Check for updates", HorizontalAlignment = HorizontalAlignment.Left });
    private string _signature = "";
    private bool _working;
    private static readonly Dictionary<string, string> Names = new()
    {
        ["codex"] = "Codex CLI", ["claude"] = "Claude Code", ["antigravity"] = "Antigravity CLI", ["copilot"] = "Copilot CLI"
    };
    internal CliUpdatesPanel(HttpClient http, Func<OfficialInstall, bool> confirm, Action<OfficialInstall> start,
        Action<string> open, Func<string, bool> enabled)
    {
        _http = http; _confirm = confirm; _start = start; _open = open; _enabled = enabled;
        Children.Add(new TextBlock { Text = "Checks enabled providers every 24 hours while AQM is running. Updates run only after you confirm.",
            TextWrapping = TextWrapping.Wrap, Foreground = Ui.Muted, FontSize = 12, Margin = new Thickness(0, 2, 0, 6) });
        Children.Add(_rows);
        Children.Add(_check);
        Children.Add(_message);
        AutomationProperties.SetName(_check, "Check enabled provider CLIs for updates");
        _check.Click += async (_, _) => await Check();
    }
    internal void Render(JsonElement status)
    {
        if (!status.TryGetProperty("cliUpdates", out var updates)) { Visibility = Visibility.Collapsed; return; }
        Visibility = Visibility.Visible;
        bool busy = updates.TryGetProperty("busy", out var b) && b.ValueKind == JsonValueKind.True;
        var tools = updates.GetProperty("tools").EnumerateArray().Where(t => _enabled(Text(t, "provider"))).ToArray();
        _check.IsEnabled = !_working && !busy && tools.Length > 0;
        _check.Content = busy ? "Checking…" : "Check for updates";
        if (updates.TryGetProperty("storageError", out var error) && error.ValueKind == JsonValueKind.True)
            _message.Text = "Update check history could not be saved. Try again after storage is available.";
        string signature = string.Join("|", tools.Select(t => t.GetRawText()));
        if (_signature == signature) return;
        _signature = signature;
        _rows.Children.Clear();
        foreach (var tool in tools)
        {
            string provider = Text(tool, "provider"), state = Text(tool, "state"), installed = Text(tool, "installed");
            if (!Names.TryGetValue(provider, out var name)) continue;
            string summary = state switch {
                "available" => $"{installed} → {Text(tool, "latest")} available",
                "current" => $"{installed} · Up to date",
                "missing" => "Not installed",
                "failed" => "Check unavailable",
                _ => installed.Length > 0 ? $"{installed} · Not checked" : "Not checked"
            };
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 5) };
            if (state == "available")
            {
                string method = Text(tool, "method");
                bool guide = OfficialInstall.CliUpdate(provider, method, Text(tool, "channel")) is null;
                var button = Ui.Apply(new Button { Content = guide ? "Update instructions" : "Update", Tag = "cli-update:" + provider });
                AutomationProperties.SetName(button, "Update " + name);
                button.ToolTip = method == "winget" ? "Ask WinGet to upgrade this package. The vendor release may reach WinGet later." : "Review the official update action before running it.";
                button.Click += async (_, _) => await Update(provider, button);
                DockPanel.SetDock(button, Dock.Right);
                row.Children.Add(button);
            }
            var label = new TextBlock { Text = name + " · " + summary, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            if (tool.TryGetProperty("checkedAt", out var checkedAt) && checkedAt.TryGetDouble(out double timestamp))
                label.ToolTip = "Last checked " + DateTimeOffset.FromUnixTimeSeconds((long)timestamp).ToLocalTime().ToString("g");
            row.Children.Add(label);
            _rows.Children.Add(row);
        }
        if (tools.Length == 0) _rows.Children.Add(new TextBlock { Text = "Enable a provider to check its CLI.", Foreground = Ui.Muted });
    }
    private async Task Check()
    {
        if (_working) return;
        _working = true; _check.IsEnabled = false;
        try
        {
            using var response = await BackendRequests.PostAsync(_http, "/api/cli-updates/check", new { });
            _message.Text = response.IsSuccessStatusCode ? "Checking enabled CLIs. Repeated checks wait at least a minute and honor provider cooldowns." : "Couldn't check CLI updates. Try again later.";
        }
        catch { _message.Text = "Couldn't reach the quota reader."; }
        finally { _working = false; }
    }
    private async Task Update(string provider, Button button)
    {
        if (_working || !_enabled(provider)) return;
        _working = true; button.IsEnabled = false;
        try
        {
            using var response = await BackendRequests.PostAsync(_http, "/api/cli-updates/prepare", new { provider });
            if (!response.IsSuccessStatusCode) { _message.Text = "The client or provider changed. Check for updates again."; return; }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = document.RootElement;
            var install = OfficialInstall.CliUpdate(provider, Text(data, "method"), Text(data, "channel"));
            if (!_enabled(provider)) return;
            if (install is null) { _open(OfficialInstall.CliUpdateHelp(provider)); return; }
            if (!_confirm(install) || !_enabled(provider)) return;
            // Confirmation can stay open while another window changes provider settings.
            using var recheck = await BackendRequests.PostAsync(_http, "/api/cli-updates/prepare", new { provider });
            if (!recheck.IsSuccessStatusCode || !_enabled(provider)) { _message.Text = "The client or provider changed. Check again."; return; }
            using var verified = JsonDocument.Parse(await recheck.Content.ReadAsStringAsync());
            if (Text(verified.RootElement, "method") != Text(data, "method") || Text(verified.RootElement, "channel") != Text(data, "channel"))
            { _message.Text = "The installation method changed. Check again."; return; }
            _start(install);
            _message.Text = "Update opened in PowerShell. After it finishes, check again to verify the installed version.";
        }
        catch { _message.Text = "Couldn't start the update. Check again or use the official instructions."; }
        finally { _working = false; button.IsEnabled = _enabled(provider); }
    }
    private static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
}
