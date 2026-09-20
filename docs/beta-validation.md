# Portable beta validation

Validated on Windows 11 x64 on 2026-09-20.

- Built with 64-bit Python 3.12.10 and PyInstaller 6.22.3.
- All 59 Python tests, JavaScript syntax, pace calculations, and Copilot bridge tests passed.
- Frozen offline smoke check passed for six bundled resources, Tcl/Tk, and icon rendering. No provider reads were made by that check.
- ZIP extracted outside the repository and launched with a different working directory.
- Fresh quota reads succeeded for Codex, direct Claude, Antigravity, and Copilot through existing official sessions.
- The existing unavailable account remained explicitly stale.
- Second launch exited successfully while the original process retained the single loopback listener.
- Quit removed the loopback listener. The source development instance was restored afterward.
- Archive contains dependency notices and pystray source. No DPAPI state, environment files, or application logs were found in the extracted archive.
- Staged source passed Gitleaks.

The unsigned beta has not been tested on a clean Windows installation without developer tools. Sleep/resume, multiple monitors, high DPI, screen-reader behavior, and all external sign-in flows still require broader testing. In particular, check external client launches for inherited DLL search-path conflicts in frozen builds before wider distribution.
