using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace AgentQuotaMonitor;

internal sealed class AppearancePanel : StackPanel
{
    private readonly HttpClient http;
    private readonly Action changed;
    private readonly Func<bool> ready;
    private readonly Func<string, string?> pick;
    private readonly Dictionary<string, Button> buttons = new();
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button restore;
    private bool saving;

    internal AppearancePanel(HttpClient http, Action changed, Func<bool> ready, Func<string, string?>? pick = null)
    {
        this.http = http; this.changed = changed; this.ready = ready; this.pick = pick ?? PickColor;
        Children.Add(new TextBlock { Text = "Main-window ring colors. Floating windows, tray icons, and pace warnings keep their existing colors.",
            TextWrapping = TextWrapping.Wrap, Foreground = Ui.Muted, FontSize = 12, Margin = new Thickness(0, 4, 0, 6) });
        var names = new Dictionary<string, string> { ["codex"] = "OpenAI Codex", ["claude"] = "Anthropic Claude",
            ["antigravity"] = "Google Antigravity", ["copilot"] = "GitHub Copilot" };
        foreach (var pair in names)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var button = Ui.Button("Choose color", () => _ = ChooseAsync(pair.Key));
            AutomationProperties.SetName(button, pair.Value + " ring color");
            button.Tag = "ring-color:" + pair.Key;
            buttons[pair.Key] = button;
            DockPanel.SetDock(button, Dock.Right); row.Children.Add(button);
            row.Children.Add(new TextBlock { Text = pair.Value, VerticalAlignment = VerticalAlignment.Center });
            Children.Add(row);
        }
        Children.Add(new TextBlock { Text = "An Antigravity override applies to all its model groups. Restore defaults brings back their individual colors.",
            TextWrapping = TextWrapping.Wrap, Foreground = Ui.Muted, FontSize = 12, Margin = new Thickness(0, 4, 0, 4) });
        restore = Ui.Button("Restore defaults", () => _ = SaveAsync(new Dictionary<string, string>()));
        restore.Tag = "restore-ring-colors";
        restore.HorizontalAlignment = HorizontalAlignment.Left;
        Children.Add(restore); Children.Add(message);
        UpdateSwatches();
    }

    private string? PickColor(string current)
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.ColorTranslator.FromHtml(current) };
        var owner = Window.GetWindow(this);
        var result = owner is null ? dialog.ShowDialog()
            : dialog.ShowDialog(new DialogOwner(new System.Windows.Interop.WindowInteropHelper(owner).Handle));
        return result == System.Windows.Forms.DialogResult.OK
            ? $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}" : null;
    }
    private sealed record DialogOwner(IntPtr Handle) : System.Windows.Forms.IWin32Window;

    private async Task ChooseAsync(string provider)
    {
        if (saving) return;
        var next = RingAppearance.Copy();
        var selected = pick(next.TryGetValue(provider, out var color) ? color : RingAppearance.Defaults[provider]);
        if (selected is null) return;
        if (!RingAppearance.Valid(selected)) { ShowError(); return; }
        next[provider] = selected;
        await SaveAsync(next);
    }

    private async Task SaveAsync(Dictionary<string, string> next)
    {
        if (saving) return;
        if (!ready()) { ShowError(); return; }
        saving = true;
        foreach (var button in buttons.Values) button.IsEnabled = false;
        restore.IsEnabled = false;
        try
        {
            using var response = await BackendRequests.PostAsync(http, "/api/desktop", new { mainRingColors = next });
            if (!response.IsSuccessStatusCode) { ShowError(); return; }
            RingAppearance.Set(next);
            UpdateSwatches();
            message.Foreground = Ui.Muted; message.Text = "Ring colors saved.";
            changed();
        }
        catch (Exception) { ShowError(); }
        finally
        {
            saving = false;
            foreach (var button in buttons.Values) button.IsEnabled = true;
            restore.IsEnabled = true;
        }
    }

    private void ShowError() { message.Foreground = Brushes.Firebrick; message.Text = "Ring colors couldn't be saved. Check the quota reader connection and try again."; }
    private void UpdateSwatches()
    {
        var saved = RingAppearance.Copy();
        foreach (var pair in buttons)
        {
            var color = saved.TryGetValue(pair.Key, out var custom) ? custom : RingAppearance.Defaults[pair.Key];
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Border { Width = 14, Height = 14, Background = Ui.Hex(color), BorderBrush = Ui.Hairline,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 7, 0) });
            content.Children.Add(new TextBlock { Text = color.ToUpperInvariant() });
            pair.Value.Content = content;
        }
    }
}
