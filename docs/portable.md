# Agent Quota Monitor Windows portable beta

Extract the entire ZIP into a folder you own, then open `agent-quota-monitor-windows.exe`. Keep the `_internal` directory beside the executable. No Python installation or administrator account is required to run the monitor.

This is an unsigned development beta for Windows x64. It is not a stable release or a signed installer. Broader clean-machine, sleep/resume, DPI, and accessibility testing is still pending.

The compact window opens on launch. Close hides it to the tray. Use the tray menu to open the dashboard, toggle the floating monitor, or quit. The dashboard is served only at http://127.0.0.1:8765. A second launch brings the existing instance forward.

## Connect your subscriptions

One supported provider is enough. Use Connections in the full dashboard to enable providers and launch supported official sign-in flows. Codex and direct Claude require their official coding clients. Antigravity requires its official application to be running and signed in. Existing sessions are reused without logging out or refreshing their credentials.

Copilot remains an optional advanced setup requiring Node.js, GitHub CLI, the official Copilot CLI, and the Copilot SDK. The portable monitor does not install those dependencies. See the repository's `Setup-Copilot.ps1` workflow.

## Preferences and removal

Preferences and cached quota readings remain encrypted for your Windows user under `%LOCALAPPDATA%\QuotaDashboard`. Moving or replacing the application folder preserves those preferences. Removing an account hides it from the monitor without revoking the provider session. Quit before replacing the portable folder. Delete the extracted application folder to remove the application. It installs no startup entry or service.

The tray has a transparent center and the labels CX, CL, GM, CG, and CP. Hover for the selected quota details. The full dashboard shows thick quota rings and thin time rings. Hover or focus a quota for reset and pace details. Stale and unknown data are marked explicitly.

Project and source are at https://github.com/mahlernim/agent-quota-monitor-windows.
