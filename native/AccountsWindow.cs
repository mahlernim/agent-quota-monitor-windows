using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AgentQuotaMonitor;

public sealed class AccountsWindow : Window
{
    private const string RowFormat = "AgentQuotaMonitor.SettingsAccount";
    private const string Readme = "https://github.com/mahlernim/agent-quota-monitor-windows#";
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
    private readonly Dictionary<string, TextBox> _initialsFields = [];
    private readonly Dictionary<string, TextBox> _nameFields = [];
    private readonly StackPanel _accounts = new();
    private readonly TextBlock _job = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Button _cancelConnection = Ui.Apply(new Button
    {
        Content = "Cancel sign-in", Margin = new Thickness(0, 5, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed
    });
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _health = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick };
    private readonly TextBlock _layoutMessage = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 5) };
    private readonly Button _editOrder = Ui.Apply(new Button { Content = "Edit order", IsEnabled = false, Margin = new Thickness(0, 1, 4, 1) });
    private readonly Button _saveOrder = Ui.Apply(new Button { Content = "Save order", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 1, 4, 1) }, true);
    private readonly Button _cancelOrder = Ui.Apply(new Button { Content = "Cancel", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 1, 4, 1) });
    private readonly Button _restore = Ui.Apply(new Button { Content = "Restore hidden accounts" });
    private readonly AccountLayoutState _layout = new();
    private readonly HashSet<string> _expandedAccounts = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _providerGate = new(1, 1);
    private string[] _confirmedProviders = [];
    private int _providerRequest;
    private bool _providersLoaded;
    private bool _polling;
    private bool _closed;
    private bool _savingLayout;
    private int _mutationRevision;
    private string? _activeJobId;
    private string _accountsSignature = string.Empty;
    private Point? _dragStart;

    private readonly Button _installCli = Ui.Apply(new Button
    {
        Content = "Install CLI", Tag = "antigravity-cli", Margin = new Thickness(0, 1, 6, 1), Visibility = Visibility.Collapsed,
        ToolTip = "Install the official Antigravity CLI (agy) so quota can be read without the desktop app."
    });
    private readonly Func<Window, bool> _confirmCliInstall;
    private readonly Action _startCliInstall;

    internal AccountsWindow(Window owner, HttpClient http, Action changed, UpdateService? updates = null, Func<bool>? backendReady = null,
        Func<Window, bool>? confirmCliInstall = null, Action? startCliInstall = null)
    {
        _confirmCliInstall = confirmCliInstall ?? AntigravityCliInstall.Confirm;
        _startCliInstall = startCliInstall ?? AntigravityCliInstall.Start;
        _updates = updates;
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        _backendReady = backendReady ?? (() => true);
        Title = "Settings and accounts";
        Icon = owner.Icon;
        Width = 640;
        MinWidth = 500;
        Height = 660;
        MinHeight = 430;
        Background = new SolidColorBrush(Color.FromRgb(248, 250, 252));
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

    private static TextBlock Heading(string text) => new()
    {
        Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Ui.Text, Margin = new Thickness(0, 4, 0, 2)
    };

    private static TextBlock Hint(string text, string? link = null)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Muted, FontSize = 12, Margin = new Thickness(0, 2, 0, 4) };
        block.Inlines.Add(new Run(text));
        if (link is not null)
        {
            var hyperlink = new Hyperlink(new Run("Learn more")) { NavigateUri = new Uri(link) };
            hyperlink.RequestNavigate += (_, e) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception) { }
            };
            block.Inlines.Add(new Run(" "));
            block.Inlines.Add(hyperlink);
        }
        return block;
    }

    private UIElement BuildContent()
    {
        var root = new DockPanel { Margin = new Thickness(16, 12, 16, 12) };
        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        footer.Children.Add(_job);
        _cancelConnection.Click += async (_, _) => await CancelConnectionAsync();
        footer.Children.Add(_cancelConnection);
        _message.Foreground = Brushes.Firebrick;
        _message.Margin = new Thickness(0, 4, 0, 0);
        footer.Children.Add(_message);
        footer.Children.Add(_health);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = new StackPanel();
        var startup = new CheckBox {
            Content = "Start with Windows (in the tray)", Margin = new Thickness(0, 0, 0, 4),
            ToolTip = "Starts when you sign in. Keep the portable app in a permanent folder. Turn this off before moving or deleting it."
        };
        try { startup.IsChecked = StartupRegistration.Enabled; }
        catch { startup.IsEnabled = false; _message.Text = "Windows startup settings are unavailable."; }
        startup.Click += (_, _) => {
            try { StartupRegistration.SetEnabled(startup.IsChecked == true); _message.Text = ""; }
            catch { startup.IsChecked = false; _message.Text = "Couldn't change Windows startup. Check your Windows account permissions."; }
        };
        body.Children.Add(startup);
        if (_updates is not null) {
            var updateRow = new DockPanel { Margin = new Thickness(0, 2, 0, 4), LastChildFill = true };
            var auto = new CheckBox { Content = "Check for updates automatically", IsChecked = _updates.Preferences.Automatic, VerticalAlignment = VerticalAlignment.Center };
            auto.Click += (_, _) => _updates.SetAutomatic(auto.IsChecked == true);
            var check = Ui.Button("Check for updates", () => _ = _updates.Check(true));
            DockPanel.SetDock(check, Dock.Right);
            updateRow.Children.Add(check);
            updateRow.Children.Add(auto);
            body.Children.Add(updateRow);
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Muted, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
            body.Children.Add(status);
            void UpdateStatus() { check.IsEnabled = !_updates.Busy; status.Text = $"Installed {UpdateService.InstalledVersion} · " + _updates.Status; }
            _updates.Changed += UpdateStatus;
            Closed += (_, _) => _updates.Changed -= UpdateStatus;
            UpdateStatus();
        }
        body.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 6) });
        body.Children.Add(Heading("Monitored providers"));
        body.Children.Add(Hint("Changes save right away. Sign-in uses each provider's official client, and the monitor never switches accounts."));

        foreach (string provider in Providers)
        {
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1), LastChildFill = false };
            var check = new CheckBox { Content = ProviderNames[provider], VerticalAlignment = VerticalAlignment.Center, Tag = provider, IsEnabled = false };
            check.Click += async (_, _) => await SaveProvidersAsync();
            _providerChecks[provider] = check;
            row.Children.Add(check);
            var signIn = Ui.Apply(new Button { Content = provider == "antigravity" ? "Open desktop app" : "Sign in", Tag = provider, MinWidth = 88, IsEnabled = false });
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
            body.Children.Add(row);
        }
        body.Children.Add(Hint("Antigravity: the CLI (agy) reads quota while the desktop app is closed. Open desktop app doesn't sign in the CLI.",
            Readme + "google-antigravity"));
        body.Children.Add(Hint("Copilot needs a one-time setup before Sign in.", "https://github.com/mahlernim/agent-quota-monitor-windows/blob/main/docs/copilot-setup.md"));

        body.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });
        var accountHeader = new DockPanel { LastChildFill = false };
        accountHeader.Children.Add(Heading("Displayed accounts"));
        _restore.Click += async (_, _) => await RestoreAsync();
        DockPanel.SetDock(_restore, Dock.Right);
        accountHeader.Children.Add(_restore);
        body.Children.Add(accountHeader);
        body.Children.Add(Hint("Hide removes an account from the monitor without signing out. To reorder, choose Edit order and drag rows or press Alt+Up and Alt+Down."));
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

        body.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 6) });
        body.Children.Add(Heading("Quota names"));
        body.Children.Add(Hint("Initials appear in the tray. Names appear on rings and in the floating monitor when they fit."));
        body.Children.Add(BuildNames());

        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(scroll);
        return root;
    }

    private UIElement BuildNames()
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        void Cell(UIElement element, int row, int column) { Grid.SetRow(element, row); Grid.SetColumn(element, column); grid.Children.Add(element); }
        grid.RowDefinitions.Add(new RowDefinition());
        Cell(new TextBlock { Text = "Quota", Foreground = Ui.Muted, FontSize = 11 }, 0, 0);
        Cell(new TextBlock { Text = "Initials", Foreground = Ui.Muted, FontSize = 11 }, 0, 1);
        Cell(new TextBlock { Text = "Name", Foreground = Ui.Muted, FontSize = 11 }, 0, 2);
        int index = 1;
        foreach (string type in QuotaNames.Types)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            QuotaName name = QuotaNames.For(type);
            var initials = new TextBox { Text = name.Initials, MaxLength = QuotaNames.MaxInitials, Width = 50, Margin = new Thickness(0, 2, 8, 2), Padding = new Thickness(3, 2, 3, 2) };
            var full = new TextBox { Text = name.Name, MaxLength = QuotaNames.MaxName, Width = 160, Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(3, 2, 3, 2) };
            AutomationProperties.SetName(initials, "Initials for " + QuotaNames.TypeLabels[type]);
            AutomationProperties.SetName(full, "Name for " + QuotaNames.TypeLabels[type]);
            _initialsFields[type] = initials;
            _nameFields[type] = full;
            Cell(new TextBlock { Text = QuotaNames.TypeLabels[type], VerticalAlignment = VerticalAlignment.Center }, index, 0);
            Cell(initials, index, 1);
            Cell(full, index, 2);
            index++;
        }
        var panel = new StackPanel();
        panel.Children.Add(grid);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var save = Ui.Button("Save names", () => _ = SaveNamesAsync());
        save.Margin = new Thickness(0, 1, 4, 1);
        actions.Children.Add(save);
        actions.Children.Add(Ui.Button("Reset to defaults", () =>
        {
            foreach (string type in QuotaNames.Types)
            {
                _initialsFields[type].Text = QuotaNames.Defaults[type].Initials;
                _nameFields[type].Text = QuotaNames.Defaults[type].Name;
            }
            _ = SaveNamesAsync();
        }));
        panel.Children.Add(actions);
        return panel;
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
                _health.Text = "Couldn't read account status. Displayed information may be out of date.";
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
            _health.Text = "Couldn't read account status. Displayed information may be out of date.";
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
            {
                check.IsChecked = selected.Contains(provider);
                check.IsEnabled = true;
            }
            _confirmedProviders = Providers.Where(selected.Contains).ToArray();
            _providersLoaded = true;
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

        _job.Text = "";
        _job.Visibility = Visibility.Collapsed;
        _activeJobId = null;
        _cancelConnection.Visibility = Visibility.Collapsed;
        if (hasConnections && connections.TryGetProperty("job", out JsonElement job) && job.ValueKind == JsonValueKind.Object)
        {
            string provider = Text(job, "provider");
            string state = Text(job, "state").Replace('_', ' ');
            string message = Text(job, "message");
            _job.Text = $"{ProviderNames.GetValueOrDefault(provider, provider)} · {state}\n{message}".Trim();
            _job.Visibility = Visibility.Visible;
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
        _layoutMessage.Text = conflict ? "The account list changed. Cancel editing and try again. Your draft order has been kept."
            : editing ? "Drag rows or press Alt+Up and Alt+Down. Order changes aren't saved yet." : "";
        _layoutMessage.Foreground = conflict ? Brushes.Firebrick : Ui.Muted;
        _layoutMessage.Visibility = _layoutMessage.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var rows = _layout.Displayed;
        string signature = JsonSerializer.Serialize(new { rows, editing, conflict, _savingLayout, expanded = _expandedAccounts.OrderBy(id => id) });
        if (!force && signature == _accountsSignature)
            return;
        _accountsSignature = signature;
        string? focusedRow = (Keyboard.FocusedElement as FrameworkElement)?.Tag as string;
        _accounts.Children.Clear();
        foreach (AccountStatus account in rows)
            _accounts.Children.Add(AccountRow(account, editing, conflict));
        if (rows.Count == 0)
            _accounts.Children.Add(new TextBlock { Text = "No accounts displayed. Restore hidden accounts or enable a provider.", TextWrapping = TextWrapping.Wrap });
        if (focusedRow is not null) FocusRow(focusedRow);
    }

    private FrameworkElement AccountRow(AccountStatus account, bool editing, bool conflict)
    {
        string label = $"{ProviderNames.GetValueOrDefault(account.Provider, account.Provider)} · {account.Label}";
        bool expanded = _expandedAccounts.Contains(account.Id);
        var line = new DockPanel { LastChildFill = true };
        if (!editing)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var details = Ui.Apply(new Button
            {
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = {
                    new TextBlock { Text = "Details", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) },
                    Icons.Create(expanded ? Icons.ChevronUp : Icons.ChevronDown, 12, Ui.Muted) } },
                Tag = "details:" + account.Id
            });
            AutomationProperties.SetName(details, (expanded ? "Hide details for " : "Show details for ") + label);
            details.Click += (_, _) =>
            {
                if (!_expandedAccounts.Remove(account.Id)) _expandedAccounts.Add(account.Id);
                RenderAccounts(true);
                FocusRow("details:" + account.Id);
            };
            actions.Children.Add(details);
            var copy = Ui.IconButton(Icons.Copy, "Copy details for a support request", () => CopyDetails(account), 14, 26);
            AutomationProperties.SetName(copy, "Copy details for " + label);
            actions.Children.Add(copy);
            var hide = Ui.IconButton(Icons.EyeOff, "Hide from the monitor without signing out", () => _ = RemoveAsync(account.Id), 14, 26);
            AutomationProperties.SetName(hide, "Hide " + label);
            hide.Tag = "hide:" + account.Id;
            actions.Children.Add(hide);
            DockPanel.SetDock(actions, Dock.Right);
            line.Children.Add(actions);
        }
        else
        {
            var grip = Icons.Create(Icons.Grip, 14, Ui.Muted);
            grip.Margin = new Thickness(0, 0, 6, 0);
            DockPanel.SetDock(grip, Dock.Left);
            line.Children.Add(grip);
        }
        var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
            Fill = Ui.Hex(account.Provider switch { "codex" => "#168f87", "claude" => "#c46843", "antigravity" => "#287bc1", "copilot" => "#488b74", _ => "#64748b" }) };
        DockPanel.SetDock(dot, Dock.Left);
        line.Children.Add(dot);
        bool healthy = account.Status == "live" && account.Error.Length == 0;
        var chip = Ui.Chip(account.Status.Length > 0 ? account.Status : "unknown", healthy);
        DockPanel.SetDock(chip, Dock.Right);
        line.Children.Add(chip);
        line.Children.Add(new TextBlock { Text = label, ToolTip = label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });

        var content = new StackPanel();
        content.Children.Add(line);
        if (account.Guidance.Length > 0)
            content.Children.Add(new TextBlock { Text = account.Guidance, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Ui.WarningText, Margin = new Thickness(14, 2, 0, 0) });
        if (expanded && !editing)
            content.Children.Add(new TextBlock { Text = DetailText(account), Tag = "detail-text:" + account.Id, TextWrapping = TextWrapping.Wrap,
                FontSize = 12, Foreground = Ui.Muted, Margin = new Thickness(14, 4, 0, 2) });

        var row = new Border
        {
            Child = content, Padding = new Thickness(6, 5, 4, 5), BorderBrush = Ui.Hairline, BorderThickness = new Thickness(0, 0, 0, 1),
            Tag = account.Id, Background = Brushes.Transparent
        };
        if (editing)
        {
            row.Focusable = !_savingLayout && !conflict;
            row.Cursor = Cursors.SizeAll;
            row.AllowDrop = true;
            row.FocusVisualStyle = null;
            row.GotKeyboardFocus += (_, _) => row.Background = Ui.AccentTint;
            row.LostKeyboardFocus += (_, _) => row.Background = Brushes.Transparent;
            AutomationProperties.SetName(row, label + ". Press Alt+Up or Alt+Down to move.");
            row.PreviewKeyDown += (_, e) =>
            {
                Key key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (Keyboard.Modifiers != ModifierKeys.Alt || key is not (Key.Up or Key.Down)) return;
                e.Handled = MoveRow(account.Id, key == Key.Up ? -1 : 1);
            };
            row.PreviewMouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(this); row.Focus(); };
            row.PreviewMouseMove += (_, e) =>
            {
                if (_dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed || _savingLayout || conflict) return;
                Vector moved = e.GetPosition(this) - start;
                if (Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _dragStart = null;
                DragDrop.DoDragDrop(row, new DataObject(RowFormat, account.Id), DragDropEffects.Move);
            };
            row.DragOver += (_, e) =>
            {
                e.Effects = e.Data.GetData(RowFormat) is string id && id != account.Id ? DragDropEffects.Move : DragDropEffects.None;
                row.BorderBrush = e.Effects == DragDropEffects.Move ? Ui.Accent : Ui.Hairline;
                e.Handled = true;
            };
            row.DragLeave += (_, _) => row.BorderBrush = Ui.Hairline;
            row.Drop += (_, e) =>
            {
                row.BorderBrush = Ui.Hairline;
                if (e.Data.GetData(RowFormat) is not string id) return;
                e.Handled = true;
                var order = _layout.Order.ToList();
                MoveRow(id, order.IndexOf(account.Id) - order.IndexOf(id));
            };
        }
        return row;
    }

    private static string DetailText(AccountStatus account)
    {
        string retry = account.RetryState == "suspended" ? "Paused until the provider reports a usable retry time" : AccountStatus.Timestamp(account.NextAttempt, "Not reported");
        var lines = new List<string>
        {
            "Last read · " + AccountStatus.Timestamp(account.LastSuccess, "Never"),
            "Next read · " + retry,
            "Source · " + account.SourceText
        };
        if (account.SessionExpiresAt.HasValue) lines.Add("Session expires · " + AccountStatus.Timestamp(account.SessionExpiresAt, "Not reported"));
        if (account.SessionRenewedAt.HasValue) lines.Add("Session last renewed · " + AccountStatus.Timestamp(account.SessionRenewedAt, "Not reported"));
        if (account.QuotaDetails.Length > 0) lines.Add(account.QuotaDetails);
        return string.Join("\n", lines);
    }

    private void CopyDetails(AccountStatus account)
    {
        try { Clipboard.SetText(account.Diagnostics()); ShowSuccess("Details copied. They contain no credentials or account label."); }
        catch (System.Runtime.InteropServices.ExternalException) { ShowError("The clipboard is busy. Try again."); }
    }

    /// <summary>Moves a row inside the unsaved draft. Save order or Cancel decides what happens to it.</summary>
    private bool MoveRow(string id, int offset)
    {
        if (!_layout.Editing || _layout.HasConflict || _savingLayout || offset == 0) return false;
        bool moved = false;
        for (int step = 0; step < Math.Abs(offset); step++)
            moved |= _layout.Move(id, Math.Sign(offset));
        if (moved)
        {
            RenderAccounts(true);
            FocusRow(id);
        }
        return moved;
    }

    private void FocusRow(string tag)
    {
        IEnumerable<FrameworkElement> Walk(DependencyObject node)
        {
            if (node is FrameworkElement element) yield return element;
            foreach (object child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject next)
                    foreach (FrameworkElement found in Walk(next)) yield return found;
        }
        Walk(_accounts).FirstOrDefault(element => element.Focusable && Equals(element.Tag, tag))?.Focus();
    }

    private async Task SaveOrderAsync()
    {
        if (_closed) return;
        if (!_backendReady()) { ShowError("Quota reader unavailable. The account order hasn't been saved."); return; }
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
        catch (Exception) { ShowError("The account order couldn't be saved. Your draft order has been kept."); }
        finally
        {
            ++_mutationRevision;
            _savingLayout = false;
            RenderAccounts(true);
            await PollAsync();
        }
    }

    /// <summary>
    /// Saves the full provider selection. Writes run one at a time, and only the newest selection
    /// is sent or allowed to change the checkboxes, so a slow response can't undo a later click.
    /// </summary>
    private async Task SaveProvidersAsync()
    {
        int request = ++_providerRequest;
        await _providerGate.WaitAsync();
        try
        {
            if (request != _providerRequest || _closed) return;
            string[] enabled = _providerChecks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToArray();
            if (!_backendReady()) { RestoreProviders(); ShowError("Quota reader unavailable. Provider changes weren't saved."); return; }
            using HttpResponseMessage response = await BackendRequests.PostAsync(_http, "/api/providers", new { enabled });
            if (request != _providerRequest) return;
            if (!response.IsSuccessStatusCode) { RestoreProviders(); ShowError(await BackendErrorAsync(response)); return; }
            _confirmedProviders = enabled;
            ShowSuccess("Monitored providers saved.");
            _changed();
        }
        catch (Exception)
        {
            if (request == _providerRequest) { RestoreProviders(); ShowError("Provider changes couldn't be saved."); }
        }
        finally { _providerGate.Release(); }
    }

    private void RestoreProviders()
    {
        foreach ((string provider, CheckBox check) in _providerChecks)
            check.IsChecked = _confirmedProviders.Contains(provider);
    }

    private async Task SaveNamesAsync()
    {
        var names = QuotaNames.Types.ToDictionary(type => type, type => new QuotaName(_initialsFields[type].Text.Trim(), _nameFields[type].Text.Trim()));
        if (names.Values.Any(name => !QuotaNames.ValidInitials(name.Initials) || !QuotaNames.ValidName(name.Name)))
        {
            ShowError($"Initials need 1 to {QuotaNames.MaxInitials} letters or digits. Names need 1 to {QuotaNames.MaxName} characters.");
            return;
        }
        if (!_backendReady()) { ShowError("Quota reader unavailable. Names weren't saved."); return; }
        try
        {
            using HttpResponseMessage response = await BackendRequests.PostAsync(_http, "/api/desktop", new { quotaLabels = QuotaNames.ToPreference(names) });
            if (!response.IsSuccessStatusCode) { ShowError("Names couldn't be saved."); return; }
            QuotaNames.Set(names);
            ShowSuccess("Quota names saved.");
            _changed();
        }
        catch (Exception) { ShowError("Names couldn't be saved."); }
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
        catch (Exception) { ShowError("PowerShell couldn't be started. Install the CLI from antigravity.google/docs/cli/install."); }
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
