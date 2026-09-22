using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AgentQuotaMonitor;

namespace StartupTests;

internal static class Program
{
    private static int checks;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Supply the isolated test configuration path.");
        var configuration = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(args[0]))!;
        string profile = Path.GetFullPath(configuration.Profile);
        if (!Directory.Exists(profile) || !File.Exists(Path.Combine(profile, "local", "QuotaDashboard", "settings.dpapi")))
            throw new InvalidOperationException("The isolated encrypted settings must already exist.");
        if (configuration.Port < 1024 || configuration.Port > 65535 || configuration.Port == 8765)
            throw new InvalidOperationException("Use a random non-production port.");
        // Fail before any HTTP mutation if the wrapper's selected port was taken.
        var reservation = new TcpListener(IPAddress.Loopback, configuration.Port);
        reservation.Server.ExclusiveAddressUse = true;
        reservation.Start();
        reservation.Stop();

        var origin = new Uri($"http://127.0.0.1:{configuration.Port}");
        using var transport = new RecordingTransport();
        using var http = new HttpClient(transport) { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.Add("Origin", origin.GetLeftPart(UriPartial.Authority));
        http.DefaultRequestHeaders.Add("X-Quota-Request", "refresh");
        int launches = 0, exits = 0, hides = 0;
        Process? observer = null;
        int? childPid = null, childExitCode = null;
        bool childExited = false, ready = false, preferencesLoaded = false;
        var connection = new BackendConnection(http, () =>
        {
            ++launches;
            if (launches != 1) throw new InvalidOperationException("The startup path launched more than one backend.");
            var start = new ProcessStartInfo
            {
                FileName = configuration.Backend ?? configuration.Python,
                WorkingDirectory = configuration.Backend is null ? configuration.SourceRoot : profile,
                UseShellExecute = false, CreateNoWindow = true
            };
            start.Environment.Clear();
            foreach (string name in new[] { "SystemRoot", "WINDIR", "COMSPEC" })
                if (Environment.GetEnvironmentVariable(name) is string value) start.Environment[name] = value;
            start.Environment["LOCALAPPDATA"] = Path.Combine(profile, "local");
            start.Environment["APPDATA"] = Path.Combine(profile, "roaming");
            start.Environment["USERPROFILE"] = profile;
            start.Environment["TEMP"] = start.Environment["TMP"] = Path.Combine(profile, "tmp");
            start.Environment["PATH"] = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "System32");
            if (configuration.Backend is null)
                foreach (string argument in new[] { "-E", "-s", "-m", "quota.server" }) start.ArgumentList.Add(argument);
            start.ArgumentList.Add("--port"); start.ArgumentList.Add(configuration.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var process = Process.Start(start) ?? throw new InvalidOperationException("The test backend failed to start.");
            childPid = process.Id;
            // The connector disposes its Process. A separate handle verifies exit.
            observer = Process.GetProcessById(process.Id);
            _ = observer.Handle;
            return process;
        });
        var app = new App(http, connection.EnsureAsync, connection.StopOwnedAsync, () => ++exits, connection);
        int result = 1;
        string? failure = null;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            var main = new MainWindow(_ => {}, _ => {}, () => {}, () => {}, () => {}, () => {})
                { Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
            var floating = new FloatingWindow(() => {}, () => { ++hides; Invoke(app, "HideFloating"); })
                { Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
            Set(app, "main", main); Set(app, "floating", floating);
            try
            {
                // This executes the production initial socket probe, launch,
                // readiness loop, App status parse, and preference load.
                await Call(app, "StartServices").WaitAsync(TimeSpan.FromSeconds(25));
                ready = Field<bool>(app, "backendReady");
                preferencesLoaded = Field<bool>(app, "preferencesLoaded");
                Check(launches == 1, "A real initially vacant Windows port must launch exactly one backend.");
                Check(ready && preferencesLoaded, "The real App startup path must load status and preferences.");
                Check(connection.OwnsBackend, "The launched backend must be owned by the connector.");
                Check(Field<List<QuotaItem>>(app, "items").Count == 0, "The isolated disabled providers must supply no quota cards.");
                Check(Field<HashSet<string>>(app, "pins").Count == 0, "Explicitly empty saved pins must stay empty.");
                Check(floating.IsVisible && Math.Abs(floating.Opacity - .70) < .001 && Math.Abs(floating.MonitorScale - 1.25) < .001,
                    "Saved floating visibility, opacity, and scale must load into the real window.");
                Check(transport.StatusRequests >= 2 && transport.PreferenceRequests >= 1, "Readiness and App preference loading must use real HTTP.");
                using (var status = JsonDocument.Parse(await http.GetStringAsync("/api/status")))
                {
                    connection.ValidatePeer(status.RootElement);
                    Check(status.RootElement.GetProperty("backend").GetProperty("processId").GetInt32() == childPid,
                        "The accepted peer must be the process launched by this harness.");
                    Check(status.RootElement.GetProperty("enabledProviders").GetArrayLength() == 0,
                        "All providers must remain disabled.");
                }
                await Call(app, "Quit").WaitAsync(TimeSpan.FromSeconds(12));
                Check(exits == 1 && hides == 0 && transport.PreferencePosts == 0, "Quit must finish once without persisting floating Hide.");
                Check(transport.ShutdownRequests == 1 && transport.ShutdownStatus == HttpStatusCode.Accepted,
                    "Owned shutdown must succeed through the real process-bound API.");
                Check(observer is not null && observer.WaitForExit(2000), "No owned backend process may remain after Quit.");
                childExited = observer!.HasExited; childExitCode = observer.ExitCode;
                Check(childExitCode == 0 && !connection.CleanupFailed, "The backend must exit cleanly without forced cleanup.");
                Check(!connection.OwnsBackend, "The connector must release backend ownership.");
                result = 0;
            }
            catch (Exception error) { failure = error.ToString(); Console.Error.WriteLine(error); }
            finally
            {
                try { await Call(app, "Quit").WaitAsync(TimeSpan.FromSeconds(12)); }
                catch (Exception error) { failure ??= error.ToString(); }
                connection.Dispose();
                if (observer is not null)
                {
                    try
                    {
                        if (!observer.HasExited) { observer.Kill(); observer.WaitForExit(5000); }
                        childExited = observer.HasExited;
                        if (childExited) childExitCode = observer.ExitCode;
                    }
                    finally { observer.Dispose(); }
                }
                File.WriteAllText(configuration.Report, JsonSerializer.Serialize(new
                {
                    passed = result == 0, checks, launches, exits, hides, childPid, childExited, childExitCode,
                    ready, preferencesLoaded, port = configuration.Port,
                    statusRequests = transport.StatusRequests, preferenceRequests = transport.PreferenceRequests,
                    preferencePosts = transport.PreferencePosts, shutdownRequests = transport.ShutdownRequests,
                    shutdownStatus = (int?)transport.ShutdownStatus, failure
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown();
            }
        }));
        app.Run();
        if (result == 0) Console.WriteLine($"Real Windows socket startup passed {checks} checks with one isolated backend and clean shutdown.");
        return result;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); ++checks; }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Invoke(object target, string name) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
    private static Task Call(object target, string name) => (Task)Invoke(target, name)!;

    private sealed record Configuration(string Profile, string SourceRoot, string Python, string? Backend, int Port, string Report);
}

// Count requests while retaining the real Windows HTTP and socket transport.
internal sealed class RecordingTransport : DelegatingHandler
{
    internal int StatusRequests, PreferenceRequests, PreferencePosts, ShutdownRequests;
    internal HttpStatusCode? ShutdownStatus;
    internal RecordingTransport() : base(new HttpClientHandler { AllowAutoRedirect = false }) { }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        string path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get && path == "/api/status") ++StatusRequests;
        if (path == "/api/desktop") { if (request.Method == HttpMethod.Get) ++PreferenceRequests; else ++PreferencePosts; }
        bool shutdown = path == "/api/shutdown" && request.Method == HttpMethod.Post;
        if (shutdown) ++ShutdownRequests;
        var response = await base.SendAsync(request, token);
        if (shutdown) ShutdownStatus = response.StatusCode;
        return response;
    }
}
