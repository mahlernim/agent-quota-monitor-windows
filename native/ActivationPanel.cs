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

internal sealed class ActivationPanel : StackPanel
{
    private readonly HttpClient http;
    private readonly CheckBox enabled = new() { Content = "Start idle quota windows automatically", IsEnabled = false };
    private readonly CheckBox suggestions = new() { Content = "Suggest activation after three hours of observed inactivity", IsEnabled = false };
    private readonly ComboBox delay = new() { Width = 105, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = new[] { 30, 60, 120 } };
    private readonly StackPanel accounts = new();
    private readonly TextBlock feedback = Text("");
    private readonly Dictionary<string, CheckBox> selections = new();
    private readonly Dictionary<string, TextBlock> statuses = new();
    private readonly Button save;
    private bool dirty, saving;
    private string signature = "";

    internal ActivationPanel(HttpClient http)
    {
        this.http = http;
        Margin = new Thickness(0, 8, 0, 8);
        Children.Add(Text("Off until you enable it. Updates preserve your choices. Enabling this sends a short subscription prompt and uses allowance. Choose each account explicitly."));
        Children.Add(enabled);
        Children.Add(Text("Wait for observed inactivity, in minutes"));
        Children.Add(delay);
        Children.Add(accounts);
        Children.Add(suggestions);
        Children.Add(Text("This release supports direct Claude five-hour windows with verified Claude Code 2.1.280 and extra usage off. Weekly, Codex, and Antigravity activation are unavailable pending runner validation. New accounts stay off."));
        save = Ui.Button("Save activation settings", () => _ = Save());
        save.HorizontalAlignment = HorizontalAlignment.Left;
        save.IsEnabled = false;
        Children.Add(save);
        Children.Add(feedback);
        enabled.Click += (_, _) => Edit();
        suggestions.Click += (_, _) => Edit();
        delay.SelectionChanged += (_, _) => { if (delay.IsKeyboardFocusWithin || delay.IsDropDownOpen) Edit(); };
        AutomationProperties.SetName(delay, "Activation inactivity delay in minutes");
    }

    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Muted, Margin = new Thickness(0, 3, 0, 4) };
    private void Edit() { dirty = true; save.IsEnabled = !saving; }

    internal void Render(JsonElement root)
    {
        if (saving || !root.TryGetProperty("activation", out var state) || !state.TryGetProperty("preferences", out var prefs)) return;
        bool failed = state.TryGetProperty("storageError", out var error) && error.ValueKind == JsonValueKind.True;
        enabled.IsEnabled = suggestions.IsEnabled = delay.IsEnabled = !failed;
        if (failed) { feedback.Text = "Activation storage is unavailable. No new prompts can be sent."; save.IsEnabled = false; return; }
        var rows = state.GetProperty("accounts").EnumerateArray().ToArray();
        string incoming = string.Join("|", rows.Select(a => QuotaItem.Text(a, "accountId") + a.GetProperty("supported").ToString()));
        var selected = prefs.GetProperty("accounts").EnumerateArray().Select(a => a.GetString()).ToHashSet();
        if (signature != incoming && !dirty)
        {
            accounts.Children.Clear(); selections.Clear(); statuses.Clear(); signature = incoming;
            foreach (var row in rows)
            {
                string id = QuotaItem.Text(row, "accountId");
                bool supported = row.GetProperty("supported").ValueKind == JsonValueKind.True;
                var check = new CheckBox { Content = QuotaItem.Text(row, "label") + (supported ? " · Claude five-hour" : " · unavailable"), IsEnabled = supported, IsChecked = selected.Contains(id), Tag = id, Margin = new Thickness(0, 5, 0, 0) };
                check.Click += (_, _) => Edit();
                accounts.Children.Add(check); selections[id] = check;
                var message = Text(QuotaItem.Text(row, "reason"));
                accounts.Children.Add(message); statuses[id] = message;
            }
        }
        foreach (var row in rows)
            if (statuses.TryGetValue(QuotaItem.Text(row, "accountId"), out var status))
                status.Text = QuotaItem.Text(row, "status") + " " + QuotaItem.Text(row, "reason");
        if (!dirty)
        {
            enabled.IsChecked = prefs.GetProperty("enabled").ValueKind == JsonValueKind.True;
            suggestions.IsChecked = prefs.GetProperty("suggestions").ValueKind == JsonValueKind.True;
            delay.SelectedItem = prefs.GetProperty("idleMinutes").GetInt32();
            foreach (var item in selections) item.Value.IsChecked = selected.Contains(item.Key);
        }
    }

    private async Task Save()
    {
        if (saving) return;
        saving = true; save.IsEnabled = false;
        try
        {
            using var response = await BackendRequests.PostAsync(http, "/api/activation", new {
                enabled = enabled.IsChecked == true,
                accounts = selections.Where(s => s.Value.IsChecked == true).Select(s => s.Key).ToArray(),
                idleMinutes = delay.SelectedItem is int minutes ? minutes : 30,
                suggestions = suggestions.IsChecked == true
            });
            response.EnsureSuccessStatusCode();
            dirty = false;
            feedback.Text = "Activation settings saved. An already-dispatched prompt cannot be undone.";
        }
        catch { feedback.Text = "Couldn't save activation settings. Your previous choices remain active."; }
        finally { saving = false; save.IsEnabled = dirty; }
    }
}
