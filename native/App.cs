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
    private bool stopping; private bool polling; private bool preferencesLoaded; private bool backendReady;
    private SingleInstance? instance; private DispatcherTimer? timer;
    private readonly CancellationTokenSource lifetime = new();
    private readonly BackendConnection? backendConnection;
    private readonly Func<CancellationToken, Task> ensureBackend;
    private readonly Func<CancellationToken, Task> stopBackend;
    private readonly Action? exitOverride;
    private Task startupTask = Task.CompletedTask, pollTask = Task.CompletedTask, activationTask = Task.CompletedTask;
    private Task? quitTask;
    private double opacity = 0.85;
    private DispatcherTimer? placementSave;
    private UpdateService? updates;
    private DispatcherTimer? updateTimer;
    private bool offlineCheck;
    public App()
    {
        backendConnection = new BackendConnection(http, LaunchBackend);
        ensureBackend = backendConnection.EnsureAsync;
        stopBackend = backendConnection.StopOwnedAsync;
    }
    internal App(HttpClient client, Func<CancellationToken, Task> ensure, Func<CancellationToken, Task> stop, Action exited)
    {
        http.Dispose();
        http = client;
        ensureBackend = ensure;
        stopBackend = stop;
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
        main = new MainWindow(SelectTray, TogglePin, ToggleFloating, () => _ = Post("/api/refresh", new {}), () => _ = Quit(), ShowAccounts);
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
        if (!Environment.GetCommandLineArgs().Contains("--minimized")) main.Show();
        await StartServices();
    }
    private Task StartServices() => startupTask = StartServicesCore();
    private async Task StartServicesCore()
    {
        if (stopping) return;
        bool connected = false;
        try
        {
            await ensureBackend(lifetime.Token);
            if (stopping) return;
            backendReady = true;
            await Poll();
            connected = true;
        }
        catch (OperationCanceledException) when (stopping) { }
        catch (Exception error)
        {
            if (!stopping && main is not null)
            {
                main.Title = "Agent Quota Monitor · backend unavailable";
                main.ShowBackendError(error is BackendConnectionException ? error.Message : "The quota backend could not be started. Close conflicting local services and restart the monitor.");
            }
        }
        if (stopping) return;
        updates = new UpdateService();
        updates.Changed += () => { if (!stopping) main?.SetUpdate(updates); };
        updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        updateTimer.Tick += async (_, _) =>
        {
            if (stopping) return;
            updateTimer.Interval = TimeSpan.FromHours(1);
            await updates.Check();
        };
        updateTimer.Start();
        if (!connected) return;
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += async (_, _) => await Poll();
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
        if (polling || stopping) return Task.CompletedTask;
        return pollTask = PollCore();
    }
    private async Task PollCore()
    {
        polling = true;
        try
        {
            using var data = JsonDocument.Parse(await http.GetStringAsync("/api/status", lifetime.Token));
            if (stopping) return;
            backendConnection?.ValidatePeer(data.RootElement);
            List<QuotaItem> candidate = QuotaSnapshot.Parse(data.RootElement);
            if (!preferencesLoaded)
            {
                using var prefs = JsonDocument.Parse(await http.GetStringAsync("/api/desktop", lifetime.Token));
                if (stopping) return;
                var p = prefs.RootElement;
                if (p.TryGetProperty("desktopSelection", out var s)) selected = ReadKey(s);
                if (p.TryGetProperty("desktopFloatingSelections", out var list) && list.ValueKind == JsonValueKind.Array)
                { foreach (var pin in list.EnumerateArray()) { var key = ReadKey(pin); if (key != null) pins.Add(key); } }
                else if (selected != null) pins.Add(selected);
                opacity = Math.Clamp((QuotaItem.Number(p, "desktopOpacity") ?? 85) / 100, .35, 1);
                if (QuotaItem.Number(p, "wpfFloatingLeft") is double x) floating!.Left = x;
                if (QuotaItem.Number(p, "wpfFloatingTop") is double y) floating!.Top = y;
                floating!.SetScale((QuotaItem.Number(p, "desktopFloatingScale") ?? 100) / 100);
                preferencesLoaded = true;
                if (p.TryGetProperty("desktopFloating", out var f) && f.ValueKind == JsonValueKind.True) { floating!.Opacity = opacity; floating.Show(); }
            }
            if (stopping) return;
            items = candidate;
            backendReady = true;
            main?.ShowBackendError("");
            selected ??= items.FirstOrDefault()?.Key;
            if (Render() && main is not null) main.Title = "Agent Quota Monitor Windows";
        }
        catch (OperationCanceledException) when (stopping) { }
        catch (BackendConnectionException error)
        {
            if (stopping) return;
            backendReady = false;
            main?.ShowBackendError(error.Message);
            foreach (var item in items) item.MarkDisconnected();
            Render();
        }
        catch
        {
            if (stopping) return;
            bool wasReady = backendReady;
            backendReady = false;
            if (wasReady) main?.ShowBackendError("The quota reader is unavailable or returned invalid data. Cached values are shown while reconnecting.");
            if (main is not null) main.Title = "Agent Quota Monitor · disconnected";
            foreach (var item in items) item.MarkDisconnected();
            Render();
        }
        finally { polling = false; }
    }
    private static string? ReadKey(JsonElement e) => e.ValueKind != JsonValueKind.Object ? null :
        QuotaItem.MakeKey(QuotaItem.Text(e, "accountId"), QuotaItem.Text(e, "groupId"), QuotaItem.Text(e, "bucketId"));
    private bool Render()
    {
        if (stopping) return false;
        try { main?.SetData(items, selected, pins); floating?.SetData(items.Where(q => pins.Contains(q.Key)).ToList()); tray?.SetQuota(items.FirstOrDefault(q => q.Key == selected)); return true; }
        catch { if (!stopping && main is not null) main.Title = "Agent Quota Monitor · display unavailable"; return false; }
    }
    private void SelectTray(QuotaItem item) { if (stopping) return; selected = item.Key; Render(); _ = Post("/api/desktop", new { desktopSelection = item.Selection }); }
    private void TogglePin(QuotaItem item)
    {
        if (stopping) return;
        if (!pins.Add(item.Key)) pins.Remove(item.Key); Render();
        var values = pins.Select(key => { var ids = JsonSerializer.Deserialize<string[]>(key)!; return new { accountId = ids[0], groupId = ids[1], bucketId = ids[2] }; }).ToArray();
        _ = Post("/api/desktop", new { desktopFloatingSelections = values });
    }
    private void ShowMain() { if (stopping) return; main?.Show(); if (main != null) { main.WindowState = WindowState.Normal; main.Activate(); } }
    private AccountsWindow? accounts;
    private void ShowAccounts()
    {
        if (stopping) return;
        ShowMain();
        if (accounts != null) { accounts.Activate(); return; }
        accounts = new AccountsWindow(main!, http, () => _ = Poll(), updates, () => backendReady && !stopping);
        accounts.Closed += (_, _) => accounts = null; accounts.Show();
    }
    private void ToggleFloating() { if (stopping) return; if (floating!.IsVisible) HideFloating(); else { floating.Opacity = opacity; floating.Show(); _ = Post("/api/desktop", new { desktopFloating = true }); } }
    private void HideFloating() { if (stopping) return; floating?.Hide(); _ = Post("/api/desktop", new { desktopFloating = false }); }
    private async Task Post(string path, object value)
    {
        if (stopping || !backendReady) return;
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
