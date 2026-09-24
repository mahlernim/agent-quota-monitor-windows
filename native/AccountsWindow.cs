using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
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

    private readonly UpdateService? _updates;
    private readonly HttpClient _http;
    private readonly Action _changed;
    private readonly Func<bool> _backendReady;
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
    private readonly TextBlock _health = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick };
    private readonly TextBlock _layoutMessage = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 7) };
    private readonly Button _editOrder = new() { Content = "Edit order", Padding = new Thickness(8, 3, 8, 3), IsEnabled = false };
    private readonly Button _saveOrder = new() { Content = "Save order", Padding = new Thickness(8, 3, 8, 3), Visibility = Visibility.Collapsed };
    private readonly Button _cancelOrder = new() { Content = "Cancel", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(6, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _restore = new() { Content = "Restore hidden accounts", Padding = new Thickness(8, 3, 8, 3) };
    private readonly AccountLayoutState _layout = new();
    private readonly HashSet<string> _expandedAccounts = new(StringComparer.Ordinal);
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
    private bool _closed;
    private bool _savingLayout;
    private int _mutationRevision;
    private string? _activeJobId;
    private string _accountsSignature = string.Empty;

    private readonly Button _installCli = new()
    {
        Content = "Install CLI",
        Tag = "antigravity-cli",
        MinWidth = 90,
        Padding = new Thickness(8, 3, 8, 3),
        Margin = new Thickness(0, 0, 6, 0),
        Visibility = Visibility.Collapsed,
        ToolTip = "Install the official Antigravity CLI (agy) so quota can be read without the desktop app."
    };
    private readonly Func<Window, bool> _confirmCliInstall;
    private readonly Action _startCliInstall;

    internal AccountsWindow(Window owner, HttpClient http, Action changed, UpdateService? updates = null, Func<bool>? backendReady = null,
        Func<Window, bool>? confirmCliInstall = null, Action? startCliInstall = null)
    {
        _confirmCliInstall = confirmCliInstall ?? (window => MessageBox.Show(window, AntigravityCliInstall.Confirmation,
            "Install Antigravity CLI", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK);
        _startCliInstall = startCliInstall ?? AntigravityCliInstall.Start;
        _updates = updates;
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        _backendReady = backendReady ?? (() => true);
        Title = "Settings and accounts";
        Icon = owner.Icon;
        Width = 660;
        MinWidth = 520;
        Height = 640;
        MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = BuildContent();

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
            async (_, _) => await PollAsync(), Dispatcher);
        Loaded += async (_, _) =>
        {
            await PollAsync();
            if (!_closed) _timer.Start();
        };
        Closed += (_, _) => { _closed = true; _timer.Stop(); };
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
        footer.Children.Add(_health);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = new StackPanel();
        var startup = new CheckBox {
            Content = "Start with Windows (in the tray)", Margin = new Thickness(0, 0, 0, 6),
            ToolTip = "Starts when you sign in. Keep the portable app in a permanent folder. Turn this off before moving or deleting it."
        };
        try { startup.IsChecked = StartupRegistration.Enabled; }
        catch { startup.IsEnabled = false; _message.Text = "Windows startup settings are unavailable."; }
        startup.Click += (_, _) => {
            try { StartupRegistration.SetEnabled(startup.IsChecked == true); _message.Text = ""; }
            catch { startup.IsChecked = false; _message.Text = "Could not change Windows startup. Check your Windows account permissions."; }
        };
        body.Children.Add(startup);
        if (_updates is not null) {
            var auto = new CheckBox { Content = "Check for updates automatically", IsChecked = _updates.Preferences.Automatic, Margin = new Thickness(0, 3, 0, 3) };
            auto.Click += (_, _) => _updates.SetAutomatic(auto.IsChecked == true);
            body.Children.Add(auto);
            var check = new Button { Content = "Check for updates", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(7, 3, 7, 3) };
            check.Click += async (_, _) => await _updates.Check(true);
            body.Children.Add(check);
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 5) };
            body.Children.Add(status);
            void UpdateStatus() { check.IsEnabled = !_updates.Busy; status.Text = $"Installed {UpdateService.InstalledVersion} · " + _updates.Status; }
            _updates.Changed += UpdateStatus;
            Closed += (_, _) => _updates.Changed -= UpdateStatus;
            UpdateStatus();
        }
        body.Children.Add(new Separator { Margin = new Thickness(0, 2, 0, 8) });
        body.Children.Add(new TextBlock
        {
            Text = "Monitored providers",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        body.Children.Add(new TextBlock
        {
            Text = "Provider sign-in uses the official coding client. The monitor follows verified client identities and never switches accounts automatically.",
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
            {
                var signIn = new Button
                {
                    Content = provider == "antigravity" ? "Open desktop app" : "Sign in",
                    Tag = provider,
                    MinWidth = 90,
                    Padding = new Thickness(8, 3, 8, 3),
                    IsEnabled = false
                };
                signIn.Click += async (_, _) => await StartConnectionAsync(provider);
                DockPanel.SetDock(signIn, Dock.Right);
                row.Children.Add(signIn);
                _signInButtons[provider] = signIn;
                if (provider == "antigravity")
                {
                    AutomationProperties.SetName(_installCli, "Install the official Antigravity CLI");
                    _installCli.Click += (_, _) => InstallCli();
                    DockPanel.SetDock(_installCli, Dock.Right);
                    row.Children.Add(_installCli);
                }
            }
            body.Children.Add(row);
        }

        body.Children.Add(new TextBlock {
            Text = "Antigravity Open desktop app does not start CLI sign-in. The official Antigravity CLI (agy) lets the monitor read quota while the desktop app is closed. " +
                "Install CLI appears when agy is missing and asks before running Google's installer in a visible window. " +
                "For an existing CLI account, run agy interactively and sign in if prompted, then run agy -p /usage and press Refresh in the monitor.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 5, 0, 0)
        });
        body.Children.Add(new TextBlock {
            Text = "Copilot Sign in opens the official GitHub CLI and browser. Its optional runtime must first be installed with Setup-Copilot.ps1 from the source repository.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 5, 0, 0)
        });
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
        _restore.Click += async (_, _) => await RestoreAsync();
        DockPanel.SetDock(_restore, Dock.Right);
        accountHeader.Children.Add(_restore);
        body.Children.Add(accountHeader);
        body.Children.Add(new TextBlock
        {
            Text = "Edit order changes the account order in the main monitor. Move accounts, then Save order or Cancel. Remove hides an account without signing out or deleting its official client account.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 4, 0, 7)
        });
        var orderActions = new StackPanel { Orientation = Orientation.Horizontal };
        _editOrder.Click += (_, _) => { _layout.Begin(); RenderAccounts(true); };
        _saveOrder.Click += async (_, _) => await SaveOrderAsync();
        _cancelOrder.Click += (_, _) => { _layout.Cancel(); ShowSuccess("Order changes discarded."); RenderAccounts(true); };
        orderActions.Children.Add(_editOrder);
        orderActions.Children.Add(_saveOrder);
        orderActions.Children.Add(_cancelOrder);
        body.Children.Add(orderActions);
        body.Children.Add(_layoutMessage);
        body.Children.Add(_accounts);

        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(scroll);
        return root;
    }

    private async Task PollAsync()
    {
        if (_closed || _polling || _savingLayout || !IsVisible)
            return;
        if (!_backendReady())
        {
            _health.Text = "Quota reader unavailable. Startup and update settings are still available.";
            return;
        }
        _polling = true;
        int revision = _mutationRevision;
        try
        {
            using HttpResponseMessage response = await _http.GetAsync("/api/status");
            if (!response.IsSuccessStatusCode)
            {
                _health.Text = "Could not read account status. Displayed information may be stale.";
                return;
            }
            using JsonDocument document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            if (_closed || !_backendReady() || revision != _mutationRevision) return;
            RenderStatus(document.RootElement);
            _health.Text = document.RootElement.TryGetProperty("storageError", out JsonElement storageError) && storageError.ValueKind == JsonValueKind.True
                ? "Encrypted cache unavailable. Changes may not be saved." : string.Empty;
        }
        catch (Exception)
        {
            _health.Text = "Could not read account status. Displayed information may be stale.";
        }
        finally
        {
            _polling = false;
        }
    }

    private void RenderStatus(JsonElement status)
    {
        AccountStatus[] accountRows = AccountStatus.Parse(status);
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
            // Offer the install only when the reader positively reports that agy is missing.
            _installCli.Visibility = clients.TryGetProperty("antigravityCli", out JsonElement cli) && cli.ValueKind == JsonValueKind.False
                ? Visibility.Visible : Visibility.Collapsed;
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

        _layout.Observe(accountRows);
        RenderAccounts();
    }

    private void RenderAccounts(bool force = false)
    {
        bool editing = _layout.Editing;
        bool conflict = _layout.HasConflict;
        _editOrder.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        _editOrder.IsEnabled = _layout.Loaded && _layout.Displayed.Count > 1;
        _saveOrder.Visibility = _cancelOrder.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        _saveOrder.IsEnabled = !_savingLayout && !conflict;
        _cancelOrder.IsEnabled = !_savingLayout;
        _restore.IsEnabled = !editing;
        _saveProviders.IsEnabled = _providersLoaded && !editing;
        _layoutMessage.Text = conflict ? "The account list changed. Cancel editing and try again. Your draft order has been kept."
            : editing ? "Order changes are not saved yet." : "";
        _layoutMessage.Foreground = conflict ? Brushes.Firebrick : Brushes.DimGray;
        var rows = _layout.Displayed;
        string signature = JsonSerializer.Serialize(new { rows, editing, conflict, _savingLayout });
        if (!force && signature == _accountsSignature)
            return;
        _accountsSignature = signature;
        object? focusedTag = (Keyboard.FocusedElement as FrameworkElement)?.Tag;
        _accounts.Children.Clear();
        foreach ((AccountStatus account, int index) in rows.Select((account, index) => (account, index)))
        {
            string label = $"{ProviderNames.GetValueOrDefault(account.Provider, account.Provider)} · {account.Label}";
            var card = new StackPanel { Margin = new Thickness(0, 4, 0, 7) };
            var row = new DockPanel();
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            if (editing)
            {
                foreach ((string caption, int direction) in new[] { ("Move up", -1), ("Move down", 1) })
                {
                    var move = new Button { Content = caption, Tag = (account.Id, direction), Padding = new Thickness(6, 2, 6, 2),
                        IsEnabled = !_savingLayout && !conflict && (direction < 0 ? index > 0 : index < rows.Count - 1) };
                    AutomationProperties.SetName(move, caption + " " + label);
                    move.Click += (_, _) =>
                    {
                        if (_layout.Move(account.Id, direction))
                        {
                            RenderAccounts(true);
                            FocusMove(account.Id, direction);
                        }
                    };
                    controls.Children.Add(move);
                }
            }
            else
            {
                var remove = new Button { Content = "Remove", Tag = (account.Id, 0), Padding = new Thickness(7, 2, 7, 2) };
                AutomationProperties.SetName(remove, "Remove " + label);
                remove.Click += async (_, _) => await RemoveAsync(account.Id);
                controls.Children.Add(remove);
            }
            DockPanel.SetDock(controls, Dock.Right);
            row.Children.Add(controls);
            row.Children.Add(new TextBlock
            {
                Text = $"{label} · {account.Status}",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            card.Children.Add(row);
            if (!string.IsNullOrEmpty(account.Guidance))
                card.Children.Add(new TextBlock { Text = account.Guidance, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick, Margin = new Thickness(0, 3, 0, 0) });
            string retry = account.RetryState == "suspended" ? "Paused pending a usable provider retry time" : AccountStatus.Timestamp(account.NextAttempt, "Not reported");
            string details = $"Source · {EmptyFallback(account.Source)}\nIdentity · {EmptyFallback(account.Identity)}\nAccount ID · {account.Id}\nLast successful read · {AccountStatus.Timestamp(account.LastSuccess, "Never")}\nNext eligible read · {retry}";
            if (account.SessionExpiresAt.HasValue)
                details += $"\nOfficial session expires · {AccountStatus.Timestamp(account.SessionExpiresAt, "Not reported")} (renewed by the official client)";
            if (!string.IsNullOrEmpty(account.QuotaDetails)) details += "\n" + account.QuotaDetails;
            var detail = new Expander { Header = "Details", Tag = account.Id, IsExpanded = _expandedAccounts.Contains(account.Id),
                Content = new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(14, 3, 0, 3) } };
            AutomationProperties.SetName(detail, "Details for " + label);
            detail.Expanded += (_, _) => _expandedAccounts.Add(account.Id);
            detail.Collapsed += (_, _) => _expandedAccounts.Remove(account.Id);
            card.Children.Add(detail);
            _accounts.Children.Add(card);
        }
        if (rows.Count == 0)
            _accounts.Children.Add(new TextBlock { Text = "No accounts displayed. Restore hidden accounts or enable a provider.", TextWrapping = TextWrapping.Wrap });
        if (focusedTag is ValueTuple<string, int> key) FocusMove(key.Item1, key.Item2);
    }

    private void FocusMove(string id, int direction)
    {
        var candidates = _accounts.Children.OfType<StackPanel>().SelectMany(card => card.Children.OfType<DockPanel>())
            .SelectMany(row => row.Children.OfType<StackPanel>()).SelectMany(row => row.Children.OfType<Button>())
            .Where(button => button.IsEnabled && button.Tag is ValueTuple<string, int> key && key.Item1 == id).ToArray();
        (candidates.FirstOrDefault(button => button.Tag is ValueTuple<string, int> key && key.Item2 == direction) ?? candidates.FirstOrDefault())?.Focus();
    }

    private static string EmptyFallback(string value) => string.IsNullOrWhiteSpace(value) ? "Not reported" : value;

    private async Task SaveOrderAsync()
    {
        if (_closed) return;
        if (!_backendReady()) { ShowError("Quota reader unavailable. The account order has not been saved."); return; }
        if (!_layout.Editing || _layout.HasConflict || _savingLayout) return;
        _savingLayout = true;
        ++_mutationRevision;
        RenderAccounts(true);
        try
        {
            using HttpResponseMessage response = await BackendRequests.PostAsync(_http, "/api/accounts/layout", new { order = _layout.Order, removed = Array.Empty<string>() });
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Conflict) _layout.Reject();
                ShowError(await BackendErrorAsync(response));
                return;
            }
            _layout.Commit();
            ShowSuccess("Account order saved.");
            _changed();
        }
        catch (Exception) { ShowError("The account order could not be saved. Your draft order has been kept."); }
        finally
        {
            ++_mutationRevision;
            _savingLayout = false;
            RenderAccounts(true);
            await PollAsync();
        }
    }

    private async Task SaveProvidersAsync()
    {
        string[] enabled = _providerChecks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToArray();
        await PostAsync("/api/providers", new { enabled }, "Monitored providers saved.");
    }

    private async Task StartConnectionAsync(string provider) =>
        await PostAsync("/api/connections/start", new { provider },
            provider == "antigravity" ? "Opening Antigravity desktop app. CLI accounts sign in through agy in a terminal." : "Official sign-in started.");

    private void InstallCli()
    {
        if (_closed || !_confirmCliInstall(this)) return;
        try
        {
            _startCliInstall();
            ShowSuccess("Antigravity CLI installer opened in PowerShell. Follow that window, then return here.");
        }
        catch (Exception) { ShowError("PowerShell could not be started. Install the CLI from antigravity.google/docs/cli/install."); }
    }

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
        if (_closed) return;
        if (!_backendReady()) { ShowError("Quota reader unavailable. The request was not sent."); return; }
        try
        {
            using HttpResponseMessage response = await BackendRequests.PostAsync(_http, path, payload);
            if (!response.IsSuccessStatusCode)
            {
                ShowError(await BackendErrorAsync(response));
                return;
            }
            ShowSuccess(success);
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

    private void ShowSuccess(string message)
    {
        _message.Foreground = Brushes.DarkGreen;
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
