using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgentQuotaMonitor;

public sealed class BackendConnectionException : Exception
{
    public BackendConnectionException(string message, Exception? inner = null) : base(message, inner) { }
}

// The service marker checks compatibility and process ownership, not caller authentication.
public sealed class BackendConnection : IDisposable
{
    private readonly HttpClient http;
    private readonly Func<IBackendProcess> launch;
    private readonly TimeSpan startupTimeout;
    private readonly TimeSpan probeTimeout;
    private readonly TimeSpan retryDelay;
    private readonly TimeSpan shutdownTimeout;
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private IBackendProcess? owned;
    private int? acceptedProcessId;
    public bool OwnsBackend => owned != null;
    internal bool CleanupFailed { get; private set; }

    public BackendConnection(HttpClient http, Func<Process> launch)
        : this(http, () => new BackendProcess(launch())) { }

    internal BackendConnection(HttpClient http, Func<IBackendProcess> launch,
        TimeSpan? startupTimeout = null, TimeSpan? probeTimeout = null,
        TimeSpan? retryDelay = null, TimeSpan? shutdownTimeout = null)
    {
        this.http = http;
        this.launch = launch;
        this.startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(15);
        // Windows can take just over two seconds to report a refused loopback connection.
        // Leave room for that result while retaining the overall startup deadline.
        this.probeTimeout = probeTimeout ?? TimeSpan.FromSeconds(5);
        this.retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(250);
        this.shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(6);
    }

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        await connectionGate.WaitAsync(cancellationToken);
        try { await EnsureCoreAsync(cancellationToken); }
        finally { connectionGate.Release(); }
    }

    private async Task EnsureCoreAsync(CancellationToken cancellationToken)
    {
        bool launchedThisAttempt = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(startupTimeout);
        try
        {
            acceptedProcessId = null;
            if (owned?.HasExited == true) ReleaseOwned();
            var initial = await ProbeAsync(deadline.Token);
            if (initial.ProcessId.HasValue)
            {
                if (owned != null && initial.ProcessId != owned.Id) throw Conflict();
                deadline.Token.ThrowIfCancellationRequested();
                acceptedProcessId = initial.ProcessId;
                return;
            }
            if (initial.TimedOut)
                throw new BackendConnectionException("The local quota reader did not respond in time. Click Retry connection to try again. No additional reader was started.");
            if (owned != null)
                throw new BackendConnectionException("The quota reader is still running but is not accepting connections. Click Retry connection to try again. If this continues, use Quit and reopen the monitor.");
            deadline.Token.ThrowIfCancellationRequested();
            owned = launch();
            launchedThisAttempt = true;
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (owned.HasExited)
                    throw new BackendConnectionException("The quota reader exited before it was ready. Check that the complete application folder is available and that port 8765 is free.");
                var result = await ProbeAsync(deadline.Token);
                if (result.ProcessId.HasValue)
                {
                    if (result.ProcessId != owned.Id)
                        throw new BackendConnectionException("Another local quota reader is using port 8765. Quit the other monitor instance, then click Retry connection.");
                    if (owned.HasExited)
                        throw new BackendConnectionException("The quota reader exited during startup. Try launching the monitor again.");
                    deadline.Token.ThrowIfCancellationRequested();
                    acceptedProcessId = result.ProcessId;
                    return;
                }
                await Task.Delay(retryDelay, deadline.Token);
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            if (launchedThisAttempt) ReleaseOwned();
            throw new BackendConnectionException("The quota reader did not become ready within the startup time limit. Click Retry connection to try again. Check that the complete application folder is available if this continues.", error);
        }
        catch
        {
            if (launchedThisAttempt) ReleaseOwned();
            throw;
        }
    }

    public async Task StopOwnedAsync(CancellationToken cancellationToken)
    {
        if (owned == null) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(shutdownTimeout);
        try
        {
            if (owned.HasExited) return;
            var peer = await ProbeAsync(deadline.Token);
            if (peer.ProcessId != owned.Id) return;
            using var body = new StringContent("{}", Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/shutdown") { Content = body };
            request.Headers.Add("X-Quota-Process-Id", owned.Id.ToString(CultureInfo.InvariantCulture));
            using var response = await http.SendAsync(request, deadline.Token);
            if (response.IsSuccessStatusCode) await owned.WaitForExitAsync(deadline.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or HttpRequestException or BackendConnectionException)
        {
            // A failed or replaced listener never grants ownership of that listener.
        }
        catch { CleanupFailed = true; }
        finally { ReleaseOwned(); }
    }

    private async Task<Probe> ProbeAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(probeTimeout);
        try
        {
            using var response = await http.GetAsync("/api/status", HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/json")
                throw Conflict();
            const int limit = 2 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > limit) throw Conflict();
            using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var data = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await input.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (data.Length + read > limit) throw Conflict();
                data.Write(buffer, 0, read);
            }
            using var document = JsonDocument.Parse(data.ToArray());
            return new Probe(ValidateStatus(document.RootElement), false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new Probe(null, true); }
        catch (HttpRequestException error) when (ConnectionRefused(error))
        { return new Probe(null, false); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw Conflict(error); }
        catch (HttpRequestException error)
        { throw new BackendConnectionException("The local quota reader could not be reached. Click Retry connection to try again.", error); }
    }

    public void ValidatePeer(JsonElement root)
    {
        try
        {
            if (acceptedProcessId == null || ReadPeer(root) != acceptedProcessId) throw Conflict();
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException)
        { throw Conflict(error); }
    }

    private static int ReadPeer(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw Conflict();
        var backend = root.GetProperty("backend");
        if (backend.ValueKind != JsonValueKind.Object || backend.GetProperty("name").GetString() != "agent-quota-monitor" ||
            !backend.GetProperty("protocolVersion").TryGetInt32(out var version) || version != 1 ||
            !backend.GetProperty("processId").TryGetInt32(out var pid) || pid <= 0) throw Conflict();
        return pid;
    }

    private static int ValidateStatus(JsonElement root)
    {
        var pid = ReadPeer(root);
        if (!root.GetProperty("now").TryGetDouble(out var now) || !double.IsFinite(now)) throw Conflict();
        var accounts = root.GetProperty("accounts");
        if (accounts.ValueKind != JsonValueKind.Array) throw Conflict();
        var accountIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var account in accounts.EnumerateArray())
        {
            if (!accountIds.Add(RequiredId(account)) || account.GetProperty("provider").ValueKind != JsonValueKind.String) throw Conflict();
            var groups = account.GetProperty("groups");
            if (groups.ValueKind != JsonValueKind.Array) throw Conflict();
            var groupIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in groups.EnumerateArray())
            {
                if (!groupIds.Add(RequiredId(group))) throw Conflict();
                var buckets = group.GetProperty("buckets");
                if (buckets.ValueKind != JsonValueKind.Array) throw Conflict();
                var bucketIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var bucket in buckets.EnumerateArray())
                    if (!bucketIds.Add(RequiredId(bucket))) throw Conflict();
            }
        }
        return pid;
    }

    private static string RequiredId(JsonElement element)
    {
        var value = element.GetProperty("id").GetString();
        return !string.IsNullOrWhiteSpace(value) ? value : throw Conflict();
    }

    private static bool ConnectionRefused(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
            if (current is SocketException socket && socket.SocketErrorCode == SocketError.ConnectionRefused) return true;
        return false;
    }

    private static BackendConnectionException Conflict(Exception? inner = null) => new(
        "Port 8765 is being used by an incompatible service or an older quota reader. Quit an older monitor if one is running, then click Retry connection.", inner);

    private void ReleaseOwned()
    {
        var process = owned;
        owned = null;
        acceptedProcessId = null;
        if (process == null) return;
        // Each cleanup step is independent. A process-state race must not skip
        // disposal, mask a startup error, or prevent the application from exiting.
        bool shouldKill = true;
        try { shouldKill = !process.HasExited; }
        catch { CleanupFailed = true; }
        if (shouldKill)
        {
            try { process.Kill(); }
            catch { CleanupFailed = true; }
        }
        try { process.Dispose(); }
        catch { CleanupFailed = true; }
    }

    public void Dispose() => ReleaseOwned();
    private readonly record struct Probe(int? ProcessId, bool TimedOut);
}

internal interface IBackendProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void Kill();
}

internal sealed class BackendProcess : IBackendProcess
{
    private readonly Process process;
    internal BackendProcess(Process process) { this.process = process; }
    public int Id => process.Id;
    public bool HasExited => process.HasExited;
    public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
    public void Kill()
    {
        try { process.Kill(); }
        catch (InvalidOperationException) when (process.HasExited) { }
    }
    public void Dispose() => process.Dispose();
}
