# Agent Quota Monitor for Windows

Monitor your AI coding quotas from the Windows tray, main window, or compact floating monitor.

## Install and launch

Use the [Windows installer](https://github.com/mahlernim/agent-quota-monitor-windows/releases/download/v0.2.3/agent-quota-monitor-windows-0.2.3-setup-win-x64.exe) for a Start menu shortcut and optional desktop shortcut. Installation is per user and needs no administrator rights.

For the [portable ZIP](https://github.com/mahlernim/agent-quota-monitor-windows/releases/download/v0.2.3/agent-quota-monitor-windows-0.2.3-win-x64.zip), extract the entire archive into a permanent folder and run `agent-quota-monitor-windows.exe`. Keep the `backend` folder and all runtime files beside the executable. Python and .NET runtimes are included.

The installer and application are unsigned. Windows may display an unknown-publisher warning.

## Connect your providers

Open **Settings**, select your providers, and click **Save monitored providers**. Existing sessions from official coding clients are detected where available. A browser sign-in alone may not be enough.

- **Codex and Claude** use **Sign in** to launch their official coding-client sign-in commands. Install the official clients first.
- **Antigravity** uses **Open official client** for desktop sign-in. For monitoring while the desktop app is closed, install and sign in to the [official `agy` CLI](https://antigravity.google/docs/cli/install), run `agy -p /usage` once, and press **Refresh**. Pin quotas on the account marked **CLI**. After a successful CLI reading, that account replaces desktop-source cards in the display. Stored selections are retained, and identities are not merged. API-key mode is not supported for subscription monitoring.
- **Copilot** needs the optional [one-time setup](https://github.com/mahlernim/agent-quota-monitor-windows/blob/main/docs/copilot-setup.md). Its **Sign in** button opens the official GitHub CLI and browser. Complete the prompts in the console and leave it open until sign-in finishes.

Accounts remain separate by verified identity. Direct Claude subscriptions are separate from Claude or GPT allowance supplied by Antigravity. Use a provider's own client to change accounts. The monitor never switches accounts automatically.

**Remove** hides an account and stops monitoring it without signing out or deleting credentials. **Restore hidden accounts** brings it back.

## Use the monitor

- Click a ring in the main window to choose the system-tray quota. Its label shows **Tray**.
- Click the pin at the top-right corner of a ring to pin or unpin that quota on the floating monitor. Filled violet means pinned. A hollow slate pin means unpinned.
- Hover a ring for percentages, reset times, and pace details.
- Use **Floating** to show or hide the floating monitor. Drag it to move it, and double-click it to show the main window.
- Right-click the floating monitor for **Size** (75, 100, 125, 150, 200%) and **Opacity** (35, 50, 70, 85, 100%). Size, opacity, and position are remembered.
- Choose **Reorder** in the main window to move accounts up or down and rings left or right within their account. Each move saves immediately and changes the floating monitor order. Choose **Done reordering** when finished.
- **Close** hides the main window to the tray. **Quit** exits and stops the quota reader if this app started it.

Settings shows each account's identity, reading source, and retry status. Account controls and quota details are available in the Windows app.

The thick outer ring shows quota remaining in the provider's color. The thin gray inner ring shows time remaining when the reset timing is known. Percentages show at most one decimal.

Percentage text turns amber when quota remaining falls below half of time remaining, and red below one quarter. With 80% of the window left, amber starts below 40% quota and red below 20%. Missing timing data produces no pace warning.

A stale reading preserves the last available value. It does not mean zero, and a missing window never means unlimited. A passed reset time does not confirm replenishment until the provider returns a new reading. **Refresh** respects provider cooldowns. Settings shows when an account can be retried.

## Start with Windows

Enable **Start with Windows (in the tray)** in Settings to launch at sign-in. It is off by default and needs no administrator rights. For a portable installation, disable this setting before moving or deleting the folder, then enable it from the new location. Windows Task Manager can also disable the startup entry.

## Update or uninstall

Automatic checks use GitHub's public releases API without account credentials. They run shortly after launch, with at most one attempt every 24 hours, including failed attempts. Automatic failures are quiet. **Check for updates** in Settings checks immediately and reports the result. You can disable automatic checks there.

The update banner offers **Download update**, **Later**, **Skip this version**, and **Release notes**. Download and release notes open the official release page. Later postpones reminders for a day. Skip this version persists across restarts, and a manual check reconsiders skipped releases. This stable release offers stable updates only. Nothing is installed automatically.

Quit before updating. Run the new installer into the same folder, or replace all extracted portable files. Settings and account bindings are preserved.

Uninstall the installed version through Windows Installed apps. This removes app files, shortcuts, and this installation's startup entry. Monitor settings and vendor accounts are preserved. To remove a portable copy, disable its startup setting, quit, and delete its extracted folder.

## Privacy and local data

The monitor reads official client sessions without taking over sign-in or renewal. It sends no model prompts to estimate quotas and has no telemetry or automatic public uploads.

Quota settings and cached snapshots are encrypted for your Windows user under `%LOCALAPPDATA%\QuotaDashboard`. Non-secret update preferences are stored in your user registry. The quota reader communicates with the Windows app over loopback only. It is intended for a trusted personal computer and does not isolate access from other local processes.

Provider interfaces can change and interrupt readings. If an account is missing or stale, confirm that its official client has a valid session, then press **Refresh**. For launch failures with the portable version, confirm that the complete archive was extracted.

If the quota reader is unavailable, click **Retry connection** in the toolbar. This reconnects or starts the reader without changing accounts. If it stays unavailable, use **Quit** and reopen the monitor. Closing the window only hides it to the tray.

See the [project README](https://github.com/mahlernim/agent-quota-monitor-windows#readme) for screenshots and the [release page](https://github.com/mahlernim/agent-quota-monitor-windows/releases/tag/v0.2.3) for downloads and release notes. Source contributors can use the [development guide](https://github.com/mahlernim/agent-quota-monitor-windows/blob/main/docs/development.md).

MIT licensed. Independent project, not affiliated with or endorsed by any supported provider.
