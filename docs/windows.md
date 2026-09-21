# Agent Quota Monitor Windows

The recommended download is the per-user setup executable. It creates a Start menu shortcut and an optional desktop shortcut without administrator rights. Quit the monitor before updating into the same folder. Windows Installed apps provides removal of app files and this installation's startup entry. Monitor settings and vendor credentials are preserved.

Settings offers Check for updates and an automatic-check toggle. Checks contact the public GitHub releases API without account credentials, shortly after launch and at most daily. Failures are silent unless you requested the check. The main-window banner opens the official release page. Later snoozes for a day; Skip this version survives restarts. Manual checks reconsider skipped releases. Stable builds ignore prereleases. Nothing is installed automatically. Non-secret update preferences are stored in your Windows user registry; quota snapshots remain encrypted separately.

This beta uses WPF for the native windows and vector graphics. Extract the entire archive and run `agent-quota-monitor-windows.exe`. The package includes the .NET runtime and a separate local quota reader, so Python and .NET do not need to be installed separately. Keep the `backend` folder and all runtime files beside the executable.

Click a donut to choose the tray quota. Corner stars choose the quotas in the compact floating strip. Hover for details. Drag the floating strip to reposition it, and right-click for its menu and opacity. Close hides the main window. Quit stops the reader if this application started it.

Outer ring colors identify agents, including burnt orange for direct Claude. Yellow percentage text means quota remaining is below half the remaining portion of the time window. Red percentage text means it is below one quarter. Missing timing data never generates a projected warning. Percentages show at most one decimal.

Accounts opens native provider, sign-in, removal, and restore controls. Codex and Claude launch their official coding-client sign-in commands. Antigravity opens its official desktop application, where account selection takes place. The monitor verifies returned account identity. Removal hides the account and stops monitoring without logging out or revoking credentials. Copilot still requires its optional official CLI/SDK setup. Web opens the full dashboard, including account ordering.

Settings and cache are encrypted for your Windows user under `%LOCALAPPDATA%\QuotaDashboard`. Existing pinned selections are migrated automatically. The monitor binds only to 127.0.0.1 and has no startup service or telemetry.

Antigravity can also refresh through the official `agy` CLI while its desktop app is closed. Sign in to the CLI and run `agy -p /usage`, then refresh the monitor and pin the account marked CLI. This independently verified account stays separate from desktop cards. Remove an unwanted desktop card in Settings if you only need the CLI source. Neither removal nor this reader logs you out.

This is an unsigned beta. Clean-machine, screen-reader, mixed-DPI multi-monitor, and sleep/resume testing is still limited. Source and build instructions are available in the repository.

For source development, install a .NET 8 SDK and use `Start-Windows.ps1`. The development reader uses `.venv-desktop`. `Build-Windows.ps1` creates the self-contained WPF archive. The earlier Tk prototype remains available through `Start-Desktop.ps1` for comparison, but should not run alongside the WPF monitor.

The native main window groups quotas by account. Click a ring to select the system-tray quota, shown by the Tray marker. Corner stars pin quotas to the floating monitor. Right-click the floating monitor for Size (75–200%) and Opacity (35–100%). Both settings persist. Outer rings keep agent colors, including burnt orange for direct Claude. Gray time rings sit immediately inside quota rings. Pace warnings color the percentage text.

In Settings, Start with Windows launches the monitor in the tray when you sign in. It is off by default, needs no administrator rights, and can be disabled there. Keep the portable folder in a permanent location. Disable startup before moving or deleting it, then re-enable it from the new location. Windows Task Manager can independently disable startup entries.

Copilot Sign in in Settings launches the official GitHub CLI browser flow in its own console. Complete its prompts there. After the optional runtime setup, the monitor binds the returned account only after a verified quota read. Existing GitHub CLI authentication is managed by GitHub CLI.
