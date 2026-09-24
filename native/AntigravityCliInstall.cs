using System;
using System.Diagnostics;
using System.IO;

namespace AgentQuotaMonitor;

/// <summary>
/// An official installer that runs in a visible PowerShell window after the user confirms.
/// The monitor never downloads, stores, or runs the vendor script itself.
/// </summary>
internal sealed record OfficialInstall(string Title, string Command, string Confirmation, string Script, bool BypassPolicy = false)
{
    /// <summary>Google's official Antigravity CLI installer.</summary>
    internal static readonly OfficialInstall Antigravity = new("Install Antigravity CLI", AntigravityCliInstall.Command,
        AntigravityCliInstall.Confirmation, AntigravityCliInstall.Script);

    /// <summary>Anthropic's official native Claude Code installer.</summary>
    internal static readonly OfficialInstall Claude = Client("Claude Code", "https://claude.ai/install.ps1",
        "It installs Claude Code for your Windows account in %USERPROFILE%\\.local\\bin.", "Anthropic Claude");

    /// <summary>OpenAI's official standalone Codex installer. OpenAI documents running it with a process-only execution policy bypass.</summary>
    internal static readonly OfficialInstall Codex = Client("Codex", "https://chatgpt.com/codex/install.ps1",
        "It installs Codex for your Windows account in %LOCALAPPDATA%\\Programs\\OpenAI\\Codex and adds it to your PATH.", "OpenAI Codex", true);

    private static OfficialInstall Client(string name, string source, string location, string row, bool bypass = false)
    {
        string command = "irm " + source + " | iex";
        string confirmation = $"Install {name}?\n\nA PowerShell window will run the official install command:\n{command}\n\n{location} " +
            $"It doesn't change any other installation.\n\nWhen it finishes, choose Sign in beside {row} in Settings. The monitor never sees your credentials.";
        string script = string.Join("\n",
            "$ErrorActionPreference = 'Stop'",
            $"Write-Host 'Installing {name} from {source}' -ForegroundColor Cyan",
            command,
            "Write-Host ''",
            $"Write-Host 'Finished. Return to Agent Quota Monitor and choose Sign in beside {row}.' -ForegroundColor Green");
        return new OfficialInstall("Install " + name, command, confirmation, script, bypass);
    }

    internal ProcessStartInfo StartInfo()
    {
        // Use the system PowerShell by full path so a same-named program on PATH is never chosen.
        string shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NoExit");
        if (BypassPolicy) { start.ArgumentList.Add("-ExecutionPolicy"); start.ArgumentList.Add("Bypass"); }
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(Script);
        return start;
    }

    internal void Start()
    {
        using var process = Process.Start(StartInfo());
    }

    /// <summary>Shows the exact official command. Nothing runs unless the user chooses OK.</summary>
    internal bool Confirm(System.Windows.Window owner) =>
        System.Windows.MessageBox.Show(owner, Confirmation, Title, System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Question, System.Windows.MessageBoxResult.Cancel) == System.Windows.MessageBoxResult.OK;
}

/// <summary>Google's official Antigravity CLI installer, followed by agy -p /usage for sign-in.</summary>
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

    internal static ProcessStartInfo StartInfo() => OfficialInstall.Antigravity.StartInfo();
    internal static void Start() => OfficialInstall.Antigravity.Start();
    internal static bool Confirm(System.Windows.Window owner) => OfficialInstall.Antigravity.Confirm(owner);
}
