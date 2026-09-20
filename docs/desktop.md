# Agent Quota Monitor Windows

`Start-Desktop.ps1` runs a local native Windows shell over the same monitor and loopback dashboard. It creates `.venv-desktop` in this checkout, installs the two pinned display packages, and starts one process. It does not install a startup task or service.

```powershell
cd agent-quota-monitor-windows
.\Start-Desktop.ps1
```

The tray shows one selected quota window. The main window groups quota donuts by provider, account, and quota group. The floating monitor shows a compact strip of pinned quota windows. Unknown readings remain `Unknown`, and stale readings remain marked stale. The selection, floating state, and opacity use the existing CurrentUser DPAPI settings file. Existing account order, hidden-account settings, and requested Google labels are preserved.

Click a main-window donut to choose the tray quota. Use its pin control to include or remove that quota in the floating monitor. Pinning is independent of the tray selection. The tray menu can show or hide the compact popup, toggle the optional always-on-top floating monitor, open the complete browser dashboard, or quit. Escape and closing the popup hide only the popup. Choosing Quit stops the desktop-owned loopback server and polling thread.

The window icon is a robot inside a ring. The tray ring shows quota remaining with a short group label inside. CX means Codex, CL means direct Claude, GM means Antigravity Gemini, CG means Antigravity Claude/GPT, and CP means Copilot. Hover the tray for details. A gray broken ring indicates a reading that is not live.

The main window, floating monitor, and full dashboard use a thick quota ring and a thin time ring. Hover, keyboard focus, or tap reveals the reset and pace details while keeping the main view compact. Stale status remains visible.

The desktop shell reads no credentials and launches no model prompt. Provider polling remains the existing conservative monitor schedule. It opens the full dashboard in the default browser because this prototype intentionally avoids an embedded webview dependency.

Only one instance is allowed per Windows user session through a named Windows mutex. A second launch signals the existing process to show its popup. If the default port is in use, stop the other local dashboard process or choose another port.

The floating strip is frameless and always on top. Drag it to reposition it. Right-click to reopen the main monitor or hide the strip. Hover a donut for the account, reset, and pace details. Pin choices and position persist across restarts.
