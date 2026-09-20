# Connect GitHub Copilot

Copilot needs a one-time setup in addition to the portable app. It uses GitHub CLI authentication and the official Copilot CLI and SDK. A browser sign-in alone is not enough.

## One-time setup

1. Install PowerShell 7, Node.js with npm, Python 3.12 or newer, and GitHub CLI from their official distributions.
2. Download the source ZIP from the repository Code menu, or clone the repository. Extract it and open PowerShell 7 in that folder.
3. Run `gh auth login --hostname github.com --web` and complete GitHub's prompts with your Copilot account.
4. Run `./Setup-Copilot.ps1`. It installs the official Copilot CLI if needed and an isolated SDK runtime, then verifies your quota without sending a coding prompt.
5. Open the monitor, enable GitHub Copilot in Settings, and save monitored providers.

## Sign in again or choose another account

In Settings, click **Sign in** beside GitHub Copilot. Complete the prompts in the GitHub CLI console and browser. Leave the console open until it finishes. The monitor checks the account and quota before connecting it. This also changes the session used by GitHub CLI.

If optional components are missing, Settings displays setup guidance before opening sign-in. If you change accounts outside the monitor, use Sign in to verify and reconnect it.

Copilot's own `copilot login` is supported by GitHub, but this version of the monitor specifically uses the session established by `gh auth login`.

See [GitHub CLI authentication](https://cli.github.com/manual/gh_auth_login) and [Copilot CLI authentication](https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/authenticate-copilot-cli).
