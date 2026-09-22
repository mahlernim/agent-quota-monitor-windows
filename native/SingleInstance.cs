using System;
using System.Threading;

namespace AgentQuotaMonitor;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    internal EventWaitHandle Activation { get; }
    internal bool IsPrimary { get; }
    private bool _disposed;

    internal SingleInstance(string mutexName = "Local\\AgentQuotaMonitorWpf", string eventName = "Local\\AgentQuotaMonitorWpfActivate")
    {
        // Publish the activation event before the ownership mutex can be observed.
        Activation = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        try
        {
            _mutex = new Mutex(false, mutexName);
            IsPrimary = TakeOwnership(0);
            if (!IsPrimary)
            {
                Activation.Set();
                // A primary that exits during activation leaves ownership available.
                IsPrimary = TakeOwnership(100);
            }
        }
        catch { _mutex?.Dispose(); Activation.Dispose(); throw; }
    }

    private bool TakeOwnership(int timeout)
    {
        try { return _mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
        Activation.Dispose();
    }
}
