using System;
using System.Diagnostics;
using System.IO;

namespace AgentQuotaMonitor;

/// <summary>
/// Runs Google's official Antigravity CLI installer in a visible PowerShell window after the
/// user confirms. The monitor never downloads, stores, or runs the script itself.
/// </summary>
internal static class AntigravityCliInstall
{
    internal const string Source = "https://antigravity.google/cli/install.ps1";
    internal const string Command = "irm " + Source + " | iex";

    internal const string Confirmation =
        "Install the official Antigravity CLI (agy)?\n\n" +
        "A PowerShell window will run Google's official install command:\n" +
        Command + "\n\n" +
        "It installs agy for your Windows account in %LOCALAPPDATA%\\agy\\bin. The same window then runs " +
        "agy -p /usage, which asks you to sign in through your browser if needed and shows your quota.\n\n" +
        "The monitor never sees your credentials. It starts reading quota through agy within a minute, " +
        "even while the Antigravity desktop app is closed.";

    internal static readonly string Script = string.Join("\n",
        "$ErrorActionPreference = 'Stop'",
        "Write-Host 'Installing the official Antigravity CLI from " + Source + "' -ForegroundColor Cyan",
        Command,
        "$agy = Join-Path $env:LOCALAPPDATA 'agy\\bin\\agy.exe'",
        "if (-not (Test-Path -LiteralPath $agy)) { $agy = (Get-Command agy.exe).Source }",
        "Write-Host ''",
        "Write-Host 'Reading quota with agy. Complete sign-in in your browser if it opens.' -ForegroundColor Cyan",
        "& $agy -p /usage",
        "Write-Host ''",
        "Write-Host 'Finished. You can close this window. Agent Quota Monitor uses the CLI within a minute.' -ForegroundColor Green");

    internal static ProcessStartInfo StartInfo()
    {
        // Use the system PowerShell by full path so a same-named program on PATH is never chosen.
        string shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        foreach (string argument in new[] { "-NoProfile", "-NoExit", "-Command", Script })
            start.ArgumentList.Add(argument);
        return start;
    }

    internal static void Start()
    {
        using var process = Process.Start(StartInfo());
    }
}
