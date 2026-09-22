using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AgentQuotaMonitor;

namespace LifecycleTests;

internal static class Program
{
    private static int checks;
    private static readonly string[] Cases = { "late-status", "late-preferences", "late-startup", "snapshots", "owned-quit", "external-quit", "cleanup-failure", "instances" };
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            foreach (string name in Cases)
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                start.ArgumentList.Add(name);
                using var process = Process.Start(start)!;
                Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(20000)) { process.Kill(); throw new TimeoutException("Lifecycle case timed out: " + name); }
                Console.Write(output.GetAwaiter().GetResult());
                if (process.ExitCode != 0) { Console.Error.Write(error.GetAwaiter().GetResult()); return 1; }
            }
            Console.WriteLine("All lifecycle cases passed with synthetic HTTP and isolated instance names.");
            return 0;
        }
        if (args[0] == "instances")
        {
            try { TestInstances(); Console.WriteLine($"instances passed {checks} checks"); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        using var handler = new BackendFixture();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1"), Timeout = TimeSpan.FromSeconds(10) };
        var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken startupToken = default;
        int stops = 0, exits = 0;
        Func<CancellationToken, Task> ensure = args[0] == "late-startup"
            ? token => { startupToken = token; return starting.Task; } : _ => Task.CompletedTask;
        Func<CancellationToken, Task> stop = _ => { ++stops; if (args[0] == "cleanup-failure") throw new InvalidOperationException("Synthetic stop failure"); return Task.CompletedTask; };
        BackendConnection? connection = null;
        var processFixture = new ProcessFixture();
        if (args[0] is "owned-quit" or "external-quit")
        {
            handler.RefuseFirst = args[0] == "owned-quit";
            handler.OnShutdown = () => processFixture.Exited = true;
            connection = new BackendConnection(http, () => processFixture, retryDelay: TimeSpan.FromMilliseconds(1));
            ensure = connection.EnsureAsync;
            stop = connection.StopOwnedAsync;
        }
        var app = new App(http, ensure, stop, () => ++exits);
        int result = 1;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            var main = new MainWindow(_ => {}, _ => {}, () => {}, () => {}, () => {}, () => {})
                { Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
            int hides = 0;
            var floating = new FloatingWindow(() => {}, () => { ++hides; Invoke(app, "HideFloating"); })
                { Left = -32000, Top = -32000, ShowActivated = false };
            var tray = new TrayController(() => {}, () => {}, () => {}, () => {});
            Set(app, "main", main); Set(app, "floating", floating); Set(app, "tray", tray);
            Set(app, "preferencesLoaded", args[0] != "late-preferences");
            var cached = new List<QuotaItem> { new() { Key = QuotaItem.MakeKey("kept", "g", "b"), AccountId = "kept", GroupId = "g", BucketId = "b", Status = "live", Remaining = 77, Code = "CL" } };
            Set(app, "items", cached);
            Set(app, "selected", cached[0].Key);
            Field<HashSet<string>>(app, "pins").Add(cached[0].Key);
            try
            {
                switch (args[0])
                {
                    case "late-status":
                    case "late-preferences":
                    {
                        var delayed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                        handler.DelayPath = args[0] == "late-status" ? "/api/status" : "/api/desktop";
                        handler.Delayed = delayed.Task;
                        Task poll = Call(app, "Poll");
                        await Until(() => handler.DelayStarted);
                        await Call(app, "Quit").WaitAsync(TimeSpan.FromSeconds(6));
                        Check(handler.DelayedToken.IsCancellationRequested, "Quit cancels an in-flight native request");
                        delayed.SetResult(BackendFixture.Json(args[0] == "late-status" ? handler.Status : "{\"desktopFloating\":true,\"desktopFloatingSelections\":[]}"));
                        await poll.WaitAsync(TimeSpan.FromSeconds(2));
                        Check(ReferenceEquals(Field<List<QuotaItem>>(app, "items"), cached) && cached[0].Status == "live", "Late responses do not replace or modify the cached snapshot after Quit");
                        Check(Field<HashSet<string>>(app, "pins").SetEquals(new[] { cached[0].Key }), "Late preferences do not change saved pins");
                        Check(Field<bool>(tray, "_disposed") && exits == 1 && stops == 1, "Quit disposes the tray and completes once");
                        Check(hides == 0 && handler.Posts.Count == 0, "Shutdown never saves the explicit floating Hide action");
                        break;
                    }
                    case "late-startup":
                    {
                        Task startup = Call(app, "StartServices");
                        await Call(app, "Quit").WaitAsync(TimeSpan.FromSeconds(6));
                        Check(startupToken.IsCancellationRequested, "Quit cancels backend startup");
                        starting.SetResult();
                        await startup.WaitAsync(TimeSpan.FromSeconds(2));
                        await Call(app, "StartServices");
                        Check(handler.Requests == 0 && Field<object?>(app, "timer") is null && Field<object?>(app, "updateTimer") is null && Field<object?>(app, "updates") is null,
                            "A startup continuation cannot create polls or update timers after Quit");
                        Check(exits == 1 && stops == 1 && hides == 0, "Startup shutdown runs cleanup once without hiding preferences");
                        break;
                    }
                    case "cleanup-failure":
                    {
                        System.ComponentModel.CancelEventHandler brokenClose = (_, _) => throw new InvalidOperationException("Synthetic window close failure");
                        main.Closing += brokenClose;
                        await Call(app, "Quit");
                        main.Closing -= brokenClose;
                        await Call(app, "Quit");
                        Check(stops == 1 && exits == 1 && Field<bool>(tray, "_disposed"), "Stop and window cleanup failures cannot prevent final exit or cause duplicate shutdown");
                        Check(hides == 0 && handler.Posts.Count == 0, "Failed cleanup does not persist floating Hide");
                        break;
                    }
                    case "snapshots":
                    {
                        await Call(app, "Poll");
                        List<QuotaItem> good = Field<List<QuotaItem>>(app, "items");
                        string valid = handler.Status;
                        var accountDuplicate = JsonNode.Parse(valid)!;
                        accountDuplicate["accounts"]!.AsArray().Add(accountDuplicate["accounts"]![0]!.DeepClone());
                        var bucketDuplicate = JsonNode.Parse(valid)!;
                        var buckets = bucketDuplicate["accounts"]![0]!["groups"]![0]!["buckets"]!.AsArray();
                        buckets.Add(buckets[0]!.DeepClone());
                        var groupDuplicate = JsonNode.Parse(valid)!;
                        var groups = groupDuplicate["accounts"]![0]!["groups"]!.AsArray();
                        groups.Add(groups[0]!.DeepClone());
                        var missingId = JsonNode.Parse(valid)!;
                        missingId["accounts"]![0]!["id"] = "";
                        foreach (string malformed in new[] { "{}", "{\"accounts\":null}", "{\"accounts\":[{\"id\":\"a\"}]}", accountDuplicate.ToJsonString(), groupDuplicate.ToJsonString(), bucketDuplicate.ToJsonString(), missingId.ToJsonString() })
                        {
                            handler.Status = malformed;
                            await Call(app, "Poll");
                            Check(ReferenceEquals(Field<List<QuotaItem>>(app, "items"), good) && good.All(item => item.Stale), "Malformed or duplicate identities retain the last successful values as stale");
                        }
                        Check(Field<HashSet<string>>(app, "pins").SetEquals(new[] { cached[0].Key }) && Field<string>(app, "selected") == cached[0].Key,
                            "Snapshot failures preserve missing stable selections and pins");
                        handler.Status = valid;
                        await Call(app, "Poll");
                        Check(!ReferenceEquals(Field<List<QuotaItem>>(app, "items"), good) && Field<List<QuotaItem>>(app, "items").All(item => !item.Stale), "A valid later poll recovers normally");
                        floating.Show();
                        floating.Close();
                        Check(hides == 1 && handler.Posts.Count(path => path == "/api/desktop") == 1, "Explicit close still requests Hide and saves the preference");
                        await Call(app, "Quit");
                        Check(hides == 1, "Quit does not repeat the Hide callback");
                        break;
                    }
                    case "owned-quit":
                    case "external-quit":
                    {
                        await Call(app, "StartServices");
                        floating.Show();
                        await Call(app, "Quit");
                        bool owned = args[0] == "owned-quit";
                        Check(handler.Posts.Count(path => path == "/api/shutdown") == (owned ? 1 : 0), "Only an owned verified backend receives shutdown");
                        Check(hides == 0 && !handler.Posts.Contains("/api/desktop"), "Both owned and external backend shutdown preserve floating preferences");
                        Check(exits == 1 && (!owned || processFixture.Disposals == 1), "Backend ownership cleanup completes once");
                        break;
                    }
                }
                Console.WriteLine($"{args[0]} passed {checks} checks");
                result = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { connection?.Dispose(); app.Shutdown(); }
        }));
        app.Run();
        return result;
    }

    private static void TestInstances()
    {
        string suffix = Guid.NewGuid().ToString("N"), mutexName = "Local\\QuotaLifecycleMutex" + suffix, eventName = "Local\\QuotaLifecycleEvent" + suffix;
        using var ready = new ManualResetEventSlim(); using var activated = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        Exception? failure = null;
        var primary = new Thread(() =>
        {
            try
            {
                using var instance = new SingleInstance(mutexName, eventName);
                if (!instance.IsPrimary) throw new Exception("First instance was not primary");
                ready.Set();
                if (instance.Activation.WaitOne(5000)) activated.Set();
                release.Wait(5000);
            }
            catch (Exception error) { failure = error; ready.Set(); }
        });
        primary.Start();
        Check(ready.Wait(3000) && failure is null, "First instance acquires ownership");
        using (var secondary = new SingleInstance(mutexName, eventName))
        {
            Check(!secondary.IsPrimary && activated.Wait(1000), "Second instance signals the already-published activation event");
        }
        release.Set(); Check(primary.Join(3000) && failure is null, "Primary ownership is released cleanly");
        using (var replacement = new SingleInstance(mutexName, eventName)) Check(replacement.IsPrimary, "A later instance takes over after the primary exits");
        using var exitReady = new ManualResetEventSlim();
        var exitingPrimary = new Thread(() =>
        {
            using var instance = new SingleInstance(mutexName + "Exit", eventName + "Exit");
            exitReady.Set();
            instance.Activation.WaitOne(3000);
        });
        exitingPrimary.Start(); Check(exitReady.Wait(3000), "The exiting primary publishes its activation handle");
        using (var secondary = new SingleInstance(mutexName + "Exit", eventName + "Exit"))
            Check(secondary.IsPrimary, "A primary disappearing during activation transfers ownership safely");
        Check(exitingPrimary.Join(3000), "The activated exiting primary finishes");
        string abandonedName = mutexName + "Abandoned";
        Mutex? abandoned = null;
        var crashingPrimary = new Thread(() => { abandoned = new Mutex(true, abandonedName); });
        crashingPrimary.Start(); Check(crashingPrimary.Join(3000), "Synthetic primary exits while owning its isolated mutex");
        using (var replacement = new SingleInstance(abandonedName, eventName + "Abandoned")) Check(replacement.IsPrimary, "An abandoned primary is recovered without an activation exception");
        abandoned?.Dispose();
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); ++checks; }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static Task Call(object target, string name) => (Task)Invoke(target, name)!;
}

