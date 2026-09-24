# Agent Quota Monitor for Windows

Monitor your AI coding quotas from the Windows tray, main window, or compact floating monitor. This guide ships with the portable ZIP. The [project page](https://github.com/mahlernim/agent-quota-monitor-windows#readme) has screenshots and the latest version of this guide.

## Install and launch

Use the [Windows installer](https://github.com/mahlernim/agent-quota-monitor-windows/releases/download/v0.3.3/agent-quota-monitor-windows-0.3.3-setup-win-x64.exe) for a Start menu shortcut and an optional desktop shortcut. Installation is per user and needs no administrator rights.

For the [portable ZIP](https://github.com/mahlernim/agent-quota-monitor-windows/releases/download/v0.3.3/agent-quota-monitor-windows-0.3.3-win-x64.zip), extract the entire archive into a permanent folder and run `agent-quota-monitor-windows.exe`. Keep the `backend` folder and all runtime files beside the executable. Python and .NET runtimes are included.

The installer and application are unsigned. Windows may display an unknown-publisher warning.

## Connect your providers

Open **Settings** (the gear icon) and select your providers. Changes save right away. Existing sessions from official coding clients are detected within a minute. A browser sign-in alone may not be enough.

- **Codex and Claude.** **Sign in** launches the official client's sign-in. If a client is missing, **Install Codex** or **Install Claude Code** shows the official install command and runs it in a visible PowerShell window after you confirm. The clients renew their own sessions while you use them. If a session lapses, use that client and the monitor resumes by itself.
- **Claude specifically.** The monitor reads the sign-in saved by Claude Code, the command-line tool. The Claude desktop app or claude.ai alone isn't enough. Claude Code needs a Pro, Max, Team, or Enterprise plan. **Sign in** opens a Claude Code window next to the browser. If the browser shows a code instead of returning, paste it into that window.
- **Antigravity.** The official Antigravity CLI (`agy`) lets the monitor read quota while the desktop app is closed. When `agy` is missing, **Install CLI** shows Google's exact install command, `irm https://antigravity.google/cli/install.ps1 | iex`, and runs it in a visible PowerShell window only after you confirm. That window then runs `agy -p /usage` so you can sign in. You can also follow the [official instructions](https://antigravity.google/docs/cli/install) yourself. **Open desktop app** opens the Antigravity desktop app, which can supply readings while it runs. Gemini CLI does not report Antigravity quota. API-key mode is not supported.
- **Copilot.** Needs a one-time setup. **Set up Copilot** lists its steps, then installs any missing official tools with winget and the Copilot SDK in a visible window. **Connect** then links the account the GitHub CLI is signed in to. **Sign in** changes the account. See the [Copilot setup guide](https://github.com/mahlernim/agent-quota-monitor-windows/blob/main/docs/copilot-setup.md).

After a successful CLI reading, the Antigravity account marked **CLI** replaces desktop-source cards. Stored selections are kept and identities are never merged.

Accounts remain separate by verified identity, even when email labels match. Direct Claude subscriptions are separate from Claude or GPT allowance supplied by Antigravity. Use a provider's own client to change accounts. The monitor never switches accounts automatically.

**Hide** (the crossed-out eye in Settings) stops monitoring an account without signing out or deleting credentials. **Restore hidden accounts** brings it back.

## Use the monitor

- Click a ring to choose the system-tray quota. It gets a blue outline and a small tray icon.
- Click the pin at a ring's top-right corner to pin or unpin it on the floating monitor. Filled violet means pinned.
- Right-click a ring to show it in the tray, pin it, move it, move its account, or copy its details.
- Drag a ring to reorder it within its account, or drag an account name to reorder accounts. Alt with the arrow keys does the same for a focused ring.
- Hover a ring for percentages, reset times, and pace. Hover a toolbar icon to see what it does.
- The floating monitor icon shows or hides the floating monitor. Drag it to move it, double-click it to show the main window, and right-click it for **Size** and **Opacity**.
- **Close** or **Esc** hides the main window to the tray. **Esc** also closes Settings. **Quit** exits and stops the quota reader if this app started it.

The thick outer ring shows quota remaining in the provider's color. The thin gray inner ring shows time remaining when the reset time is known. Percentage text turns amber when quota remaining falls below half of time remaining, and red below one quarter.

Rings show short names such as **Codex 5h**. The tray icon uses initials: CX, CL, AG (Antigravity Gemini), AC (Antigravity Claude and GPT), and CP. Change both under **Quota names** in Settings. Names never change colors or account grouping.

In Settings, each account takes one row. **Details** shows the last and next read, session times, and the reading source. The copy icon copies support details without credentials or your account label.

## Stale readings

A gray ring with a **stale** badge preserves the last value that could be read. It does not mean zero, and a missing window never means unlimited. A value read before its window reset is shown as unknown until a new reading arrives. A banner under the account explains the cause and offers one action.

- **Antigravity CLI missing and desktop app closed.** Use **Install CLI**, or open the desktop app.
- **Antigravity CLI needs sign-in.** **Copy command**, then run `agy -p /usage` in a terminal.
- **Codex didn't accept the session.** Open Codex, or use **Sign in**.
- **Claude Code session expired.** An open Claude Code renews its session only when used. Send any message in it, and the monitor resumes by itself.
- **Codex or Claude Code isn't installed.** Use **Install Codex** or **Install Claude Code**, then **Sign in**.
- **Copilot isn't set up or linked.** Use **Set up Copilot**, then **Connect**.
- **No network connection.** Use **Retry**, or wait. The monitor retries every five minutes and again when Windows reconnects or wakes from sleep.
- **Provider format changed.** Use **Check for updates**. With automatic checks on, the monitor checks once by itself.

When an official app closes, its recent reading stays live for up to ten minutes before turning stale. **Refresh** respects provider cooldowns.

## Start with Windows

Enable **Start with Windows (in the tray)** in Settings to launch at sign-in. It is off by default and needs no administrator rights. For a portable copy, disable this setting before moving or deleting the folder, then enable it again from the new location.

## Update or uninstall

The monitor checks GitHub's public releases API without credentials, shortly after launch and then at most every 3 hours. You can disable automatic checks, or use **Check now** in Settings.

When an update is available, the banner offers these choices.

- **Install update** (installed copies). Downloads the installer from the official Agent Quota Monitor release on GitHub, verifies its published SHA-256 checksum, closes the monitor, and opens the installer. Nothing runs if verification fails.
- **Download update**. Opens the release page. Portable users should quit the monitor and replace all extracted files.
- **Later** and **Skip this version**. Postpone the reminder for a day, or hide that release.

Settings, cached quota, and account bindings are preserved. Setup stops a quota reader left over from an earlier run before replacing files, and the app replaces a reader from another version when it starts.

Uninstall the installed version through Windows Installed apps. This removes app files, shortcuts, and this installation's startup entry. Monitor settings and provider accounts are preserved. To remove a portable copy, disable its startup setting, quit, and delete its folder.

## Troubleshooting

- **An account is missing.** Confirm that the official client has a valid session, wait a minute, and press **Refresh**.
- **An Antigravity CLI account is stale.** Wait for the next eligible read shown in Settings. If the CLI needs sign-in, run `agy` in a terminal, sign in, run `agy -p /usage`, and press **Refresh**.
- **The quota reader is unavailable.** Click the refresh icon, which retries the connection. If it stays unavailable, use **Quit** and reopen the monitor.
- **The portable copy does nothing.** Confirm that the complete archive was extracted.
- **Report a problem or suggest an idea.** Choose **Report it on GitHub** in Settings, or open the [issue page](https://github.com/mahlernim/agent-quota-monitor-windows/issues).

## Privacy and local data

The monitor reads official client sessions without taking over sign-in or renewal. It never sends an expired or copied token, sends no model prompts, and has no telemetry.

Quota settings and cached snapshots are encrypted for your Windows user under `%LOCALAPPDATA%\QuotaDashboard`. Non-secret update preferences are stored in your user registry. The quota reader communicates with the app over loopback only. It is intended for a trusted personal computer and does not isolate access from other local processes.

Provider interfaces can change and interrupt readings. See the [release page](https://github.com/mahlernim/agent-quota-monitor-windows/releases/tag/v0.3.3) for release notes and the [development guide](https://github.com/mahlernim/agent-quota-monitor-windows/blob/main/docs/development.md) for building from source.

MIT licensed. Independent project, not affiliated with or endorsed by any supported provider.
