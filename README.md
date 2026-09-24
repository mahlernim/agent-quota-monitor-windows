# Agent Quota Monitor for Windows

See how much of your AI coding quota is left, when each window resets, and whether you are spending faster than time is passing. It lives in the Windows tray and never sends a prompt to find out.

**[Download the Windows installer](https://github.com/mahlernim/agent-quota-monitor-windows/releases/download/v0.3.3/agent-quota-monitor-windows-0.3.3-setup-win-x64.exe)** · [Portable ZIP](https://github.com/mahlernim/agent-quota-monitor-windows/releases/download/v0.3.3/agent-quota-monitor-windows-0.3.3-win-x64.zip) · [Release notes](https://github.com/mahlernim/agent-quota-monitor-windows/releases/tag/v0.3.3)

![Main window with grouped quota rings](docs/images/main-window.png)

*Main window. Accounts and quota values shown are sample data.*

![Floating monitor strip pinned above other windows](docs/images/floating-monitor.png)

*Floating monitor. Accounts and quota values shown are sample data.*

## Why use it

- **One glance for every subscription.** OpenAI Codex, Anthropic Claude, Google Antigravity, and GitHub Copilot quotas appear side by side.
- **Pace, not just percentages.** Each ring compares quota left with time left in its window and warns you in amber or red when you are ahead of pace.
- **Uses the sign-ins you already have.** Quotas are read through the official clients' existing sessions. The monitor never takes over sign-in, renews tokens, or switches accounts.
- **Honest when data is old.** Values that could not be refreshed stay visible in gray with the reason, and values from before a reset are shown as unknown.
- **Fixes where you look.** When an account has a problem, a short banner under it explains why and offers the one action that helps.
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

1. Download and run the [Windows x64 installer](https://github.com/mahlernim/agent-quota-monitor-windows/releases/download/v0.3.3/agent-quota-monitor-windows-0.3.3-setup-win-x64.exe). No administrator rights are needed.
2. Open **Agent Quota Monitor** from the Start menu.
3. Open **Settings** (the gear icon) and tick the providers you use. Changes save right away.

Python and .NET runtimes are bundled. You still need the official coding client for each provider you use.

**Portable copy.** Download the ZIP, extract **all** files into a permanent folder, and run `agent-quota-monitor-windows.exe`. Do not run it from inside the ZIP. Keep the `backend` folder beside the executable.

The installer and application are unsigned, so Windows may show an unknown-publisher warning.

## Connect your providers

Any combination works, and one provider is enough. If an official client is already signed in, its account appears within a minute without any action.

| Provider | Official source the monitor reads | How to connect |
| --- | --- | --- |
| OpenAI Codex | Codex app or CLI session | Settings, then **Sign in**, or **Install Codex** if it's missing |
| Anthropic Claude (direct subscription) | Claude Code session | Settings, then **Sign in**, or **Install Claude Code** if it's missing |
| Google Antigravity | Antigravity CLI (`agy`), or the running desktop app | Settings, then **Install CLI** or **Open desktop app** |
| GitHub Copilot (optional) | GitHub CLI and Copilot SDK | Settings, then **Set up Copilot** and **Connect**. See [Copilot setup](docs/copilot-setup.md) |

A browser-only login may not create the session the monitor reads. Sign in through the official client instead.

If Codex or Claude Code isn't installed, Settings and the main window offer its official installer in place of **Sign in**. The confirmation shows the exact command, and it runs in a visible PowerShell window only after you choose OK. Clients installed while the monitor runs are found within a minute.

The official clients renew their own sessions while you use them. If a session lapses, open that client (Codex, or Claude Code) and the monitor resumes by itself. Settings shows when a Claude Code session expires and when a Codex session was last renewed.

### Anthropic Claude

The monitor reads the sign-in saved by **Claude Code**, Anthropic's command-line tool. Signing in to the Claude desktop app or claude.ai alone isn't enough, because neither saves a session the monitor can read. Claude Code needs a Claude Pro, Max, Team, or Enterprise plan.

1. In Settings, tick **Anthropic Claude**. If Claude Code is missing, choose **Install Claude Code** and confirm. The official installer runs in a PowerShell window.
2. When it finishes, choose **Sign in**. It can take up to a minute to replace the install button.
3. A Claude Code window and your browser open. Sign in in the browser. If the browser shows a code instead of returning, paste it into the Claude Code window.

The account appears once its quota has been read.

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

**Hide** (the crossed-out eye in Settings) stops monitoring an account. It does not sign you out or touch credentials. **Restore hidden accounts** brings it back.

## Read a quota ring

The thick outer ring is quota remaining, drawn in the provider's color. The thin gray ring inside it is time remaining in the current window. It appears only when the reset time is known.

The percentage turns **amber** when quota remaining falls below half of time remaining, and **red** below one quarter. For example, with 80% of the window left, amber starts under 40% quota and red under 20%. Without timing data there is no pace warning.

Hover any ring for exact values, the reset time, pace, and, when something is wrong, what to do about it.

## Everyday use

- **Click a ring** to show that quota in the system tray. The chosen ring gets a blue outline and a small tray icon.
- **Click the pin** at a ring's top-right corner to pin or unpin it on the floating monitor. Filled violet means pinned.
- **Right-click a ring** to show it in the tray, pin it, move it, move its account, or copy its details.
- **Drag a ring** to reorder it within its account, or **drag an account name** to reorder accounts. With the keyboard, press Alt with the arrow keys on a focused ring. Pins and the tray choice follow their rings.
- **Toolbar icons.** Refresh, floating monitor, Settings, and Quit. Hover an icon to see what it does.
- **Floating monitor.** Drag to move it, double-click to open the main window, and right-click for **Size** (75 to 200%) and **Opacity** (35 to 100%). Its size, opacity, and position are remembered.
- **Close** or **Esc** hides the main window to the tray. **Quit** exits and stops the quota reader it started.

### Names on rings

Rings show short names such as **Codex 5h** or **Gemini 7d**. The tray icon uses initials: CX (Codex), CL (Claude), AG (Antigravity Gemini), AC (Antigravity Claude and GPT), and CP (Copilot). Change both under **Quota names** in Settings. Long names are narrowed to fit, and the floating monitor falls back to initials when a name doesn't fit. Names never change colors or which account a ring belongs to.

### Settings

Each account takes one row with its status. **Details** shows the last and next read, session times, and the reading source. The copy icon copies support details without credentials or your account label. To reorder accounts there, choose **Edit order**, drag rows or press Alt with Up and Down, then **Save order**. **Esc** closes Settings, or discards an unsaved order first.

## Stale readings and what they mean

A gray ring with a **stale** badge shows the last value the monitor could read. It never means zero, and a missing window never means unlimited. A banner under the account explains the cause and offers the fix.

| Banner | What it means | Action |
| --- | --- | --- |
| The Antigravity CLI isn't installed and the desktop app is closed | No Antigravity source is running | **Install CLI**, or open the desktop app |
| The Antigravity CLI needs sign-in | The CLI session lapsed | **Copy command**, then run `agy -p /usage` in a terminal |
| Codex didn't accept the saved session | The Codex session lapsed | Open Codex, or **Sign in** |
| Your Claude Code session expired | Claude Code renews its session only when used, even if it's already open | Send any message in Claude Code. The monitor resumes by itself |
| Codex or Claude Code isn't installed | No official client was found | **Install Codex** or **Install Claude Code** |
| GitHub Copilot isn't set up yet, or Copilot is set up | Setup is incomplete, or no GitHub account is linked | **Set up Copilot**, then **Connect** |
| No network connection | Requests couldn't reach the provider | **Retry**, or wait. The monitor also retries when Windows reconnects or wakes |
| The provider changed its data format | A provider update changed its data | **Check for updates** |
| Reset since the last read (ring tooltip) | The window reset after the value was read | Wait for the next reading. The old value is hidden |

When an official app closes, its recent reading stays live until it is ten minutes old, then turns stale. **Refresh** always respects provider cooldowns. Settings shows when each account can be read again.

## Start with Windows

Settings has an optional **Start with Windows (in the tray)** switch. It is off by default and needs no administrator rights. For a portable copy, turn it off before moving the folder, then turn it on again from the new location.

## Updates

The monitor checks GitHub for a new release shortly after startup and then at most every 3 hours. When a release is available, a banner appears in the main window.

- **Install update** (installed copies only) downloads the installer from the official Agent Quota Monitor release on GitHub, verifies it against the published SHA-256 checksum, closes the monitor, and opens the installer. Nothing is installed without your confirmation, and nothing runs if verification fails.
- **Download update** opens the release page instead. Portable copies always use this. Quit the monitor before replacing the extracted files.
- **Later** postpones the reminder for a day. **Skip this version** hides that release.

**Check now** in Settings checks immediately. You can turn off automatic checks there. Settings, cached quota, and account bindings are kept across updates.

To uninstall, use Windows **Installed apps**. App files, shortcuts, and the startup entry are removed. Monitor settings and provider accounts are kept.

## Troubleshooting

- **An account is missing.** Sign in through the provider's official client, wait a minute, then press **Refresh**.
- **Antigravity goes stale when the desktop app closes.** Install the Antigravity CLI from Settings. See [Google Antigravity](#google-antigravity).
- **An Antigravity CLI account is stale.** Wait for the next eligible read shown in Settings, then press **Refresh**. If the CLI needs sign-in, run `agy` in a terminal, sign in, run `agy -p /usage`, and refresh again.
- **Claude says the session expired, but Claude Code is open.** An open Claude Code renews its session only when it's used. Send any message in it, and the monitor resumes within a minute. Use **Sign in** only if Claude Code itself says you're signed out.
- **Codex says the session wasn't accepted.** Open the Codex app or CLI. If that doesn't help, use **Sign in**.
- **Codex Sign in stops right away.** The Codex client exited before sign-in started. Update it with `npm install -g @openai/codex@latest`, check `~/.codex/config.toml`, or sign in through the Codex app.
- **Claude says the read was rejected.** Check `claude auth status`, open Claude Code to let it renew its session, and press **Refresh**. If it still fails, sign in again through Claude Code.
- **Copilot shows nothing.** Choose **Set up Copilot** in Settings, then **Connect**. See [Copilot setup](docs/copilot-setup.md).
- **The quota reader is unavailable.** Click the refresh icon, which retries the connection. If it stays unavailable, use **Quit** and reopen the monitor. After an upgrade, the monitor replaces a reader left over from the previous version automatically.
- **The portable copy does nothing when launched.** Make sure the whole ZIP was extracted, including the `backend` folder.
- **An update could not be verified.** Nothing was installed. Try again later or use **Download update**.
- **Something else is wrong, or you have an idea.** Choose **Report it on GitHub** in Settings, or open the [issue page](https://github.com/mahlernim/agent-quota-monitor-windows/issues). Include the copied account details rather than screenshots of your quota.

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
