using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentQuotaMonitor;

public sealed class AccountsWindow : Window
{
    private static readonly string[] Providers = ["codex", "claude", "antigravity", "copilot"];
    private static readonly IReadOnlyDictionary<string, string> ProviderNames = new Dictionary<string, string>
    {
        ["codex"] = "OpenAI Codex",
        ["claude"] = "Anthropic Claude",
        ["antigravity"] = "Google Antigravity",
        ["copilot"] = "GitHub Copilot"
    };

    private readonly HttpClient _http;
    private readonly Action _changed;
    private readonly Dictionary<string, CheckBox> _providerChecks = [];
    private readonly Dictionary<string, Button> _signInButtons = [];
    private readonly StackPanel _accounts = new();
    private readonly TextBlock _job = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _cancelConnection = new()
    {
        Content = "Cancel sign-in",
        Padding = new Thickness(8, 3, 8, 3),
        Margin = new Thickness(0, 5, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Left,
        Visibility = Visibility.Collapsed
    };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _saveProviders = new()
    {
        Content = "Save monitored providers",
        HorizontalAlignment = HorizontalAlignment.Left,
        Padding = new Thickness(9, 4, 9, 4),
        Margin = new Thickness(0, 7, 0, 13),
        IsEnabled = false
    };
    private readonly DispatcherTimer _timer;
    private bool _providersLoaded;
    private bool _polling;
    private string? _activeJobId;
    private string _accountsSignature = string.Empty;

    public AccountsWindow(Window owner, HttpClient http, Action changed)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        Title = "Accounts and providers";
        Icon = owner.Icon;
        Width = 560;
        MinWidth = 460;
        Height = 520;
        MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = BuildContent();

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
            async (_, _) => await PollAsync(), Dispatcher);
        Loaded += async (_, _) =>
        {
            await PollAsync();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private UIElement BuildContent()
    {
        var root = new DockPanel { Margin = new Thickness(14) };
        var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(_job);
        _cancelConnection.Click += async (_, _) => await CancelConnectionAsync();
        footer.Children.Add(_cancelConnection);
        _message.Foreground = Brushes.Firebrick;
        _message.Margin = new Thickness(0, 5, 0, 0);
        footer.Children.Add(_message);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = "Monitored providers",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        body.Children.Add(new TextBlock
        {
            Text = "Provider sign-in uses the official coding client. Antigravity opens its official client for account changes. The monitor follows the verified client identity and never switches accounts automatically.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 4, 0, 8)
        });

        foreach (string provider in Providers)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), LastChildFill = false };
            var check = new CheckBox
            {
                Content = ProviderNames[provider],
                VerticalAlignment = VerticalAlignment.Center,
                Tag = provider
            };
            _providerChecks[provider] = check;
            row.Children.Add(check);
            if (provider != "copilot")
            {
                var signIn = new Button
                {
                    Content = provider == "antigravity" ? "Open official client" : "Sign in",
                    Tag = provider,
                    MinWidth = 90,
                    Padding = new Thickness(8, 3, 8, 3),
                    IsEnabled = false
                };
                signIn.Click += async (_, _) => await StartConnectionAsync(provider);
                DockPanel.SetDock(signIn, Dock.Right);
                row.Children.Add(signIn);
                _signInButtons[provider] = signIn;
            }
            body.Children.Add(row);
        }

        _saveProviders.Click += async (_, _) => await SaveProvidersAsync();
        body.Children.Add(_saveProviders);

        var accountHeader = new DockPanel { LastChildFill = false };
        accountHeader.Children.Add(new TextBlock
        {
            Text = "Displayed accounts",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var restore = new Button { Content = "Restore hidden accounts", Padding = new Thickness(8, 3, 8, 3) };
        restore.Click += async (_, _) => await RestoreAsync();
        DockPanel.SetDock(restore, Dock.Right);
        accountHeader.Children.Add(restore);
        body.Children.Add(accountHeader);
        body.Children.Add(new TextBlock
        {
            Text = "Remove hides an account from this monitor. It does not sign out or delete the official client account.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 4, 0, 7)
        });
        body.Children.Add(_accounts);

        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(scroll);
        return root;
    }

    private async Task PollAsync()
    {
        if (_polling || !IsVisible)
            return;
        _polling = true;
        try
        {
            using HttpResponseMessage response = await _http.GetAsync("/api/status");
            if (!response.IsSuccessStatusCode)
            {
                ShowError("Could not read account status.");
                return;
            }
            using JsonDocument document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            RenderStatus(document.RootElement);
            _message.Text = string.Empty;
        }
        catch (Exception)
        {
            ShowError("Could not read account status.");
        }
        finally
        {
            _polling = false;
        }
    }

    private void RenderStatus(JsonElement status)
    {
        if (!_providersLoaded && status.TryGetProperty("enabledProviders", out JsonElement enabled))
        {
            HashSet<string?> selected = enabled.ValueKind == JsonValueKind.Array
                ? enabled.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()).ToHashSet()
                : new HashSet<string?>();
            foreach ((string provider, CheckBox check) in _providerChecks)
                check.IsChecked = selected.Contains(provider);
            _providersLoaded = true;
            _saveProviders.IsEnabled = true;
        }

        JsonElement connections = default;
        bool hasConnections = status.TryGetProperty("connections", out connections);
        if (hasConnections && connections.TryGetProperty("clients", out JsonElement clients))
        {
            foreach ((string provider, Button button) in _signInButtons)
                button.IsEnabled = clients.TryGetProperty(provider, out JsonElement available) && available.ValueKind == JsonValueKind.True;
        }

        _job.Text = "No connection in progress.";
        _activeJobId = null;
        _cancelConnection.Visibility = Visibility.Collapsed;
        if (hasConnections && connections.TryGetProperty("job", out JsonElement job) && job.ValueKind == JsonValueKind.Object)
        {
            string provider = Text(job, "provider");
            string state = Text(job, "state").Replace('_', ' ');
            string message = Text(job, "message");
            _job.Text = $"{ProviderNames.GetValueOrDefault(provider, provider)} · {state}\n{message}".Trim();
            if (state is "starting" or "waiting" or "verifying")
            {
                _activeJobId = Text(job, "id");
                _cancelConnection.Visibility = string.IsNullOrEmpty(_activeJobId) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        if (!status.TryGetProperty("accounts", out JsonElement accounts) || accounts.ValueKind != JsonValueKind.Array)
            return;
        var accountRows = accounts.EnumerateArray().Select(account => new
        {
            Id = Text(account, "id"),
            Provider = Text(account, "provider"),
            Label = Text(account, "label"),
            Status = Text(account, "status")
        }).ToArray();
        string signature = string.Join("\u001f", accountRows.Select(row =>
            string.Join("\u001e", row.Id, row.Provider, row.Label, row.Status)));
        if (signature == _accountsSignature)
            return;
        _accountsSignature = signature;
        _accounts.Children.Clear();
        foreach (var account in accountRows)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var remove = new Button { Content = "Remove", Tag = account.Id, Padding = new Thickness(7, 2, 7, 2) };
            remove.Click += async (_, _) => await RemoveAsync(account.Id);
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(new TextBlock
            {
                Text = $"{ProviderNames.GetValueOrDefault(account.Provider, account.Provider)} · {account.Label} · {account.Status}",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            });
            _accounts.Children.Add(row);
        }
    }

    private async Task SaveProvidersAsync()
    {
        string[] enabled = _providerChecks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToArray();
        await PostAsync("/api/providers", new { enabled }, "Monitored providers saved.");
    }

    private async Task StartConnectionAsync(string provider) =>
        await PostAsync("/api/connections/start", new { provider },
            provider == "antigravity" ? "Official Antigravity client opened." : "Official sign-in started.");

    private async Task RemoveAsync(string accountId) =>
        await PostAsync("/api/accounts/remove", new { accountId }, "Account hidden from the monitor.");

    private async Task RestoreAsync() =>
        await PostAsync("/api/accounts/restore", new { }, "Hidden accounts restored.");

    private async Task CancelConnectionAsync()
    {
        string? jobId = _activeJobId;
        if (string.IsNullOrEmpty(jobId))
            return;
        await PostAsync("/api/connections/cancel", new { jobId }, "Sign-in wait cancelled. The official client remains open.");
    }

    private async Task PostAsync(string path, object payload, string success)
    {
        try
        {
            using HttpResponseMessage response = await _http.PostAsJsonAsync(path, payload);
            if (!response.IsSuccessStatusCode)
            {
                ShowError(await BackendErrorAsync(response));
                return;
            }
            _message.Foreground = Brushes.DarkGreen;
            _message.Text = success;
            _changed();
            await PollAsync();
        }
        catch (Exception)
        {
            ShowError("The request could not be completed.");
        }
    }

    private void ShowError(string message)
    {
        _message.Foreground = Brushes.Firebrick;
        _message.Text = message;
    }

    private static async Task<string> BackendErrorAsync(HttpResponseMessage response)
    {
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            if (document.RootElement.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String)
            {
                string message = error.GetString() ?? "The request could not be completed.";
                return message.Length <= 300 ? message : message[..300];
            }
        }
        catch (Exception) { }
        return "The request could not be completed.";
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
