# Connect GitHub Copilot

Copilot needs a one-time setup. The monitor reads Copilot quota through the official GitHub CLI sign-in, the official Copilot CLI, and the official Copilot SDK. A browser sign-in alone is not enough.

## One-time setup

1. Open **Settings** and tick **GitHub Copilot**.
2. Choose **Set up Copilot**. The confirmation lists exactly what will run. Nothing runs until you choose OK.
3. A visible PowerShell window then:
   - installs any missing official tools with winget: GitHub CLI (`GitHub.cli`), Node.js LTS (`OpenJS.NodeJS.LTS`), and GitHub Copilot CLI (`GitHub.Copilot`). winget may ask you to accept their terms, and Node.js may ask for administrator approval.
   - installs the official Copilot SDK into `%LOCALAPPDATA%\QuotaDashboard\copilot-runtime`
   - starts GitHub sign-in in your browser, only if the GitHub CLI isn't signed in yet
4. When the window says it's finished, choose **Connect** beside GitHub Copilot, in Settings or in the main window banner. Connect links the account the GitHub CLI is signed in to and checks its quota. It sends no prompts.

Tools installed during setup are found within a minute without restarting the monitor. winget comes with App Installer on current Windows versions. If it's missing, install App Installer from the Microsoft Store first.

## Sign in again or choose another account

In Settings, choose **Sign in** beside GitHub Copilot. Complete the prompts in the GitHub CLI console and browser, and leave the console open until it finishes. The monitor checks the account and quota before connecting it. This also changes the session the GitHub CLI uses.

Copilot's own `copilot login` is supported by GitHub, but the monitor uses the session established by `gh auth login`.

## Building from source

`Setup-Copilot.ps1` in the repository remains available for development. It needs PowerShell 7, Node.js, and Python, and it verifies the quota with the source reader.

See [GitHub CLI authentication](https://cli.github.com/manual/gh_auth_login) and [Copilot CLI authentication](https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/authenticate-copilot-cli).
