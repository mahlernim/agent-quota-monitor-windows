using AgentQuotaMonitor;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); }
async Task Expect<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
async Task Test(string name, Func<Task> action)
{
    await action();
    passed++;
    Console.WriteLine("PASS " + name);
}
string Status(int pid = 77) => "{\"backend\":{\"name\":\"agent-quota-monitor\",\"protocolVersion\":1,\"processId\":" + pid + "},\"now\":1,\"accounts\":[]}";
HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
HttpRequestException Refused() => new("Connection refused", new SocketException((int)SocketError.ConnectionRefused));
BackendConnection Connection(HttpClient http, Func<IBackendProcess> launch, int limit = 180) =>
    new(http, launch, TimeSpan.FromMilliseconds(limit), TimeSpan.FromMilliseconds(45), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(100));

await Test("existing compatible reader is attached and never stopped", async () =>
{
    var launches = 0;
    using var http = Client((r, ct) => Task.FromResult(Json(Status())));
    using var connection = Connection(http, () => { launches++; return new FakeProcess(); });
    await connection.EnsureAsync(CancellationToken.None);
    await connection.StopOwnedAsync(CancellationToken.None);
    Check(launches == 0 && !connection.OwnsBackend, "Existing ownership");
});

await Test("later peer identity is checked separately from quota schema", async () =>
{
    using var http = Client((r, ct) => Task.FromResult(Json(Status())));
    using var connection = Connection(http, () => new FakeProcess());
    await connection.EnsureAsync(CancellationToken.None);
    using var malformedQuota = System.Text.Json.JsonDocument.Parse(Status().Replace("\"accounts\":[]", "\"accounts\":{}"));
    connection.ValidatePeer(malformedQuota.RootElement);
    foreach (var changed in new[] { "{}", Status(88), Status().Replace("protocolVersion\":1", "protocolVersion\":2") })
    {
        using var peer = System.Text.Json.JsonDocument.Parse(changed);
        await Expect<BackendConnectionException>(() => { connection.ValidatePeer(peer.RootElement); return Task.CompletedTask; });
    }
    using var recovered = System.Text.Json.JsonDocument.Parse(Status());
    connection.ValidatePeer(recovered.RootElement);
});

foreach (var invalid in new[]
{
    "{}", Status().Replace("agent-quota-monitor", "unrelated"), Status().Replace("protocolVersion\":1", "protocolVersion\":2"),
    Status().Replace("\"accounts\":[]", "\"accounts\":{}"), Status().Replace("\"now\":1", "\"now\":\"1\""),
    Status(-1), Status().Replace("\"accounts\":[]", "\"accounts\":[{\"id\":\"a\",\"provider\":\"codex\"}]"),
    Status().Replace("\"accounts\":[]", "\"accounts\":[{\"id\":\"a\",\"provider\":\"codex\",\"groups\":[]},{\"id\":\"a\",\"provider\":\"codex\",\"groups\":[]}]")
})
{
    await Test("invalid peer is rejected without starting another reader " + passed, async () =>
    {
        var launches = 0;
        using var http = Client((r, ct) => Task.FromResult(Json(invalid)));
        using var connection = Connection(http, () => { launches++; return new FakeProcess(); });
        await Expect<BackendConnectionException>(() => connection.EnsureAsync(CancellationToken.None));
        Check(launches == 0, "Unexpected launch for conflict");
    });
}

