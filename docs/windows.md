# Agent Quota Monitor for Windows

Monitor your AI coding quotas from the Windows tray, main window, or compact floating monitor. This guide ships with the portable ZIP. The [project page](https://github.com/mahlernim/agent-quota-monitor-windows#readme) has screenshots and the latest version of this guide.

## Install and launch

Use the [Windows installer](https://github.com/mahlernim/agent-quota-monitor-windows/releases/download/v0.2.4/agent-quota-monitor-windows-0.2.4-setup-win-x64.exe) for a Start menu shortcut and an optional desktop shortcut. Installation is per user and needs no administrator rights.

For the [portable ZIP](https://github.com/mahlernim/agent-quota-monitor-windows/releases/download/v0.2.4/agent-quota-monitor-windows-0.2.4-win-x64.zip), extract the entire archive into a permanent folder and run `agent-quota-monitor-windows.exe`. Keep the `backend` folder and all runtime files beside the executable. Python and .NET runtimes are included.

The installer and application are unsigned. Windows may display an unknown-publisher warning.

## Connect your providers

Open **Settings**, select your providers, and click **Save monitored providers**. Existing sessions from official coding clients are detected within a minute. A browser sign-in alone may not be enough.

- **Codex and Claude.** **Sign in** launches the official client's sign-in. Install the official clients first.
- **Antigravity.** The official Antigravity CLI (`agy`) lets the monitor read quota while the desktop app is closed. When `agy` is missing, **Install CLI** shows Google's exact install command, `irm https://antigravity.google/cli/install.ps1 | iex`, and runs it in a visible PowerShell window only after you confirm. That window then runs `agy -p /usage` so you can sign in. You can also follow the [official instructions](https://antigravity.google/docs/cli/install) yourself. **Open desktop app** opens the Antigravity desktop app, which can supply readings while it runs. Gemini CLI does not report Antigravity quota. API-key mode is not supported.
- **Copilot.** Needs the optional [one-time setup](https://github.com/mahlernim/agent-quota-monitor-windows/blob/main/docs/copilot-setup.md). **Sign in** opens the official GitHub CLI and browser. Leave the console open until sign-in finishes.

After a successful CLI reading, the Antigravity account marked **CLI** replaces desktop-source cards. Stored selections are kept and identities are never merged.

Accounts remain separate by verified identity, even when email labels match. Direct Claude subscriptions are separate from Claude or GPT allowance supplied by Antigravity. Use a provider's own client to change accounts. The monitor never switches accounts automatically.

**Remove** hides an account and stops monitoring it without signing out or deleting credentials. **Restore hidden accounts** brings it back.

## Use the monitor

- Click a ring to choose the system-tray quota. Its label shows **Tray**.
- Click the pin at a ring's top-right corner to pin or unpin it on the floating monitor. Filled violet means pinned.
- Hover a ring for percentages, reset times, pace, and guidance when a reading has a problem.
- **Floating** shows or hides the floating monitor. Drag it to move it, double-click it to show the main window, and right-click it for **Size** and **Opacity**.
- **Reorder** moves accounts up or down and rings left or right. Each move saves immediately.
- **Close** hides the main window to the tray. **Quit** exits and stops the quota reader if this app started it.

The thick outer ring shows quota remaining in the provider's color. The thin gray inner ring shows time remaining when the reset time is known. Percentage text turns amber when quota remaining falls below half of time remaining, and red below one quarter.

Settings shows each account's identity, reading source, last successful read, next eligible read, and when an official session expires.

## Stale readings

A gray **STALE** ring preserves the last value that could be read. It does not mean zero, and a missing window never means unlimited. A value read before its window reset is shown as unknown until a new reading arrives. The ring tooltip and Settings explain the cause.

- **Antigravity CLI missing and desktop app closed.** Use **Install CLI**, or open the desktop app.
- **Claude Code session expired.** Open Claude Code. The monitor resumes once Claude Code renews its session.
- **No network connection.** The monitor retries every five minutes and again when Windows reconnects or wakes from sleep.
- **Provider format changed.** Check for a monitor update. With automatic checks on, the monitor checks once by itself.

When an official app closes, its recent reading stays live for up to ten minutes before turning stale. **Refresh** respects provider cooldowns.

## Start with Windows

Enable **Start with Windows (in the tray)** in Settings to launch at sign-in. It is off by default and needs no administrator rights. For a portable copy, disable this setting before moving or deleting the folder, then enable it again from the new location.

## Update or uninstall

The monitor checks GitHub's public releases API without credentials, shortly after launch and at most once a day. You can disable automatic checks or check immediately in Settings.

When an update is available, the banner offers these choices.

- **Install update** (installed copies). Downloads the installer from this project's GitHub release, verifies its published SHA-256 checksum, closes the monitor, and opens the installer. Nothing runs if verification fails.
- **Download update**. Opens the release page. Portable users should quit the monitor and replace all extracted files.
- **Later** and **Skip this version**. Postpone the reminder for a day, or hide that release.

Settings, cached quota, and account bindings are preserved. Setup stops a quota reader left over from an earlier run before replacing files, and the app replaces a reader from another version when it starts.

Uninstall the installed version through Windows Installed apps. This removes app files, shortcuts, and this installation's startup entry. Monitor settings and provider accounts are preserved. To remove a portable copy, disable its startup setting, quit, and delete its folder.

## Troubleshooting

- **An account is missing.** Confirm that the official client has a valid session, wait a minute, and press **Refresh**.
- **An Antigravity CLI account is stale.** Wait for the next eligible read shown in Settings. If the CLI needs sign-in, run `agy` in a terminal, sign in, run `agy -p /usage`, and press **Refresh**.
- **The quota reader is unavailable.** Click **Retry connection**. If it stays unavailable, use **Quit** and reopen the monitor.
- **The portable copy does nothing.** Confirm that the complete archive was extracted.

## Privacy and local data

The monitor reads official client sessions without taking over sign-in or renewal. It never sends an expired or copied token, sends no model prompts, and has no telemetry.

Quota settings and cached snapshots are encrypted for your Windows user under `%LOCALAPPDATA%\QuotaDashboard`. Non-secret update preferences are stored in your user registry. The quota reader communicates with the app over loopback only. It is intended for a trusted personal computer and does not isolate access from other local processes.

Provider interfaces can change and interrupt readings. See the [release page](https://github.com/mahlernim/agent-quota-monitor-windows/releases/tag/v0.2.4) for release notes and the [development guide](https://github.com/mahlernim/agent-quota-monitor-windows/blob/main/docs/development.md) for building from source.

MIT licensed. Independent project, not affiliated with or endorsed by any supported provider.