internal sealed class BackendFixture : HttpMessageHandler
{
    internal string Status = "{\"backend\":{\"name\":\"agent-quota-monitor\",\"protocolVersion\":1,\"processId\":876},\"now\":1,\"accounts\":[{\"id\":\"account\",\"provider\":\"claude\",\"label\":\"Synthetic\",\"status\":\"live\",\"groups\":[{\"id\":\"group\",\"label\":\"Synthetic\",\"buckets\":[{\"id\":\"bucket\",\"label\":\"Synthetic\",\"remaining\":55}]}]}]}";
    internal string? DelayPath;
    internal Task<HttpResponseMessage>? Delayed;
    internal bool DelayStarted, RefuseFirst;
    internal CancellationToken DelayedToken;
    internal int Requests;
    internal List<string> Posts = new();
    internal Action? OnShutdown;
    internal static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        ++Requests;
        string path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post)
        {
            Posts.Add(path); if (path == "/api/shutdown") OnShutdown?.Invoke(); return Json("{}");
        }
        if (RefuseFirst) { RefuseFirst = false; throw new HttpRequestException("Synthetic connection refusal", new SocketException((int)SocketError.ConnectionRefused)); }
        if (path == DelayPath) { DelayStarted = true; DelayedToken = token; return await Delayed!; }
        return Json(path == "/api/status" ? Status : "{\"desktopFloatingSelections\":[]}");
    }
}
internal sealed class ProcessFixture : IBackendProcess
{
    internal bool Exited;
    internal int Disposals;
    public int Id => 876;
    public bool HasExited => Exited;
    public Task WaitForExitAsync(CancellationToken token) => Task.CompletedTask;
    public void Kill() => Exited = true;
    public void Dispose() => ++Disposals;
}
