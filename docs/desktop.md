# Agent Quota Monitor Windows

`Start-Desktop.ps1` runs a local native Windows shell over the same monitor and loopback dashboard. It creates `.venv-desktop` in this checkout, installs the two pinned display packages, and starts one process. It does not install a startup task or service.

```powershell
cd agent-quota-monitor-windows
.\Start-Desktop.ps1
```

The tray tooltip and floating monitor show one selected provider, account, quota group, and reported window. The compact popup lists all dynamically discovered quota windows. Unknown readings remain `Unknown`, and stale readings remain marked stale. The selection, floating state, and opacity use the existing CurrentUser DPAPI settings file. Existing account order, hidden-account settings, and requested Google labels are preserved.

Click a compact-popup row to select the provider, account, group, and window used by the tray and floating monitor. The tray menu can show or hide the compact popup, toggle the optional always-on-top floating monitor, open the complete browser dashboard, or quit. Escape and closing the popup hide only the popup. Choosing Quit stops the desktop-owned loopback server and polling thread.

The window icon is a robot inside a ring. The tray ring shows quota remaining with a short group label inside. CX means Codex, CL means direct Claude, GM means Antigravity Gemini, CG means Antigravity Claude/GPT, and CP means Copilot. Hover the tray for details. A gray broken ring indicates a reading that is not live.

The full dashboard uses a thick quota ring and a thin time ring. Hover, keyboard focus, or tap reveals the reset and pace details while keeping the main view compact. Stale status remains visible.

The desktop shell reads no credentials and launches no model prompt. Provider polling remains the existing conservative monitor schedule. It opens the full dashboard in the default browser because this prototype intentionally avoids an embedded webview dependency.

Only one instance is allowed per Windows user session through a named Windows mutex. A second launch signals the existing process to show its popup. If the default port is in use, stop the other local dashboard process or choose another port.
