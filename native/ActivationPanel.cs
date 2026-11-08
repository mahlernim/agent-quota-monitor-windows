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
    private readonly CheckBox enabled = new() { Content = "Automatically start idle quota windows", IsEnabled = false };
    private readonly StackPanel details = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel accounts = new();
    private readonly TextBlock feedback = Text("");
    private readonly Dictionary<string, CheckBox> selections = new();
    private readonly Dictionary<string, TextBlock> statuses = new();
    private readonly Dictionary<string, bool> supported = new();
    private HashSet<string> confirmedSelections = new();
    private bool confirmedEnabled, saving, failed, awaitingConfirmation;
    private string? signature;

    internal ActivationPanel(HttpClient http)
    {
        this.http = http;
        Margin = new Thickness(0, 8, 0, 8);
        Children.Add(enabled);
        Children.Add(Text("After 30 minutes of observed inactivity, send a short prompt to start the selected windows. Uses subscription allowance."));
        details.Children.Add(Text("Choose windows below. Changes save immediately."));
        details.Children.Add(accounts);
        Children.Add(details);
        feedback.Visibility = Visibility.Collapsed;
        Children.Add(feedback);
        enabled.Click += async (_, _) => await Save(false);
    }

    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Muted,
        FontSize = 12, Margin = new Thickness(0, 3, 0, 4) };

    private void UpdateControls()
    {
        enabled.IsEnabled = !saving && !failed;
        details.Visibility = enabled.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        foreach (var pair in selections)
        {
            pair.Value.IsEnabled = !saving && !failed && supported[pair.Key];
            statuses[pair.Key].Visibility = pair.Value.IsChecked == true || !supported[pair.Key] ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    internal void Render(JsonElement root)
    {
        if (saving || !root.TryGetProperty("activation", out var state) || !state.TryGetProperty("preferences", out var prefs)) return;
        failed = state.TryGetProperty("storageError", out var error) && error.ValueKind == JsonValueKind.True;
        if (failed)
        {
            feedback.Text = "Activation settings are unavailable. No new prompts can be sent.";
            feedback.Visibility = Visibility.Visible;
            UpdateControls(); return;
        }
        var incomingEnabled = prefs.GetProperty("enabled").ValueKind == JsonValueKind.True;
        var selected = prefs.GetProperty("accounts").EnumerateArray().Select(a => a.GetString()!).ToHashSet();
        // A status request started before a save must not undo its confirmed result.
        if (!awaitingConfirmation || (incomingEnabled == confirmedEnabled && selected.SetEquals(confirmedSelections)))
        {
            confirmedEnabled = incomingEnabled; confirmedSelections = selected; awaitingConfirmation = false;
        }
        enabled.IsChecked = confirmedEnabled;
        var rows = state.GetProperty("accounts").EnumerateArray().ToArray();
        string incoming = string.Join("|", rows.Select(a => string.Join("/", QuotaItem.Text(a, "accountId"),
            QuotaItem.Text(a, "stableAccountId"), QuotaItem.Text(a, "provider"), QuotaItem.Text(a, "label"),
            QuotaItem.Text(a, "displayLabel"), a.GetProperty("supported").ToString())));
        if (signature != incoming)
        {
            accounts.Children.Clear(); selections.Clear(); statuses.Clear(); supported.Clear(); signature = incoming;
            // Group by stable source identity, never by the displayed email.
            foreach (var group in rows.GroupBy(row => (QuotaItem.Text(row, "provider"),
                QuotaItem.Text(row, "stableAccountId", QuotaItem.Text(row, "accountId")))))
            {
                accounts.Children.Add(new TextBlock { Text = QuotaItem.Text(group.First(), "label"), Foreground = Ui.Text,
                    FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 3) });
                foreach (var row in group)
                {
                    string id = QuotaItem.Text(row, "accountId");
                    supported[id] = row.GetProperty("supported").ValueKind == JsonValueKind.True;
                    string window = QuotaItem.Text(row, "displayLabel");
                    if (string.IsNullOrEmpty(window)) window = QuotaItem.Text(row, "provider") + " · " + QuotaItem.Text(row, "window");
                    var check = new CheckBox { Content = window, Tag = id, Margin = new Thickness(0, 3, 0, 0) };
                    AutomationProperties.SetName(check, QuotaItem.Text(row, "label") + " · " + window);
                    check.Click += async (_, _) => await Save(true);
                    accounts.Children.Add(check); selections[id] = check;
                    var status = Text(""); status.Margin = new Thickness(18, 1, 0, 4);
                    accounts.Children.Add(status); statuses[id] = status;
                }
            }
            if (rows.Length == 0) accounts.Children.Add(Text("No supported account windows are available yet."));
        }
        foreach (var row in rows)
        {
            string id = QuotaItem.Text(row, "accountId");
            var check = selections[id];
            check.IsChecked = confirmedSelections.Contains(id);
            string explanation = QuotaItem.Text(row, "status") + " " + QuotaItem.Text(row, "reason");
            check.ToolTip = explanation.Trim();
            AutomationProperties.SetHelpText(check, explanation.Trim());
            statuses[id].Text = supported[id] ? QuotaItem.Text(row, "status") : "Unavailable";
            statuses[id].ToolTip = explanation.Trim();
            statuses[id].Visibility = check.IsChecked == true || !supported[id] ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateControls();
    }

    private async Task Save(bool selectionChange)
    {
        if (saving || failed) return;
        saving = true; UpdateControls();
        feedback.Text = "Saving…"; feedback.Visibility = Visibility.Visible;
        bool nextEnabled = enabled.IsChecked == true;
        var nextSelections = selectionChange ? selections.Where(s => s.Value.IsChecked == true).Select(s => s.Key).ToHashSet() : confirmedSelections;
        var payload = new Dictionary<string, object> { ["enabled"] = nextEnabled, ["idleMinutes"] = 30 };
        // Turning the feature off retains selections, including temporarily undiscovered accounts.
        if (selectionChange) payload["accounts"] = nextSelections.ToArray();
        try
        {
            using var response = await BackendRequests.PostAsync(http, "/api/activation", payload);
            response.EnsureSuccessStatusCode();
            confirmedEnabled = nextEnabled; confirmedSelections = new(nextSelections); awaitingConfirmation = true;
            feedback.Visibility = Visibility.Collapsed;
        }
        catch
        {
            enabled.IsChecked = confirmedEnabled;
            foreach (var pair in selections) pair.Value.IsChecked = confirmedSelections.Contains(pair.Key);
            feedback.Text = "Couldn't save activation settings. Your previous choices remain active.";
        }
        finally { saving = false; UpdateControls(); }
    }
}