await Test("non-success listener is not treated as an unused port", async () =>
{
    var launches = 0;
    using var http = Client((r, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
    using var connection = Connection(http, () => { launches++; return new FakeProcess(); });
    await Expect<BackendConnectionException>(() => connection.EnsureAsync(CancellationToken.None));
    Check(launches == 0, "Non-success listener launch");
});

await Test("initial timeout does not create a second writer", async () =>
{
    var launches = 0;
    using var http = Client(async (r, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json(Status()); });
    using var connection = Connection(http, () => { launches++; return new FakeProcess(); });
    await Expect<BackendConnectionException>(() => connection.EnsureAsync(CancellationToken.None));
    Check(launches == 0, "Timed-out listener launch");
});

await Test("refused connection launches and verifies child PID", async () =>
{
    var process = new FakeProcess(); var calls = 0;
    using var http = Client((r, ct) => ++calls < 3 ? throw Refused() : Task.FromResult(Json(Status())));
    using var connection = Connection(http, () => process);
    await connection.EnsureAsync(CancellationToken.None);
    Check(connection.OwnsBackend && calls == 3 && !process.Killed, "Child startup");
});

await Test("foreign compatible listener never grants child ownership", async () =>
{
    var process = new FakeProcess(); var calls = 0;
    using var http = Client((r, ct) => ++calls == 1 ? throw Refused() : Task.FromResult(Json(Status(88))));
    using var connection = Connection(http, () => process);
    await Expect<BackendConnectionException>(() => connection.EnsureAsync(CancellationToken.None));
    Check(process.Killed && process.Disposed && !connection.OwnsBackend, "Wrong PID cleanup");
});

await Test("early child exit produces a bounded startup failure", async () =>
{
    var process = new FakeProcess(); process.Exit();
    using var http = Client((r, ct) => throw Refused());
    using var connection = Connection(http, () => process);
    await Expect<BackendConnectionException>(() => connection.EnsureAsync(CancellationToken.None));
    Check(process.Disposed && !process.Killed, "Exited child cleanup");
});

await Test("repeated refused probes share one startup deadline", async () =>
{
    var process = new FakeProcess(); var clock = Stopwatch.StartNew();
    using var http = Client((r, ct) => throw Refused());
    using var connection = Connection(http, () => process, 100);
    await Expect<BackendConnectionException>(() => connection.EnsureAsync(CancellationToken.None));
    Check(clock.Elapsed < TimeSpan.FromSeconds(2) && process.Killed, "Total startup deadline");
});

await Test("hanging child probes share one startup deadline", async () =>
{
    var process = new FakeProcess(); var calls = 0; var clock = Stopwatch.StartNew();
    using var http = Client(async (r, ct) => { if (++calls == 1) throw Refused(); await Task.Delay(Timeout.Infinite, ct); return Json(Status()); });
    using var connection = Connection(http, () => process, 120);
    await Expect<BackendConnectionException>(() => connection.EnsureAsync(CancellationToken.None));
    Check(clock.Elapsed < TimeSpan.FromSeconds(2) && process.Killed && calls > 2, "Hanging probe deadline");
});

await Test("cancellation during startup terminates only the child", async () =>
{
    var process = new FakeProcess(); var calls = 0;
    using var cancellation = new CancellationTokenSource();
    using var http = Client(async (r, ct) => { if (++calls == 1) throw Refused(); cancellation.Cancel(); await Task.Delay(1, ct); return Json(Status()); });
    using var connection = Connection(http, () => process);
    await Expect<OperationCanceledException>(() => connection.EnsureAsync(cancellation.Token));
    Check(process.Killed && process.Disposed && !connection.OwnsBackend, "Cancelled startup cleanup");
});

await Test("pre-cancelled startup never launches", async () =>
{
    var launches = 0;
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    using var http = Client((r, ct) => { ct.ThrowIfCancellationRequested(); throw Refused(); });
    using var connection = Connection(http, () => { launches++; return new FakeProcess(); });
    await Expect<OperationCanceledException>(() => connection.EnsureAsync(cancellation.Token));
    Check(launches == 0, "Pre-cancelled launch");
});

await Test("owned shutdown is sent only after matching PID probe", async () =>
{
    var process = new FakeProcess(); var calls = 0; var shutdowns = 0;
    using var http = Client((r, ct) =>
    {
        if (r.Method == HttpMethod.Post)
        {
            Check(r.Headers.GetValues("X-Quota-Process-Id").Single() == "77", "Shutdown process guard");
            shutdowns++; process.Exit(); return Task.FromResult(Json("{}"));
        }
        return ++calls == 1 ? throw Refused() : Task.FromResult(Json(Status()));
    });
    using var connection = Connection(http, () => process);
    await connection.EnsureAsync(CancellationToken.None);
    await connection.StopOwnedAsync(CancellationToken.None);
    Check(shutdowns == 1 && !process.Killed && process.Disposed, "Graceful child shutdown");
});

await Test("replaced listener receives no shutdown request", async () =>
{
    var process = new FakeProcess(); var calls = 0; var shutdowns = 0;
    using var http = Client((r, ct) =>
    {
        if (r.Method == HttpMethod.Post) shutdowns++;
        return ++calls == 1 ? throw Refused() : Task.FromResult(Json(Status(calls > 2 ? 88 : 77)));
    });
    using var connection = Connection(http, () => process);
    await connection.EnsureAsync(CancellationToken.None);
    await connection.StopOwnedAsync(CancellationToken.None);
    Check(shutdowns == 0 && process.Killed && process.Disposed, "Replacement safety");
});

await Test("shutdown wait is bounded and kills only owned child", async () =>
{
    var process = new FakeProcess(); var calls = 0;
    using var http = Client((r, ct) => ++calls == 1 ? throw Refused() : Task.FromResult(Json(Status())));
    using var connection = Connection(http, () => process);
    await connection.EnsureAsync(CancellationToken.None);
    var clock = Stopwatch.StartNew();
    await connection.StopOwnedAsync(CancellationToken.None);
    Check(clock.Elapsed < TimeSpan.FromSeconds(2) && process.Killed && process.Disposed, "Shutdown deadline");
});

await Test("failed kill and disposal preserve original startup cancellation", async () =>
{
    var process = new FakeProcess { ThrowOnKill = true, ThrowOnDispose = true }; var calls = 0;
    using var cancellation = new CancellationTokenSource();
    using var http = Client(async (r, ct) => { if (++calls == 1) throw Refused(); cancellation.Cancel(); await Task.Delay(1, ct); return Json(Status()); });
    using var connection = Connection(http, () => process);
    await Expect<OperationCanceledException>(() => connection.EnsureAsync(cancellation.Token));
    Check(process.KillAttempted && process.DisposeAttempted && connection.CleanupFailed && !connection.OwnsBackend, "Cleanup masked startup cancellation");
});

await Test("process state kill and disposal faults cannot abort shutdown", async () =>
{
    var process = new FakeProcess(); var calls = 0;
    using var http = Client((r, ct) => ++calls == 1 ? throw Refused() : Task.FromResult(Json(Status())));
    using var connection = Connection(http, () => process);
    await connection.EnsureAsync(CancellationToken.None);
    process.ThrowOnHasExited = process.ThrowOnKill = process.ThrowOnDispose = true;
    await connection.StopOwnedAsync(CancellationToken.None);
    Check(process.KillAttempted && process.DisposeAttempted && connection.CleanupFailed && !connection.OwnsBackend, "Shutdown cleanup steps");
    connection.Dispose();
});

await Test("process wait fault still kills and releases the owned child", async () =>
{
    var process = new FakeProcess { ThrowOnWait = true }; var calls = 0;
    using var http = Client((r, ct) => ++calls == 1 ? throw Refused() : Task.FromResult(Json(Status())));
    using var connection = Connection(http, () => process);
    await connection.EnsureAsync(CancellationToken.None);
    await connection.StopOwnedAsync(CancellationToken.None);
    Check(process.Killed && process.Disposed && connection.CleanupFailed && !connection.OwnsBackend, "Wait fault cleanup");
});

await Test("reconnecting to an owned live reader reuses its process", async () =>
{
    var process = new FakeProcess(); var calls = 0; var launches = 0;
    using var http = Client((r, ct) => ++calls == 1 ? throw Refused() : Task.FromResult(Json(Status())));
    using var connection = Connection(http, () => { ++launches; return process; });
    await connection.EnsureAsync(CancellationToken.None);
    await connection.EnsureAsync(CancellationToken.None);
    Check(launches == 1 && connection.OwnsBackend && !process.Disposed, "Live reader reuse");
});

await Test("an unresponsive owned reader is retained without launching a duplicate", async () =>
{
    var process = new FakeProcess(); var calls = 0; var launches = 0; bool retry = false;
    using var http = Client(async (r, ct) =>
    {
        if (retry) await Task.Delay(Timeout.Infinite, ct);
        if (++calls == 1) throw Refused();
        return Json(Status());
    });
    using var connection = Connection(http, () => { ++launches; return process; });
    await connection.EnsureAsync(CancellationToken.None);
    retry = true;
    await Expect<BackendConnectionException>(() => connection.EnsureAsync(CancellationToken.None));
    Check(launches == 1 && connection.OwnsBackend && !process.Killed && !process.Disposed, "Timeout preserves owned reader");
    retry = false;
    await connection.EnsureAsync(CancellationToken.None);
    Check(launches == 1, "A later successful retry reuses the reader");
});

await Test("an owned reader refusing connections is not duplicated", async () =>
{
    var process = new FakeProcess(); var calls = 0; var launches = 0; bool retry = false;
    using var http = Client((r, ct) => retry || ++calls == 1 ? throw Refused() : Task.FromResult(Json(Status())));
    using var connection = Connection(http, () => { ++launches; return process; });
    await connection.EnsureAsync(CancellationToken.None);
    retry = true;
    await Expect<BackendConnectionException>(() => connection.EnsureAsync(CancellationToken.None));
    Check(launches == 1 && connection.OwnsBackend && !process.Killed, "Refusal preserves live child ownership");
});

await Test("retry replaces an exited owned reader without terminating another process", async () =>
{
    var first = new FakeProcess(); var second = new FakeProcess(); var calls = 0; var launches = 0;
    using var http = Client((r, ct) => ++calls is 1 or 3 ? throw Refused() : Task.FromResult(Json(Status())));
    using var connection = Connection(http, () => ++launches == 1 ? first : second);
    await connection.EnsureAsync(CancellationToken.None);
    first.Exit();
    await connection.EnsureAsync(CancellationToken.None);
    Check(launches == 2 && first.Disposed && !first.Killed && !second.Disposed, "Exited reader replacement");
});

await Test("a listener replacing an owned reader is not adopted during retry", async () =>
{
    var process = new FakeProcess(); var calls = 0; var launches = 0;
    using var http = Client((r, ct) => ++calls == 1 ? throw Refused() : Task.FromResult(Json(Status(calls > 2 ? 88 : 77))));
    using var connection = Connection(http, () => { ++launches; return process; });
    await connection.EnsureAsync(CancellationToken.None);
    await Expect<BackendConnectionException>(() => connection.EnsureAsync(CancellationToken.None));
    Check(launches == 1 && connection.OwnsBackend && !process.Killed, "Retry preserves original ownership");
});

await Test("concurrent connection requests serialize and launch only once", async () =>
{
    var process = new FakeProcess(); var calls = 0; var launches = 0;
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var http = Client(async (r, ct) =>
    {
        if (++calls == 1) throw Refused();
        await ready.Task.WaitAsync(ct);
        return Json(Status());
    });
    using var connection = Connection(http, () => { ++launches; return process; }, 1000);
    Task first = connection.EnsureAsync(CancellationToken.None);
    Task second = connection.EnsureAsync(CancellationToken.None);
    ready.SetResult();
    await Task.WhenAll(first, second);
    Check(launches == 1 && calls == 3 && connection.OwnsBackend, "Serialized connection attempts");
});

Console.WriteLine($"{passed} backend compatibility and ownership checks passed");

HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) =>
    new(new FakeHandler(handle)) { BaseAddress = new Uri("http://127.0.0.1:8765"), Timeout = Timeout.InfiniteTimeSpan };

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
}

sealed class FakeProcess : IBackendProcess
{
    private readonly TaskCompletionSource exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool hasExited;
    public int Id => 77;
    public bool HasExited => ThrowOnHasExited ? throw new InvalidOperationException("Process handle unavailable") : hasExited;
    public bool ThrowOnHasExited { get; set; }
    public bool ThrowOnKill { get; set; }
    public bool ThrowOnDispose { get; set; }
    public bool ThrowOnWait { get; set; }
    public bool KillAttempted { get; private set; }
    public bool DisposeAttempted { get; private set; }
    public bool Killed { get; private set; }
    public bool Disposed { get; private set; }
    public void Exit() { hasExited = true; exit.TrySetResult(); }
    public void Kill()
    {
        KillAttempted = true;
        if (ThrowOnKill) throw new InvalidOperationException("Process termination failed");
        Killed = true; Exit();
    }
    public Task WaitForExitAsync(CancellationToken cancellationToken) => ThrowOnWait
        ? throw new InvalidOperationException("Process wait failed") : exit.Task.WaitAsync(cancellationToken);
    public void Dispose()
    {
        DisposeAttempted = true;
        if (ThrowOnDispose) throw new InvalidOperationException("Process disposal failed");
        Disposed = true;
    }
}
