using System;
using Microsoft.Win32;

namespace AgentQuotaMonitor;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Entry = "AgentQuotaMonitorWindows";
    internal static string Command(string executable) => "\"" + executable + "\" --minimized";
    internal static bool Enabled {
        get { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue(Entry) is string value && value.Length > 0; }
    }
    internal static void SetEnabled(bool enabled) {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled) key.SetValue(Entry, Command(Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable")), RegistryValueKind.String);
        else key.DeleteValue(Entry, false);
    }
}
