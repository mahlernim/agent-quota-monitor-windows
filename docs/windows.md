# Agent Quota Monitor Windows

This beta uses WPF for the native windows and vector graphics. Extract the entire archive and run `agent-quota-monitor-windows.exe`. The package includes the .NET runtime and a separate local quota reader, so Python and .NET do not need to be installed separately. Keep the `backend` folder and all runtime files beside the executable.

Click a donut to choose the tray quota. Pin checkboxes choose the quotas in the compact floating strip. Hover for details. Drag the floating strip to reposition it, and right-click for its menu and opacity. Close hides the main window. Quit stops the reader if this application started it.

Colors identify agents using blue-green shades. Yellow percentage text means quota remaining is below half the remaining portion of the time window. Red means it is below one quarter. Missing timing data never generates a projected warning. Percentages show at most one decimal.

Accounts opens native provider, sign-in, removal, and restore controls. Codex and Claude launch their official coding-client sign-in commands. Antigravity opens its official desktop application, where account selection takes place. The monitor verifies returned account identity. Removal hides the account and stops monitoring without logging out or revoking credentials. Copilot still requires its optional official CLI/SDK setup. Web opens the full dashboard, including account ordering.

Settings and cache are encrypted for your Windows user under `%LOCALAPPDATA%\QuotaDashboard`. Existing pinned selections are migrated automatically. The monitor binds only to 127.0.0.1 and has no startup service or telemetry.

This is an unsigned beta. Clean-machine, screen-reader, mixed-DPI multi-monitor, and sleep/resume testing is still limited. Source and build instructions are available in the repository.

For source development, install a .NET 8 SDK and use `Start-Windows.ps1`. The development reader uses `.venv-desktop`. `Build-Windows.ps1` creates the self-contained WPF archive. The earlier Tk prototype remains available through `Start-Desktop.ps1` for comparison, but should not run alongside the WPF monitor.

The native main window groups quotas by account. Click a ring to select the system-tray quota, shown by the Tray marker. Corner stars pin quotas to the floating monitor. Right-click the floating monitor for Size (75–200%) and Opacity (35–100%). Both settings persist. Outer rings keep agent colors, including burnt orange for direct Claude. Gray time rings sit immediately inside quota rings. Pace warnings color the percentage text.
