# Agent Quota Monitor for Windows

Keep an eye on your AI coding quotas, reset times, and how fast you are spending them, straight from the Windows tray.

**[Download Windows installer](https://github.com/mahlernim/agent-quota-monitor-windows/releases/download/v0.2.0-beta.6/agent-quota-monitor-windows-0.2.0-beta.6-setup-win-x64.exe)** · [Portable ZIP and release notes](https://github.com/mahlernim/agent-quota-monitor-windows/releases/tag/v0.2.0-beta.6)

![Main window with grouped quota rings](docs/images/main-window.png)

*Main window. Accounts and quota values shown are sample data.*

![Floating monitor strip pinned above other windows](docs/images/floating-monitor.png)

*Floating monitor. Accounts and quota values shown are sample data.*

## Quick start

1. Download and run the Windows x64 installer above.
2. Follow the setup wizard. No administrator rights are needed.
3. Open **Agent Quota Monitor** from the Start menu.
4. Open **Settings**, choose the providers you want, and connect any that are not detected yet.

The installer creates a Start menu shortcut and optionally a desktop shortcut. Python and .NET runtimes are bundled. You still need the official coding clients for the providers you use.

Prefer a portable app? Download the ZIP, extract **all** files into a permanent folder, and run `agent-quota-monitor-windows.exe`. Do not run it from inside the ZIP.

This beta is unsigned. Windows may display an unknown-publisher warning.

## Providers

Any combination works, and one provider is enough. The monitor reads quotas through the sessions your official clients already created, so a browser login by itself may not be recognized.

| Provider | How to connect | Notes |
| --- | --- | --- |
| OpenAI Codex | Settings, then **Sign in** | Launches the official Codex client sign-in |
| Anthropic Claude (direct) | Settings, then **Sign in** | Launches the official Claude Code sign-in |
| Google Antigravity | Sign in to the official `agy` CLI, or **Open official client** in Settings | The CLI account keeps refreshing with the desktop app closed |
| GitHub Copilot (optional) | Settings, then **Sign in** | Opens `gh auth login` in a console and your browser |

If an existing session is already recognized, you do not need to sign in again.

For Antigravity background monitoring, install and sign in to the [official CLI](https://antigravity.google/docs/cli/install), then run `agy -p /usage` once and press Refresh in the monitor. The account marked **CLI** can refresh while the desktop app is closed. Pin its quotas for continuous monitoring. Once the CLI has returned verified quotas, the monitor shows its card and suppresses old desktop-source cards. Existing CLI pins stay intact. Desktop monitoring remains a fallback for installations without a working CLI connection. CLI failures preserve the last reading as stale. API-key mode is not supported for subscription monitoring.

Copilot needs a one-time optional setup with PowerShell 7, Node and npm, Python, and the GitHub CLI, using `Setup-Copilot.ps1` from the source repository. See [Copilot setup](docs/copilot-setup.md).

To switch accounts, sign in to the other account through the provider's own client. The monitor follows whichever account the official client reports and never switches for you.

**Remove** hides an account and stops monitoring it. It does not log you out of the provider or touch your credentials. **Restore hidden accounts** brings it back.

## Everyday controls

- **Click a ring** in the main window to pick the quota shown in the system tray. The chosen one is labelled **Tray**.
- **Click a corner star** to pin or unpin a quota on the floating monitor.
- **Toolbar**, at the top right, has Refresh, Web, Floating, Settings, and Quit.
- **Hover a ring** for exact percentages, reset times, and pace details.
- **Close** hides the main window to the tray. **Quit** exits, and stops the quota reader if this app started it.
- **Web** opens the full local dashboard. Reordering accounts is done there with its edit controls, not by dragging rings in the native window.

Floating monitor: right-click it for **Size** (75, 100, 125, 150, 200%) and **Opacity** (35, 50, 70, 85, 100%). Drag it to move it, and double-click it to bring back the main window. Both preferences are remembered.

## Reading a quota ring

The thick outer ring is quota remaining, in a color that identifies the provider. The thin gray ring just inside it is time remaining in the current window, and it appears only when the reset timing is known. Percentages show at most one decimal.

The number turns **amber** when quota remaining falls below half of time remaining, and **red** below one quarter. For example, with 80% of the window left, amber starts under 40% quota and red under 20%. Without timing data there is no pace warning at all.

A gray, stale reading means the value could not be refreshed. It does not mean zero, and a missing window never means unlimited.

## Start with Windows

Settings has an optional **Start with Windows (in the tray)** switch. It is off by default and needs no administrator rights. Because the app is portable, keep its folder in a permanent place. If you need to move or delete the folder, turn the switch off first, then turn it on again from the new location.

## Updates

The monitor checks GitHub shortly after startup and at most once every 24 hours. A quiet banner offers **Download update**, **Later**, **Skip this version**, and **Release notes**. It never interrupts your work or installs anything automatically.

Later postpones reminders for a day. Skip this version hides that release across restarts. **Check for updates** in Settings lets you reconsider skipped releases. Settings also lets you disable automatic checks. Stable builds ignore betas; beta builds can receive newer betas or stable releases.

Download update opens the official release page. Quit the monitor, run the new installer, and keep the same installation folder. Settings and account bindings are preserved. Portable users should quit before replacing their extracted files.

Uninstall through Windows Installed apps. App files, shortcuts, and this installation's startup entry are removed. Monitor settings and vendor accounts are preserved.

## Troubleshooting

- **An account is missing.** Sign in through the provider's own client, then press Refresh. A browser-only login may not create the session the monitor reads.
- **Everything looks stale.** Press Refresh. If it persists, confirm the provider's client still has a valid session.
- **Copilot shows nothing.** Complete the one-time setup in [Copilot setup](docs/copilot-setup.md), then use Copilot **Sign in** and finish the prompts in the console window that opens.
- **Nothing happens when launching.** Confirm you extracted the whole ZIP, including the `backend` folder, next to the executable.
- **Values seem frozen after a move.** Run only one monitor at a time against the same account cache.

## Privacy

- Credentials stay with the official clients. The monitor reads local sessions and never logs you out, switches accounts, or routes traffic through a proxy.
- Settings and cached snapshots are encrypted for your Windows user account.
- The local server listens on loopback only.
- No telemetry, automatic public uploads, or prompts sent to a model to estimate quotas.

Provider interfaces are internal to those products and can change without notice, which may interrupt readings.

## More

- [Windows details](docs/windows.md)
- [Provider interfaces](docs/provider-evidence.md)
- [Building and contributing](docs/development.md)
- [Third-party notices](THIRD-PARTY-NOTICES.md)

MIT licensed. Independent project, not affiliated with or endorsed by any of the supported providers.
