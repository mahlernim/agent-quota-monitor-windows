# Agent Quota Monitor for Windows

Monitor your AI coding quotas from the Windows tray, main window, or compact floating monitor. This guide ships with the portable ZIP. The [project page](https://github.com/mahlernim/agent-quota-monitor-windows#readme) has screenshots and the latest version of this guide.

## Install and launch

Requires Windows 10 or 11 on x64 and an eligible account with each provider you enable.

Use the [Windows installer](https://github.com/mahlernim/agent-quota-monitor-windows/releases/download/v0.4.1/agent-quota-monitor-windows-0.4.1-setup-win-x64.exe) for a Start menu shortcut and an optional desktop shortcut. Installation is per user and needs no administrator rights.

For the [portable ZIP](https://github.com/mahlernim/agent-quota-monitor-windows/releases/download/v0.4.1/agent-quota-monitor-windows-0.4.1-win-x64.zip), extract the entire archive into a permanent folder and run `agent-quota-monitor-windows.exe`. Keep the `backend` folder and all runtime files beside the executable. Python and .NET runtimes are included.

The installer and application are unsigned. Windows may display an unknown-publisher warning.

## Connect your providers

Open **Settings** (the gear icon) and select only the providers you use. Changes save right away. Existing official sessions are normally discovered within a minute. A first reading may take longer if the provider is unavailable or a cooldown is active. A browser sign-in alone may not be enough.

- **Codex and Claude.** **Sign in** launches the official client's sign-in. If a client is missing, **Install Codex** or **Install Claude Code** shows the official install command and runs it in a visible PowerShell window after you confirm. The clients manage their own authentication and session renewal. If a session lapses, follow the account banner to sign in or open the official client. Claude's **Sign in** works without sending a message.
- **Claude specifically.** The monitor reads the sign-in saved by Claude Code, the command-line tool. The Claude desktop app or claude.ai alone isn't enough. Claude Code needs a Pro, Max, Team, or Enterprise plan. **Sign in** opens a Claude Code window next to the browser. If the browser shows a code instead of returning, paste it into that window.
- **Antigravity.** The official Antigravity CLI (`agy`) lets the monitor read quota while the desktop app is closed. When `agy` is missing, **Install CLI** shows Google's exact install command, `irm https://antigravity.google/cli/install.ps1 | iex`, and runs it in a visible PowerShell window only after you confirm. That window then runs `agy -p /usage` so you can sign in. You can also follow the [official instructions](https://antigravity.google/docs/cli/install) yourself. **Open desktop app** opens the Antigravity desktop app, which can supply readings while it runs. Gemini CLI does not report Antigravity quota. API-key mode is not supported.
- **Copilot.** Needs a one-time setup. **Set up Copilot** lists its steps, then installs any missing official tools with winget and the Copilot SDK in a visible window. **Connect** then links the account the GitHub CLI is signed in to. **Sign in** changes the account. See the [Copilot setup guide](https://github.com/mahlernim/agent-quota-monitor-windows/blob/main/docs/copilot-setup.md).

### Claude quota reads

With Claude Code 2.1.281 or a newer 2.x version, the monitor uses its built-in `/usage` command. Claude Code handles authentication and renewal during the read, with no model message. Settings shows **Claude Code CLI (session managed by CLI)**. This source supplies the session and all-model weekly windows, but not model-specific windows.

The default Claude Code subscription login is used. Environment variables for API keys, alternate endpoints, and custom configuration directories do not redirect the monitor's reads. Your environment and client settings stay unchanged.

Older clients, or clients whose version cannot be checked, use the saved-session reader and show its expiry in Settings. Failed version checks retry after five minutes. If the session needs attention, use **Sign in**. No message or available quota is needed, and signing in does not reset quota.

### Antigravity sources and account identity

The monitor displays one Antigravity source at a time. CLI is preferred. A fresh desktop reading replaces the CLI card during a CLI failure, and CLI returns when it recovers. If the desktop source is also unavailable, only the CLI error card remains. Each source retains its own identity and saved pins. Provider cooldowns and hidden-account choices are respected.

An Antigravity window explicitly reported as **Disabled** keeps its ring and saved pin. If a fresh weekly reading in the same account and model group confirms 0% remaining, its disabled five-hour ring shows **0%** and its tooltip explains **0% available · Weekly limit reached**. Any countdown is labeled **Weekly reset**, with no five-hour pace ring. This describes baseline quota only. AI Credit overages may still allow use. Otherwise, Disabled shows no percentage or countdown. It returns to its normal display when the provider reports an active window.

Accounts remain separate by verified identity, even when email labels match. Direct Claude subscriptions are separate from Claude or GPT allowance supplied by Antigravity. Use a provider's own client to change accounts. The monitor never switches accounts automatically.

**Hide** (the crossed-out eye in Settings) stops monitoring an account without signing out or deleting credentials. **Restore hidden accounts** brings it back.

## Use the monitor

- Click a ring to choose the system-tray quota. It gets a blue outline and a small tray icon.
- Click the pin at a ring's top-right corner to pin or unpin it on the floating monitor. Filled violet means pinned.
- Right-click a ring to show it in the tray, pin it, move it, move its account, or copy its details.
- Drag a ring to reorder it within its account, or drag an account name to reorder accounts. Alt with the arrow keys does the same for a focused ring.
- Hover a ring for percentages, reset times, and pace. Hover a toolbar icon to see what it does.
- **Refresh** shows a spinning icon and **Refreshing…** while eligible accounts are read. A brief result reports updated accounts, no new readings, or a failure. Repeated clicks are disabled until it finishes, and provider cooldowns still apply.
- The floating monitor icon shows or hides the floating monitor. Drag it to move it, double-click it to show the main window, and right-click it for **Size** and **Opacity**.
- **Close** or **Esc** hides the main window to the tray. **Esc** also closes Settings. **Quit** exits and stops the quota reader if this app started it.

The thick outer ring shows quota remaining in the provider's color. The thin gray inner ring shows time remaining when the reset time is known. Percentage text turns amber when quota remaining falls below half of time remaining, and red below one quarter.

Rings show short names such as **Codex 5h**. The default tray initials are CX for Codex, CL for Claude, AG for Antigravity Gemini, AC for Antigravity Claude and GPT, and CP for Copilot. Change both under **Quota names** in Settings. Names never change colors or account grouping.

In Settings, each account takes one row. **Details** shows the last and next read, session times, and the reading source. The copy icon copies support details without credentials or your account label.

## Stale readings

A gray ring with a **stale** badge preserves the last value that could be read. It does not mean zero, and a missing window never means unlimited. A value read before its window reset is shown as unknown until a new reading arrives. A banner under the account explains the cause and offers one action.

- **Antigravity CLI missing and desktop app closed.** Use **Install CLI**, or open the desktop app.
- **Antigravity CLI needs sign-in.** **Copy command**, then run `agy -p /usage` in a terminal.
- **Codex didn't accept the session.** Open Codex, or use **Sign in**.
- **Claude Code session expired.** Choose **Sign in** on the account banner or in Settings and complete the browser login. No message is needed, even when your five-hour quota is exhausted. Signing in does not reset your quota. The monitor resumes by itself.
- **Codex or Claude Code isn't installed.** Use **Install Codex** or **Install Claude Code**, then **Sign in**.
- **Copilot isn't set up or linked.** Use **Set up Copilot**, then **Connect**.
- **No network connection.** Use **Retry**, or wait. The monitor retries every five minutes and again when Windows reconnects or wakes from sleep.
- **The monitor could not interpret the provider response.** Use **Check for updates**. With automatic checks on, the monitor checks once by itself.
- **Reading again after the monitor restarted.** An unreadable response saved before a restart or update is being read again. Wait for the next reading. If it fails again, the unreadable-response banner returns.

If a source becomes unavailable, its cached reading becomes stale. When discovery no longer finds an account, a recent reading can remain live for up to ten minutes. Closing a desktop app does not stop monitoring when its CLI or saved session can still supply quota.

**Refresh** respects provider cooldowns. An unchanged percentage can still be a successful refresh. For **No new readings**, check the next eligible read and any error in **Settings → Details**. Retry schedules are kept across restarts and updates, except that an unreadable-response error is read again as soon as the monitor starts.

## Optional quota window activation

Activation stays off on fresh installs and on the update that first adds it. In **Settings → Quota window activation**, explicitly choose each account and window, enable automatic activation, and click **Save activation settings**. Later updates preserve choices and attempt records. Newly discovered accounts and newly supported windows remain off.

Choose Codex weekly, direct Claude five-hour or weekly, and Antigravity Gemini or Claude/GPT five-hour or weekly independently. New windows remain unselected after an update. A short subscription prompt starts an eligible countdown after 30 minutes of consecutive fresh inactive readings. The delay can be 60 or 120 minutes. If both windows in a group are idle, one prompt can start both and is recorded once.

Fixed running countdowns never need activation, even at full quota. Codex and Antigravity inactivity requires full quota and a deadline that moves with time across fresh provider reads. Unknown or stale data and exhausted allowance prevent activation. The app waits for two new countdown readings after a prompt and never retries uncertain delivery. A prompt that is never confirmed before its window would have ended is recorded as expired, and a new inactivity period is required. Durable reservations prevent duplicates across restarts and updates. The limit is five prompts per account and quota group per 24 hours.

The official clients supply subscription authentication. Codex uses GPT-6 Luna with low reasoning, read-only ephemeral execution, and ignored user configuration. Claude uses Haiku with tools and hooks disabled and requires extra usage off. Antigravity uses Flash for Gemini and GPT-OSS for Claude/GPT, in plan mode without automatic permission approval. Configured Antigravity CLI plugins or MCP servers pause activation. Keep clients current. Even a short prompt can consume substantial CLI context. See [activation behavior](window-activation.md) for details.

After three hours of observed inactivity, a supported account can receive a quiet suggestion in the main window. **Configure activation** opens Settings without enabling anything. **Later** snoozes for one week. **Don't show again** persists. Suggestions can also be disabled in Settings. Sleep or stale readings restart the observation delay. The app must remain running to observe and activate windows.

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
- **Claude Code could not read quota.** Open Claude Code and run `/usage` to check its connection. The monitor keeps the last reading marked stale and retries. A failed read alone does not mean you need to sign in again.
- **Claude keeps asking for sign-in.** Check the source in Settings. Update an older Claude Code client to use CLI-managed reads. If the official client itself needs a login, use **Sign in** and finish the browser flow without sending a message.
- **The quota reader is unavailable.** Click the refresh icon, which retries the connection. If it stays unavailable, use **Quit** and reopen the monitor.
- **The portable copy does nothing.** Confirm that the complete archive was extracted.
- **Report a problem or suggest an idea.** Choose **Report it on GitHub** in Settings, or open the [issue page](https://github.com/mahlernim/agent-quota-monitor-windows/issues).

## Privacy and local data

Authentication stays with the official clients. Depending on the source, the monitor invokes a quota-only client command or reads an existing session for a quota request. It does not maintain a separate login, refresh tokens itself, or store raw credentials in its own data. Official clients may renew their sessions when invoked. Tokens, cookies, and authorization headers are excluded from monitor logs and copied support details. Quota checks send no model prompts. Optional activation sends a short subscription prompt only after explicit opt-in. It has no paid/API fallback. There is no telemetry.

Quota settings and cached snapshots are encrypted for your Windows user under `%LOCALAPPDATA%\QuotaDashboard`. Non-secret update preferences are stored in your user registry. The quota reader communicates with the app over loopback only. It is intended for a trusted personal computer and does not isolate access from other local processes.

Activation choices and attempt receipts are encrypted separately in `activation.dpapi` in that folder. Receipts contain stable account IDs, timestamps, outcomes, and numeric token counts. They do not contain response text or credentials. Updates preserve this file. Do not delete it to retry an uncertain prompt.

Provider interfaces can change and interrupt readings. See the [release page](https://github.com/mahlernim/agent-quota-monitor-windows/releases/tag/v0.4.1) for release notes and the [development guide](https://github.com/mahlernim/agent-quota-monitor-windows/blob/main/docs/development.md) for building from source.

MIT licensed. Independent project, not affiliated with or endorsed by any supported provider.
