# Agent Quota Monitor Windows

A Windows tray utility for AI subscription quotas, reset times, and consumption pace.

## Status

Early development beta. Source and a portable Windows build workflow are available for testing, but there is no signed installer or stable release yet. Supports any enabled combination of OpenAI Codex, direct Anthropic Claude, Google Antigravity, and GitHub Copilot. One provider is enough.

## Views

- A tray donut for a selected account, quota group, and window.
- A compact native main window with grouped quota donuts.
- An optional frameless floating strip of multiple pinned quota donuts, with adjustable opacity.
- A full local dashboard with account ordering, removal, connection actions, and provider selection.
- Compact paired donuts with a thick quota ring and a thin time ring for valid five-hour and weekly windows. Remaining mode compares quota remaining with time remaining. Used mode compares quota used with time elapsed. Pace compares quota with an even consumption schedule, not a usage prediction.

Hover or focus a quota to see precise percentages, reset times, and pace details. Quota values and stale status stay visible without opening details.

Unknown and stale readings are explicit. Direct Claude and Claude supplied through Antigravity are separate quotas. Missing windows never imply unlimited usage.

## Run

The primary Windows app uses WPF vector graphics. Extract the entire portable ZIP and run `agent-quota-monitor-windows.exe`. Python and .NET runtimes are bundled. See [Windows instructions](docs/windows.md).

To build from source, install .NET 8 SDK and 64-bit Python 3.12, then run `./Build-Windows.ps1`. For development, use `./Start-Windows.ps1`. The earlier Tk shell remains available through [legacy desktop instructions](docs/desktop.md).

Normal colors identify agents using blue-green hues. Yellow means quota remaining is below half of time remaining, and red means it is below one quarter. Missing or stale timing never triggers a pace warning. Percentages show at most one decimal.

For the browser-only version, run `./Start-Dashboard.ps1` and open http://127.0.0.1:8765/. The backend uses Python standard-library modules. Desktop dependencies are listed in requirements-desktop.txt.

Only one monitor process should run against an account cache. Stop the browser-only process before starting the desktop version. The desktop owns its local server and polling. Nothing starts with Windows automatically.

## Provider connections

Enable only the providers you want in Connections. A first unconfigured launch discovers existing supported sessions without adding empty cards for missing providers. Explicitly enabled providers may show connection guidance when a session is unavailable.

Codex and Claude use existing official coding-client sessions. Dashboard sign-in launches the official client and verifies a fresh quota read. Antigravity uses its running local service, whose provider account ID is not reported. Copilot uses its official SDK quota interface and requires its separate optional setup. See [provider interfaces](docs/provider-evidence.md).

## Privacy and ownership

- Credentials stay with official clients. No shared refresh-token renewal, logout, proxy routing, paid API setup, or automatic account switching.
- Normalized snapshots and preferences are encrypted with Windows DPAPI under `%LOCALAPPDATA%/QuotaDashboard`. This legacy directory name is retained to preserve existing data.
- No quota-reading inference prompts. Polling honors cooldowns and provider retry delays.
- The web server binds to 127.0.0.1 and checks Host, Origin, and same-origin action headers. It is not an isolation boundary against other software running as the same Windows user.
- Removal hides an account and stops future polling without revoking its credentials. Disabling a provider preserves its cached data and display preferences.
- No telemetry or automatic public uploads.

## Validation

```powershell
python -m unittest discover -s tests -v
node --check web/app.js
node tests/test_pace.mjs
```

Tests use synthetic fixtures. Live verification and Windows UI checks are separate from automated tests. Sleep/resume, DPI, screen-reader support, installer distribution, and clean-machine behavior still need broader testing before a stable release.

## Contributing

Issues and pull requests are welcome. Do not attach credentials, authorization URLs, provider files, personal account screenshots, or private quota snapshots. Include application version, Windows version, enabled provider names, and sanitized error categories.

MIT licensed. Independent software, not affiliated with or endorsed by the supported providers. See THIRD-PARTY-NOTICES.md for research references and license notices.
