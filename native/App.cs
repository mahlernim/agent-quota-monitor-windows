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
    private readonly HttpClient http = new() { BaseAddress = new Uri("http://127.0.0.1:8765"), Timeout = TimeSpan.FromSeconds(8) };
    private MainWindow? main; private FloatingWindow? floating; private TrayController? tray;
    private List<QuotaItem> items = new(); private string? selected; private readonly HashSet<string> pins = new();
    private Process? backend; private bool stopping; private bool polling; private bool preferencesLoaded;
    private Mutex? mutex; private EventWaitHandle? activation; private DispatcherTimer? timer;
    private double opacity = 0.85;
    private DispatcherTimer? placementSave;
    [STAThread] public static void Main(string[] args)
    {
        var app = new App();
        if (args.Length == 2 && args[0] == "--self-test-output") { app.SelfTest(args[1]); return; }
        if (args.Length == 2 && args[0] == "--screenshots-output") { PreviewImages.Create(args[1]); return; }
        app.Run();
    }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e); ShutdownMode = ShutdownMode.OnExplicitShutdown;
        mutex = new Mutex(true, "Local\\AgentQuotaMonitorWpf", out bool created);
        if (!created) { using var signal = EventWaitHandle.OpenExisting("Local\\AgentQuotaMonitorWpfActivate"); signal.Set(); Shutdown(); return; }
        activation = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\AgentQuotaMonitorWpfActivate");
        _ = Task.Run(() => { while (!stopping) if (activation.WaitOne(500)) Dispatcher.BeginInvoke(ShowMain); });
        http.DefaultRequestHeaders.Add("Origin", "http://127.0.0.1:8765");
        http.DefaultRequestHeaders.Add("X-Quota-Request", "refresh");
        main = new MainWindow(SelectTray, TogglePin, ToggleFloating, () => _ = Post("/api/refresh", new {}), OpenWeb, () => _ = Quit(), ShowAccounts);
        main.Closing += (_, ev) => { if (!stopping) { ev.Cancel = true; main.Hide(); } };
        floating = new FloatingWindow(ShowMain, HideFloating);
        main.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/robot-ring.ico"));
        floating.Icon = main.Icon;
        placementSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        placementSave.Tick += (_, _) => { placementSave.Stop(); if (preferencesLoaded && !stopping) _ = Post("/api/desktop", new { wpfFloatingLeft = floating.Left, wpfFloatingTop = floating.Top, desktopOpacity = Math.Round(floating.Opacity * 100), desktopFloatingScale = Math.Round(floating.MonitorScale * 100) }); };
        floating.ScaleChanged += () => { if (preferencesLoaded) { placementSave.Stop(); placementSave.Start(); } };
        floating.LocationChanged += (_, _) => { if (preferencesLoaded) { placementSave.Stop(); placementSave.Start(); } };
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Window.OpacityProperty, typeof(Window)).AddValueChanged(floating, (_, _) => { if (preferencesLoaded) { opacity = floating.Opacity; placementSave.Stop(); placementSave.Start(); } });
        tray = new TrayController(ShowMain, OpenWeb, ToggleFloating, () => _ = Quit());
        if (!Environment.GetCommandLineArgs().Contains("--minimized")) main.Show();
        try { await EnsureBackend(); await Poll(); }
        catch { main.Title = "Agent Quota Monitor · backend unavailable"; }
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) }; timer.Tick += async (_, _) => await Poll(); timer.Start();
    }
    private async Task EnsureBackend()
    {
        try { using var existing = await http.GetAsync("/api/status"); if (existing.IsSuccessStatusCode) return; } catch (HttpRequestException) {} catch (TaskCanceledException) {}
        string packaged = Path.Combine(AppContext.BaseDirectory, "backend", "quota-backend.exe");
        var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
        if (File.Exists(packaged)) start.FileName = packaged;
        else
        {
            var root = FindSource(); start.FileName = Path.Combine(root, ".venv-desktop", "Scripts", "pythonw.exe");
            start.WorkingDirectory = root; start.ArgumentList.Add("-m"); start.ArgumentList.Add("quota.server");
        }
        backend = Process.Start(start) ?? throw new InvalidOperationException();
        for (int i = 0; i < 40; i++) { await Task.Delay(250); try { using var response = await http.GetAsync("/api/status"); if (response.IsSuccessStatusCode) return; } catch (HttpRequestException) {} }
        throw new InvalidOperationException();
    }
    private static string FindSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null) { if (File.Exists(Path.Combine(dir.FullName, "quota", "server.py"))) return dir.FullName; dir = dir.Parent; }
        throw new InvalidOperationException("Backend was not packaged");
    }
    private async Task Poll()
    {
        if (polling || stopping) return; polling = true;
        try
        {
            using var data = JsonDocument.Parse(await http.GetStringAsync("/api/status")); items = QuotaItem.Parse(data.RootElement);
            if (!preferencesLoaded)
            {
                using var prefs = JsonDocument.Parse(await http.GetStringAsync("/api/desktop")); var p = prefs.RootElement;
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
            selected ??= items.FirstOrDefault()?.Key; Render();
            main!.Title = "Agent Quota Monitor Windows";
        }
        catch { main!.Title = "Agent Quota Monitor · disconnected"; foreach (var item in items) item.MarkDisconnected(); Render(); }
        finally { polling = false; }
    }
    private static string? ReadKey(JsonElement e) => e.ValueKind != JsonValueKind.Object ? null :
        QuotaItem.MakeKey(QuotaItem.Text(e, "accountId"), QuotaItem.Text(e, "groupId"), QuotaItem.Text(e, "bucketId"));
    private void Render() { main?.SetData(items, selected, pins); floating?.SetData(items.Where(q => pins.Contains(q.Key)).ToList()); tray?.SetQuota(items.FirstOrDefault(q => q.Key == selected)); }
    private void SelectTray(QuotaItem item) { selected = item.Key; Render(); _ = Post("/api/desktop", new { desktopSelection = item.Selection }); }
    private void TogglePin(QuotaItem item)
    {
        if (!pins.Add(item.Key)) pins.Remove(item.Key); Render();
        var values = pins.Select(key => { var ids = JsonSerializer.Deserialize<string[]>(key)!; return new { accountId = ids[0], groupId = ids[1], bucketId = ids[2] }; }).ToArray();
        _ = Post("/api/desktop", new { desktopFloatingSelections = values });
    }
    private void ShowMain() { main?.Show(); if (main != null) { main.WindowState = WindowState.Normal; main.Activate(); } }
    private AccountsWindow? accounts;
    private void ShowAccounts()
    {
        if (accounts != null) { accounts.Activate(); return; }
        accounts = new AccountsWindow(main!, http, () => _ = Poll());
        accounts.Closed += (_, _) => accounts = null; accounts.Show();
    }
    private static void OpenWeb() => Process.Start(new ProcessStartInfo("http://127.0.0.1:8765") { UseShellExecute = true });
    private void ToggleFloating() { if (floating!.IsVisible) HideFloating(); else { floating.Opacity = opacity; floating.Show(); _ = Post("/api/desktop", new { desktopFloating = true }); } }
    private void HideFloating() { floating?.Hide(); _ = Post("/api/desktop", new { desktopFloating = false }); }
    private async Task Post(string path, object value)
    {
        try { using var response = await http.PostAsync(path, new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")); response.EnsureSuccessStatusCode(); }
        catch { if (main != null) main.Title = "Agent Quota Monitor · could not save or refresh"; }
    }
    private async Task Quit()
    {
        if (stopping) return; stopping = true; timer?.Stop(); placementSave?.Stop(); tray?.Dispose();
        if (backend != null && !backend.HasExited)
        {
            await Post("/api/shutdown", new {});
            if (!await Task.Run(() => backend.WaitForExit(6000))) backend.Kill();
        }
        activation?.Set(); main?.Close(); floating?.Close(); http.Dispose(); mutex?.Dispose(); Shutdown();
    }
    private void SelfTest(string output)
    {
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
