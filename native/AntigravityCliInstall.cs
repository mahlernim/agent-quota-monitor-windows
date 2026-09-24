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

    internal const string CopilotSdk = "@github/copilot-sdk@1.0.14";

    /// <summary>
    /// Official Copilot components for installed copies. winget installs only missing tools and
    /// keeps its own license prompts. The SDK goes into the monitor's own runtime folder.
    /// </summary>
    internal static readonly OfficialInstall Copilot = new("Set up Copilot",
        "npm install --prefix %LOCALAPPDATA%\\QuotaDashboard\\copilot-runtime " + CopilotSdk,
        "Set up GitHub Copilot monitoring?\n\nA PowerShell window will:\n\n" +
        "1. Install any missing official tools with winget: GitHub CLI (GitHub.cli), Node.js LTS (OpenJS.NodeJS.LTS), and GitHub Copilot CLI (GitHub.Copilot). " +
        "winget may ask you to accept their terms, and Node.js may ask for administrator approval.\n" +
        "2. Install the official Copilot SDK (" + CopilotSdk + ") into %LOCALAPPDATA%\\QuotaDashboard\\copilot-runtime.\n" +
        "3. Start GitHub sign-in in your browser only if the GitHub CLI isn't signed in yet.\n\n" +
        "Then choose Connect beside GitHub Copilot. The monitor never sees your credentials and sends no prompts.",
        string.Join("\n",
            "$ErrorActionPreference = 'Stop'",
            "function Update-SessionPath { $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User') }",
            "if (-not (Get-Command winget.exe -ErrorAction SilentlyContinue)) { throw 'winget is required. Install App Installer from the Microsoft Store, then try again.' }",
            "function Install-Missing([string]$id, [scriptblock]$present) {",
            "  if (& $present) { Write-Host \"$id is already installed.\"; return }",
            "  Write-Host \"Installing $id with winget\" -ForegroundColor Cyan",
            "  winget install --id $id --exact --source winget",
            "  if ($LASTEXITCODE) { throw \"$id installation failed.\" }",
            "  Update-SessionPath",
            "}",
            "Install-Missing 'GitHub.cli' { Get-Command gh.exe -ErrorAction SilentlyContinue }",
            "Install-Missing 'OpenJS.NodeJS.LTS' { Get-Command node.exe -ErrorAction SilentlyContinue }",
            "Install-Missing 'GitHub.Copilot' { (Get-Command copilot.exe -ErrorAction SilentlyContinue) -or (Get-ChildItem \"$env:LOCALAPPDATA\\Microsoft\\WinGet\\Packages\\GitHub.Copilot_*\\copilot.exe\" -ErrorAction SilentlyContinue) }",
            "$runtime = Join-Path $env:LOCALAPPDATA 'QuotaDashboard\\copilot-runtime'",
            "New-Item -ItemType Directory -Path $runtime -Force | Out-Null",
            "Write-Host 'Installing the official Copilot SDK' -ForegroundColor Cyan",
            "npm install --prefix $runtime --no-audit --no-fund " + CopilotSdk,
            "if ($LASTEXITCODE) { throw 'Copilot SDK installation failed.' }",
            "gh auth status --hostname github.com *> $null",
            "if ($LASTEXITCODE) {",
            "  Write-Host 'Signing in to GitHub. Follow the prompts here and in your browser.' -ForegroundColor Cyan",
            "  gh auth login --hostname github.com --git-protocol https --web",
            "  if ($LASTEXITCODE) { throw 'GitHub sign-in did not finish.' }",
            "}",
            "Write-Host ''",
            "Write-Host 'Finished. Return to Agent Quota Monitor and choose Connect beside GitHub Copilot.' -ForegroundColor Green"));

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
