# Agent Quota Monitor for Windows

See how much of your AI coding quota is left, when each window resets, and whether you are spending faster than time is passing. It lives in the Windows tray and never sends a prompt to find out.

**[Download the Windows installer](https://github.com/mahlernim/agent-quota-monitor-windows/releases/download/v0.2.4/agent-quota-monitor-windows-0.2.4-setup-win-x64.exe)** · [Portable ZIP](https://github.com/mahlernim/agent-quota-monitor-windows/releases/download/v0.2.4/agent-quota-monitor-windows-0.2.4-win-x64.zip) · [Release notes](https://github.com/mahlernim/agent-quota-monitor-windows/releases/tag/v0.2.4)

![Main window with grouped quota rings](docs/images/main-window.png)

*Main window. Accounts and quota values shown are sample data.*

![Floating monitor strip pinned above other windows](docs/images/floating-monitor.png)

*Floating monitor. Accounts and quota values shown are sample data.*

## Why use it

- **One glance for every subscription.** OpenAI Codex, Anthropic Claude, Google Antigravity, and GitHub Copilot quotas appear side by side.
- **Pace, not just percentages.** Each ring compares quota left with time left in its window and warns you in amber or red when you are ahead of pace.
- **Uses the sign-ins you already have.** Quotas are read through the official clients' existing sessions. The monitor never takes over sign-in, renews tokens, or switches accounts.
- **Honest when data is old.** Values that could not be refreshed stay visible in gray with the reason, and values from before a reset are shown as unknown.
- **Private by design.** No telemetry and no model prompts. Local data is encrypted for your Windows account, and the reader listens on loopback only.

## Contents

- [Install](#install)
- [Connect your providers](#connect-your-providers)
- [Read a quota ring](#read-a-quota-ring)
- [Everyday use](#everyday-use)
- [Stale readings and what they mean](#stale-readings-and-what-they-mean)
- [Start with Windows](#start-with-windows)
- [Updates](#updates)
- [Troubleshooting](#troubleshooting)
- [Privacy](#privacy)

## Install

1. Download and run the [Windows x64 installer](https://github.com/mahlernim/agent-quota-monitor-windows/releases/download/v0.2.4/agent-quota-monitor-windows-0.2.4-setup-win-x64.exe). No administrator rights are needed.
2. Open **Agent Quota Monitor** from the Start menu.
3. Open **Settings**, tick the providers you use, and click **Save monitored providers**.

Python and .NET runtimes are bundled. You still need the official coding client for each provider you use.

**Portable copy.** Download the ZIP, extract **all** files into a permanent folder, and run `agent-quota-monitor-windows.exe`. Do not run it from inside the ZIP. Keep the `backend` folder beside the executable.

The installer and application are unsigned, so Windows may show an unknown-publisher warning.

## Connect your providers

Any combination works, and one provider is enough. If an official client is already signed in, its account appears within a minute without any action.

| Provider | Official source the monitor reads | How to connect |
| --- | --- | --- |
| OpenAI Codex | Codex CLI session | Settings, then **Sign in** |
| Anthropic Claude (direct subscription) | Claude Code session | Settings, then **Sign in** |
| Google Antigravity | Antigravity CLI (`agy`), or the running desktop app | Settings, then **Install CLI** or **Open desktop app** |
| GitHub Copilot (optional) | GitHub CLI and Copilot SDK | One-time [Copilot setup](docs/copilot-setup.md), then **Sign in** |

A browser-only login may not create the session the monitor reads. Sign in through the official client instead.

### Google Antigravity

The official Antigravity CLI (`agy`) is the recommended source. It lets the monitor read quota even while the Antigravity desktop app is closed. Without it, Antigravity readings stop whenever the desktop app closes.

- **Install CLI** appears in Settings when `agy` is missing. It first shows Google's exact install command, `irm https://antigravity.google/cli/install.ps1 | iex`, and runs nothing unless you confirm.
- After you confirm, a visible PowerShell window runs that official installer, then `agy -p /usage`. Sign in through your browser if it opens. The monitor picks up the CLI within a minute.
- You can also install it yourself from the [official instructions](https://antigravity.google/docs/cli/install), run `agy -p /usage` once, and press **Refresh**.
- Gemini CLI is a different product and does not report Antigravity subscription quota.
- API-key and custom-provider modes are not supported for subscription monitoring.

After a successful CLI reading, the account marked **CLI** replaces desktop-source cards. Pin quotas on that account to keep them in the floating monitor.

### Accounts stay separate

Accounts are matched by stable provider identity, never by email label, so two accounts with the same address stay separate. Direct Claude subscriptions are kept apart from the Claude and GPT allowance supplied by Antigravity. To change accounts, use the provider's own client.

**Remove** in Settings hides an account and stops monitoring it. It does not sign you out or touch credentials. **Restore hidden accounts** brings it back.

## Read a quota ring

The thick outer ring is quota remaining, drawn in the provider's color. The thin gray ring inside it is time remaining in the current window. It appears only when the reset time is known.

The percentage turns **amber** when quota remaining falls below half of time remaining, and **red** below one quarter. For example, with 80% of the window left, amber starts under 40% quota and red under 20%. Without timing data there is no pace warning.

Hover any ring for exact values, the reset time, pace, and, when something is wrong, what to do about it.

## Everyday use

- **Click a ring** to show that quota in the system tray. The chosen ring is labelled **Tray**.
- **Click the pin** at a ring's top-right corner to pin or unpin it on the floating monitor. Filled violet means pinned.
- **Toolbar.** Refresh, Floating, Reorder, Settings, and Quit.
- **Reorder** moves accounts up or down and rings left or right. Each move saves immediately, and pins and the tray choice follow their rings.
- **Floating monitor.** Drag to move it, double-click to open the main window, and right-click for **Size** (75 to 200%) and **Opacity** (35 to 100%). Its size, opacity, and position are remembered.
- **Close** hides the main window to the tray. **Quit** exits and stops the quota reader it started.

Settings shows each account's identity, reading source, last successful read, next eligible read, and when an official session expires.

## Stale readings and what they mean

A gray **STALE** ring shows the last value the monitor could read. It never means zero, and a missing window never means unlimited. The ring tooltip and Settings explain the cause.

| What you see | What it means | What to do |
| --- | --- | --- |
| Antigravity CLI is not installed and the desktop app is closed | No Antigravity source is running | Use **Install CLI** in Settings, or open the desktop app |
| The Claude Code session expired | Claude Code has not renewed its session recently | Open Claude Code. The monitor resumes by itself once the session is renewed |
| No network connection | Requests could not reach the provider | Nothing. The monitor retries every five minutes and again when Windows reconnects or wakes |
| The provider changed its response format | A provider update changed its data | Check for a monitor update. The monitor checks once on its own if automatic checks are on |
| Reset since the last read | The window has reset since the value was read | Wait for the next reading. The old value is hidden because it no longer applies |

When an official app closes, its recent reading stays live until it is ten minutes old, then turns stale. **Refresh** always respects provider cooldowns. Settings shows when each account can be read again.

## Start with Windows

Settings has an optional **Start with Windows (in the tray)** switch. It is off by default and needs no administrator rights. For a portable copy, turn it off before moving the folder, then turn it on again from the new location.

## Updates

The monitor checks GitHub for a new release shortly after startup, at most once a day. When a release is available, a banner appears in the main window.

- **Install update** (installed copies only) downloads the installer from this project's GitHub release, verifies it against the published SHA-256 checksum, closes the monitor, and opens the installer. Nothing is installed without your confirmation, and nothing runs if verification fails.
- **Download update** opens the release page instead. Portable copies always use this. Quit the monitor before replacing the extracted files.
- **Later** postpones the reminder for a day. **Skip this version** hides that release.

**Check for updates** in Settings checks immediately. You can turn off automatic checks there. Settings, cached quota, and account bindings are kept across updates.

To uninstall, use Windows **Installed apps**. App files, shortcuts, and the startup entry are removed. Monitor settings and provider accounts are kept.

## Troubleshooting

- **An account is missing.** Sign in through the provider's official client, wait a minute, then press **Refresh**.
- **Antigravity goes stale when the desktop app closes.** Install the Antigravity CLI from Settings. See [Google Antigravity](#google-antigravity).
- **An Antigravity CLI account is stale.** Wait for the next eligible read shown in Settings, then press **Refresh**. If the CLI needs sign-in, run `agy` in a terminal, sign in, run `agy -p /usage`, and refresh again.
- **Claude says the session expired.** Open Claude Code. If Claude Code itself reports that you are signed out, use **Sign in** in Settings.
- **Claude says the read was rejected.** Check `claude auth status`, open Claude Code to let it renew its session, and press **Refresh**. If it still fails, sign in again through Claude Code.
- **Copilot shows nothing.** Complete the one-time [Copilot setup](docs/copilot-setup.md), then use **Sign in** and finish the prompts in the console window.
- **The quota reader is unavailable.** Click **Retry connection** in the toolbar. If it stays unavailable, use **Quit** and reopen the monitor. After an upgrade, the monitor replaces a reader left over from the previous version automatically.
- **The portable copy does nothing when launched.** Make sure the whole ZIP was extracted, including the `backend` folder.
- **An update could not be verified.** Nothing was installed. Try again later or use **Download update**.

## Privacy

- Credentials stay with the official clients. The monitor reads their existing sessions and never renews, copies, or logs tokens.
- It never sends prompts to a model, starts usage windows, or routes traffic through a proxy.
- Settings and cached quota are encrypted for your Windows account under `%LOCALAPPDATA%\QuotaDashboard`. Non-secret update preferences are stored in your user registry.
- The quota reader talks to the app over loopback only. It is meant for a trusted personal computer and does not isolate access from other local programs.
- There is no telemetry. Update checks use GitHub's public releases API without credentials.

Provider interfaces are internal to those products and can change without notice, which may interrupt readings.

## More

- [Windows guide](docs/windows.md), included in the portable ZIP
- [Provider interfaces](docs/provider-evidence.md)
- [Building and contributing](docs/development.md)
- [Third-party notices](THIRD-PARTY-NOTICES.md)

MIT licensed. Independent project, not affiliated with or endorsed by any of the supported providers.
