using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace AgentQuotaMonitor;

public sealed class App : Application
{
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = new Uri("http://127.0.0.1:8765"), Timeout = TimeSpan.FromSeconds(8) };
    private MainWindow? main; private FloatingWindow? floating; private TrayController? tray;
    private List<QuotaItem> items = new(); private string? selected; private readonly HashSet<string> pins = new();
    private bool stopping; private bool polling; private bool preferencesLoaded; private bool backendReady; private bool connecting;
    private bool reorderBusy;
    private List<string> accountIds = new();
    private List<AccountStatus> accountStates = new();
    private SingleInstance? instance; private DispatcherTimer? timer;
    private readonly CancellationTokenSource lifetime = new();
    private readonly BackendConnection? backendConnection;
    private readonly Func<CancellationToken, Task> ensureBackend;
    private readonly Func<CancellationToken, Task> stopBackend;
    private readonly Action? exitOverride;
    private Task startupTask = Task.CompletedTask, pollTask = Task.CompletedTask, activationTask = Task.CompletedTask;
    private CancellationTokenSource? pollCancellation;
    private Task? quitTask;
    private double opacity = 0.85;
    private DispatcherTimer? placementSave;
    private UpdateService? updates;
    private DispatcherTimer? updateTimer;
    private bool offlineCheck;
    private bool formatUpdateChecked;
    private string? updateProgress;
    private string? pendingInstaller;
    // Only the per-user installation can be upgraded in place. Portable copies keep Download update.
    private static bool Installed => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));
    private DispatcherTimer? wakeTimer;
    public App()
    {
        backendConnection = new BackendConnection(http, LaunchBackend, UpdateService.InstalledVersion);
        ensureBackend = backendConnection.EnsureAsync;
        stopBackend = backendConnection.StopOwnedAsync;
    }
    internal App(HttpClient client, Func<CancellationToken, Task> ensure, Func<CancellationToken, Task> stop, Action exited, BackendConnection? connection = null)
    {
        http.Dispose();
        http = client;
        ensureBackend = ensure;
        stopBackend = stop;
        backendConnection = connection;
        exitOverride = exited;
        offlineCheck = true;
    }
    [STAThread] public static void Main(string[] args)
    {
        var app = new App { offlineCheck = args.Length == 2 && (args[0] is "--self-test-output" or "--screenshots-output") };
        if (args.Length == 2 && args[0] == "--self-test-output") { app.SelfTest(args[1]); return; }
        if (args.Length == 2 && args[0] == "--screenshots-output") { PreviewImages.Create(args[1]); return; }
        app.Run();
    }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e); ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (offlineCheck) return;
        instance = new SingleInstance();
        if (!instance.IsPrimary) { instance.Dispose(); instance = null; Shutdown(); return; }
        activationTask = Task.Run(() =>
        {
            if (lifetime.IsCancellationRequested) return;
            WaitHandle[] waits = { instance.Activation, lifetime.Token.WaitHandle };
            while (WaitHandle.WaitAny(waits) == 0 && !lifetime.IsCancellationRequested)
                Dispatcher.BeginInvoke(() => { if (!stopping) ShowMain(); });
        });
        http.DefaultRequestHeaders.Add("Origin", "http://127.0.0.1:8765");
        http.DefaultRequestHeaders.Add("X-Quota-Request", "refresh");
        main = new MainWindow(SelectTray, TogglePin, ToggleFloating, () => _ = RefreshAsync(), () => _ = Quit(), ShowAccounts,
            (item, offset) => _ = MoveRingAsync(item, offset),
            (accountId, offset) => _ = MoveAccountAsync(accountId, offset),
            (account, action) => _ = AccountActionAsync(account, action));
        main.Closing += (_, ev) => { if (!stopping) { ev.Cancel = true; main.Hide(); } };
        floating = new FloatingWindow(ShowMain, HideFloating);
        main.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/robot-ring.ico"));
        floating.Icon = main.Icon;
        placementSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        placementSave.Tick += (_, _) => { placementSave.Stop(); if (preferencesLoaded && !stopping) _ = Post("/api/desktop", new { wpfFloatingLeft = floating.Left, wpfFloatingTop = floating.Top, desktopOpacity = Math.Round(floating.Opacity * 100), desktopFloatingScale = Math.Round(floating.MonitorScale * 100) }); };
        floating.ScaleChanged += () => { if (preferencesLoaded) { placementSave.Stop(); placementSave.Start(); } };
        floating.LocationChanged += (_, _) => { if (preferencesLoaded) { placementSave.Stop(); placementSave.Start(); } };
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Window.OpacityProperty, typeof(Window)).AddValueChanged(floating, (_, _) => { if (preferencesLoaded) { opacity = floating.Opacity; placementSave.Stop(); placementSave.Start(); } });
        tray = new TrayController(ShowMain, ShowAccounts, ToggleFloating, () => _ = Quit());
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        if (!Environment.GetCommandLineArgs().Contains("--minimized")) main.Show();
        await StartServices();
    }
    private Task StartServices()
    {
        if (stopping) return Task.CompletedTask;
        if (connecting) return startupTask;
        connecting = true;
        backendReady = false;
        timer?.Stop();
        BestEffort(() => pollCancellation?.Cancel());
        main?.SetConnectionState(false, true);
        main?.ShowBackendError("Connecting to the quota reader. Please wait.");
        return startupTask = ConnectBackendCore();
    }
    private async Task ConnectBackendCore()
    {
        bool peerConnected = false;
        try
        {
            // Drain the cancelled poll before changing the accepted peer identity.
            await pollTask.WaitAsync(lifetime.Token);
            if (stopping) return;
            await ensureBackend(lifetime.Token);
            if (stopping) return;
            peerConnected = true;
            backendReady = true;
            pollTask = PollCore(true);
            await pollTask;
        }
        catch (OperationCanceledException) when (stopping) { }
        catch (Exception error)
        {
            if (!stopping)
            {
                backendReady = false;
                foreach (var item in items) item.MarkDisconnected();
                Render();
                if (main is not null)
                {
                    main.Title = "Agent Quota Monitor · backend unavailable";
                    main.ShowBackendError(error is BackendConnectionException ? error.Message : "The quota reader could not be connected. Choose Retry connection to try again.");
                }
            }
        }
        finally
        {
            connecting = false;
            if (!stopping)
            {
                InitializeUpdates();
                if (peerConnected) StartPolling();
                main?.SetConnectionState(backendReady, false);
            }
        }
    }
    private async Task RefreshAsync()
    {
        if (stopping) return;
        if (!backendReady || connecting)
        {
            await StartServices();
            return;
        }
        await Post("/api/refresh", new {});
    }
    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    { if (e.Mode == Microsoft.Win32.PowerModes.Resume) ScheduleWake(); }
    private void OnNetworkAvailabilityChanged(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
    { if (e.IsAvailable) ScheduleWake(); }
    // Resume and network events arrive on other threads, often before the network is usable.
    private void ScheduleWake() => Dispatcher.BeginInvoke(() =>
    {
        if (stopping) return;
        if (wakeTimer is null)
        {
            wakeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            wakeTimer.Tick += (_, _) => { wakeTimer.Stop(); if (!stopping) _ = Post("/api/wake", new {}); };
        }
        wakeTimer.Stop();
        wakeTimer.Start();
    });
    private void CheckForFormatUpdate(JsonElement root)
    {
        // One early check per session when a provider format changed. Automatic checks must be on.
        if (formatUpdateChecked || updates is null) return;
        if (!root.GetProperty("accounts").EnumerateArray().Any(account => QuotaItem.Text(account, "error") == "schema_changed")) return;
        formatUpdateChecked = true;
        _ = updates.Check(urgent: true);
    }
    private void ShowUpdate()
    {
        if (!stopping && updates is not null)
            main?.SetUpdate(updates, Installed ? () => _ = InstallUpdateAsync() : null, updateProgress);
    }
    private async Task InstallUpdateAsync()
    {
        var release = updates?.Available;
        if (release is null || stopping || updateProgress is not null || main is null) return;
        string version = release.Tag.TrimStart('v');
        var answer = MessageBox.Show(main, $"Install version {version} now?\n\n" +
            "The monitor downloads the installer from this project's GitHub release, checks it against the published SHA-256 checksum, " +
            "closes itself, and opens the installer. Your settings and cached quota are kept.",
            "Install update", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK || stopping) return;
        updateProgress = $"Downloading and verifying version {version}…";
        ShowUpdate();
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "AgentQuotaMonitorUpdate");
            // Installers from earlier updates are no longer needed once this app runs.
            BestEffort(() => { if (Directory.Exists(root)) Directory.Delete(root, true); });
            string folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
            pendingInstaller = await updates!.DownloadInstaller(release, folder, lifetime.Token);
            await Quit();
        }
        catch (OperationCanceledException) when (stopping) { }
        catch
        {
            pendingInstaller = null;
            updateProgress = null;
            ShowUpdate();
            main?.ShowBackendError("The update could not be downloaded or verified. Nothing was installed. Try again later or choose Download update.");
        }
    }
    private void InitializeUpdates()
    {
        if (updates is not null || stopping) return;
        updates = new UpdateService();
        updates.Changed += ShowUpdate;
        updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        updateTimer.Tick += async (_, _) =>
        {
            if (stopping) return;
            updateTimer.Interval = TimeSpan.FromHours(1);
            await updates.Check();
        };
        updateTimer.Start();
    }
    private void StartPolling()
    {
        if (timer is null)
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += async (_, _) => await Poll();
        }
        timer.Start();
    }
    private static Process LaunchBackend()
    {
        string packaged = Path.Combine(AppContext.BaseDirectory, "backend", "quota-backend.exe");
        var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
        if (File.Exists(packaged)) start.FileName = packaged;
        else
        {
            var root = FindSource(); start.FileName = Path.Combine(root, ".venv-desktop", "Scripts", "pythonw.exe");
            start.WorkingDirectory = root; start.ArgumentList.Add("-m"); start.ArgumentList.Add("quota.server");
        }
        start.ArgumentList.Add("--app-version"); start.ArgumentList.Add(UpdateService.InstalledVersion);
        return Process.Start(start) ?? throw new InvalidOperationException();
    }
    private static string FindSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null) { if (File.Exists(Path.Combine(dir.FullName, "quota", "server.py"))) return dir.FullName; dir = dir.Parent; }
        throw new InvalidOperationException("Backend was not packaged");
    }
    private Task Poll()
    {
        if (polling || stopping || connecting) return Task.CompletedTask;
        return pollTask = PollCore();
    }
    private async Task PollCore(bool duringConnection = false)
    {
        polling = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        pollCancellation = cancellation;
        try
        {
            using var data = JsonDocument.Parse(await http.GetStringAsync("/api/status", cancellation.Token));
            if (stopping || (connecting && !duringConnection)) return;
            backendConnection?.ValidatePeer(data.RootElement);
            List<QuotaItem> candidate = QuotaSnapshot.Parse(data.RootElement);
            List<string> candidateAccountIds = data.RootElement.GetProperty("accounts").EnumerateArray()
                .Select(account => QuotaItem.Text(account, "id")).ToList();
            List<AccountStatus> candidateStates = AccountStatus.Parse(data.RootElement).ToList();
            if (!preferencesLoaded)
            {
                using var prefs = JsonDocument.Parse(await http.GetStringAsync("/api/desktop", cancellation.Token));
                if (stopping || (connecting && !duringConnection)) return;
                var p = prefs.RootElement;
                if (p.TryGetProperty("desktopSelection", out var s)) selected = ReadKey(s);
                if (p.TryGetProperty("desktopFloatingSelections", out var list) && list.ValueKind == JsonValueKind.Array)
                { foreach (var pin in list.EnumerateArray()) { var key = ReadKey(pin); if (key != null) pins.Add(key); } }
                else if (selected != null) pins.Add(selected);
                opacity = Math.Clamp((QuotaItem.Number(p, "desktopOpacity") ?? 85) / 100, .35, 1);
                if (QuotaItem.Number(p, "wpfFloatingLeft") is double x) floating!.Left = x;
                if (QuotaItem.Number(p, "wpfFloatingTop") is double y) floating!.Top = y;
                floating!.SetScale((QuotaItem.Number(p, "desktopFloatingScale") ?? 100) / 100);
                QuotaNames.Apply(p);
                preferencesLoaded = true;
                if (p.TryGetProperty("desktopFloating", out var f) && f.ValueKind == JsonValueKind.True) { floating!.Opacity = opacity; floating.Show(); }
            }
            if (stopping || (connecting && !duringConnection)) return;
            items = candidate;
            accountIds = candidateAccountIds;
            accountStates = candidateStates;
            backendReady = true;
            CheckForFormatUpdate(data.RootElement);
            main?.ShowBackendError("");
            selected ??= items.FirstOrDefault()?.Key;
            if (Render() && main is not null) main.Title = "Agent Quota Monitor Windows";
        }
        catch (OperationCanceledException) when (stopping || (connecting && !duringConnection)) { }
        catch (BackendConnectionException error)
        {
            if (stopping || (connecting && !duringConnection)) return;
            backendReady = false;
            main?.ShowBackendError(error.Message);
            foreach (var item in items) item.MarkDisconnected();
            Render();
        }
        catch
        {
            if (stopping || (connecting && !duringConnection)) return;
            bool wasReady = backendReady;
            backendReady = false;
            if (wasReady) main?.ShowBackendError("The quota reader is unavailable or returned invalid data. Cached values are shown. Choose Retry connection to reconnect.");
            if (main is not null) main.Title = "Agent Quota Monitor · disconnected";
            foreach (var item in items) item.MarkDisconnected();
            Render();
        }
        finally
        {
            polling = false;
            if (ReferenceEquals(pollCancellation, cancellation)) pollCancellation = null;
            if (!stopping && !connecting) main?.SetConnectionState(backendReady, false);
        }
    }
    private static string? ReadKey(JsonElement e) => e.ValueKind != JsonValueKind.Object ? null :
        QuotaItem.MakeKey(QuotaItem.Text(e, "accountId"), QuotaItem.Text(e, "groupId"), QuotaItem.Text(e, "bucketId"));
    private bool Render()
    {
        if (stopping) return false;
        try { main?.SetData(items, selected, pins, accountStates); floating?.SetData(items.Where(q => pins.Contains(q.Key)).ToList()); tray?.SetQuota(items.FirstOrDefault(q => q.Key == selected)); return true; }
        catch { if (!stopping && main is not null) main.Title = "Agent Quota Monitor · display unavailable"; return false; }
    }
    private void SelectTray(QuotaItem item) { if (stopping || !backendReady || connecting) return; selected = item.Key; Render(); _ = Post("/api/desktop", new { desktopSelection = item.Selection }); }
    private void TogglePin(QuotaItem item)
    {
        if (stopping || !backendReady || connecting) return;
        if (!pins.Add(item.Key)) pins.Remove(item.Key); Render();
        var values = pins.Select(key => { var ids = JsonSerializer.Deserialize<string[]>(key)!; return new { accountId = ids[0], groupId = ids[1], bucketId = ids[2] }; }).ToArray();
        _ = Post("/api/desktop", new { desktopFloatingSelections = values });
    }
    /// <summary>Moves a ring by an offset within its own account, as a menu, key, or drag and drop requests.</summary>
    private async Task MoveRingAsync(QuotaItem item, int offset)
    {
        if (reorderBusy || stopping || !backendReady || connecting || offset == 0) return;
        var reordered = items.ToList();
        var slots = Enumerable.Range(0, reordered.Count).Where(index => reordered[index].AccountId == item.AccountId).ToList();
        var rings = slots.Select(index => reordered[index]).ToList();
        int from = rings.FindIndex(row => row.Key == item.Key), to = from + offset;
        if (from < 0 || to < 0 || to >= rings.Count) return;
        var moving = rings[from];
        rings.RemoveAt(from);
        rings.Insert(to, moving);
        for (int index = 0; index < slots.Count; index++) reordered[slots[index]] = rings[index];
        reorderBusy = true;
        main?.SetReorderBusy(true);
        try
        {
            using var response = await BackendRequests.PostAsync(http, "/api/accounts/rings",
                new { order = reordered.Select(row => row.Selection).ToArray() }, lifetime.Token);
            response.EnsureSuccessStatusCode();
            if (polling) await pollTask;
            await Poll();
        }
        catch (OperationCanceledException) when (stopping) { }
        catch { main?.ShowBackendError("Could not save ring order. Refresh and try again."); }
        finally { reorderBusy = false; main?.SetReorderBusy(false); }
    }
    /// <summary>Moves an account among the accounts the main window shows. Hidden accounts keep their saved slots.</summary>
    private async Task MoveAccountAsync(string accountId, int offset)
    {
        if (reorderBusy || stopping || !backendReady || connecting || offset == 0) return;
        var visible = (main?.AccountOrder ?? items.Select(row => row.AccountId).Distinct(StringComparer.Ordinal).ToList())
            .Where(accountIds.Contains).ToList();
        int from = visible.IndexOf(accountId), to = from + offset;
        if (from < 0 || to < 0 || to >= visible.Count) return;
        var reordered = accountIds.ToList();
        var slots = visible.Select(id => reordered.IndexOf(id)).OrderBy(index => index).ToList();
        visible.RemoveAt(from);
        visible.Insert(to, accountId);
        for (int index = 0; index < slots.Count; index++) reordered[slots[index]] = visible[index];
        reorderBusy = true;
        main?.SetReorderBusy(true);
        try
        {
            using var response = await BackendRequests.PostAsync(http, "/api/accounts/layout",
                new { order = reordered, removed = Array.Empty<string>() }, lifetime.Token);
            response.EnsureSuccessStatusCode();
            if (polling) await pollTask;
            await Poll();
        }
        catch (OperationCanceledException) when (stopping) { }
        catch { main?.ShowBackendError("Could not save account order. Refresh and try again."); }
        finally { reorderBusy = false; main?.SetReorderBusy(false); }
    }
    /// <summary>Runs the one action offered for an account problem in the main window.</summary>
    private async Task AccountActionAsync(AccountStatus account, string action)
    {
        if (stopping || main is null) return;
        switch (action)
        {
            case "connect-copilot":
                await ActionPost("/api/connections/start", new { provider = "copilot", accountId = account.Id, verify = true },
                    "Linking the account the GitHub CLI is signed in to. Settings shows its progress.");
                return;
            case "install-cli" or "install-codex" or "install-claude" or "setup-copilot":
                var installer = action switch
                {
                    "install-codex" => OfficialInstall.Codex, "install-claude" => OfficialInstall.Claude,
                    "setup-copilot" => OfficialInstall.Copilot, _ => OfficialInstall.Antigravity
                };
                if (!installer.Confirm(main)) return;
                try { installer.Start(); main.ShowNotice("The official installer opened in PowerShell. Follow that window, then return here."); }
                catch (Exception) { main.ShowNotice("PowerShell couldn't be started. Run the install command shown in the confirmation yourself."); }
                return;
            case "copy-agy":
                try { Clipboard.SetText("agy -p /usage"); main.ShowNotice("Copied agy -p /usage. Run it in a terminal and sign in if asked."); }
                catch (System.Runtime.InteropServices.ExternalException) { main.ShowNotice("The clipboard is busy. Run agy -p /usage in a terminal."); }
                return;
            case "check-updates":
                if (updates is null) return;
                await updates.Check(true);
                if (!stopping) main.ShowNotice(updates.Status);
                return;
            case "retry":
                await ActionPost("/api/wake", new { }, "Retrying accounts that couldn't connect. Provider cooldowns still apply.");
                return;
            case "open-desktop":
                await ActionPost("/api/connections/start", new { provider = "antigravity" }, "Opening the Antigravity desktop app.");
                return;
            case "sign-in":
                await ActionPost("/api/connections/start", new { provider = account.Provider, accountId = account.Id },
                    "Official sign-in started. Follow the browser or console window. Settings shows its progress.");
                return;
        }
    }
    private async Task ActionPost(string path, object body, string success)
    {
        if (stopping || !backendReady || connecting) { main?.ShowNotice("The quota reader isn't connected. Choose Retry connection first."); return; }
        try
        {
            using var response = await BackendRequests.PostAsync(http, path, body, lifetime.Token);
            if (response.IsSuccessStatusCode) { main?.ShowNotice(success); return; }
            string message = "The request couldn't be completed.";
            try
            {
                using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (error.RootElement.TryGetProperty("error", out var text) && text.ValueKind == JsonValueKind.String) message = text.GetString()!;
            }
            catch (JsonException) { }
            main?.ShowNotice(message.Length <= 300 ? message : message[..300]);
        }
        catch (OperationCanceledException) when (stopping) { }
        catch (Exception) { main?.ShowNotice("The request couldn't be completed."); }
    }
    private void ShowMain() { if (stopping) return; main?.Show(); if (main != null) { main.WindowState = WindowState.Normal; main.Activate(); } }
    private AccountsWindow? accounts;
    private void ShowAccounts()
    {
        if (stopping) return;
        ShowMain();
        if (accounts != null) { accounts.Activate(); return; }
        accounts = new AccountsWindow(main!, http, () => _ = Poll(), updates, () => backendReady && !stopping && !connecting);
        accounts.Closed += (_, _) => accounts = null; accounts.Show();
    }
    private void ToggleFloating() { if (stopping) return; if (floating!.IsVisible) HideFloating(); else { floating.Opacity = opacity; floating.Show(); _ = Post("/api/desktop", new { desktopFloating = true }); } }
    private void HideFloating() { if (stopping) return; floating?.Hide(); _ = Post("/api/desktop", new { desktopFloating = false }); }
    private async Task Post(string path, object value)
    {
        if (stopping || !backendReady || connecting) return;
        try { using var response = await BackendRequests.PostAsync(http, path, value, lifetime.Token); response.EnsureSuccessStatusCode(); }
        catch (OperationCanceledException) when (stopping) { }
        catch { if (!stopping && main != null) main.Title = "Agent Quota Monitor · could not save or refresh"; }
    }
    private Task Quit() => quitTask ??= QuitCore();
    private async Task QuitCore()
    {
        stopping = true;
        BestEffort(lifetime.Cancel);
        BestEffort(() => updateTimer?.Stop());
        BestEffort(() => updates?.Dispose());
        BestEffort(() => timer?.Stop());
        BestEffort(() => placementSave?.Stop());
        BestEffort(() => wakeTimer?.Stop());
        BestEffort(() => Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged);
        BestEffort(() => System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged);
        try
        {
            try { await Task.WhenAll(startupTask, pollTask, activationTask).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception) { }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try { await stopBackend(timeout.Token); }
            catch (Exception) { }
        }
        finally
        {
            BestEffort(() => tray?.Dispose());
            BestEffort(() => accounts?.Close());
            BestEffort(() => floating?.CloseForShutdown());
            BestEffort(() => main?.Close());
            BestEffort(() => backendConnection?.Dispose());
            BestEffort(http.Dispose);
            BestEffort(() => instance?.Dispose());
            // Start the verified installer only after the reader stopped and the instance mutex was released.
            if (pendingInstaller is not null)
                BestEffort(() => Process.Start(new ProcessStartInfo(pendingInstaller) { UseShellExecute = true })?.Dispose());
            if (exitOverride is not null) exitOverride();
            else Shutdown();
        }
    }
    private static void BestEffort(Action cleanup) { try { cleanup(); } catch (Exception) { } }
    private void SelfTest(string output)
    {
        PresentationChecks.Run();
        using var document = JsonDocument.Parse("{\"accounts\":[]}");
        if (QuotaItem.Parse(document.RootElement).Count != 0) throw new Exception("Empty provider handling");
        var q = new QuotaItem { Key = "test", Code = "GM", Remaining = 71.011746, TimeRemaining = 50, Status = "live" };
        if (QuotaItem.Percent(q.Remaining.Value).Contains("011")) throw new Exception("Precision");
        var warning = new QuotaItem { Code = "GM", Remaining = 39, TimeRemaining = 80, Status = "live" };
        var critical = new QuotaItem { Code = "GM", Remaining = 19, TimeRemaining = 80, Status = "live" };
        var boundary = new QuotaItem { Code = "GM", Remaining = 40, TimeRemaining = 80, Status = "live" };
        if (warning.NumberColor != "#c18a19" || critical.NumberColor != "#c54444" || boundary.NumberColor != "#25313d") throw new Exception("Pace thresholds");
        if (warning.Color != warning.IdentityColor || critical.Color != critical.IdentityColor) throw new Exception("Identity ring changed");
        if (StartupRegistration.Command("monitor.exe") != "\"monitor.exe\" --minimized") throw new Exception("Startup command quoting");
        var sizing = new FloatingWindow(() => {}, () => {}); sizing.SetScale(1.5);
        if (sizing.MonitorScale != 1.5) throw new Exception("Floating scale");
        var d = new Donut { Item = q, Width = 56, Height = 56 }; d.Measure(new Size(56, 56)); d.Arrange(new Rect(0, 0, 56, 56));
        var image = new System.Windows.Media.Imaging.RenderTargetBitmap(112,112,192,192,System.Windows.Media.PixelFormats.Pbgra32); image.Render(d);
        File.WriteAllText(output, "{\"passed\":true,\"wpfVector\":true,\"providerReads\":0}");
    }
}
