# Building and contributing

The native shell uses WPF on .NET 8. A separate Python backend reads quotas and serves the optional loopback dashboard.

## Build

On Windows x64, install .NET 8 SDK and Python 3.12 or newer, then run `./Build-Windows.ps1`. It creates a portable ZIP and SHA-256 file under `dist/`. The script accepts `-Version` and runs an offline packaged rendering check.

For development, configure `.venv-desktop` using the environment instructions in [desktop development](desktop.md), then run `./Start-Windows.ps1`. The older Tk shell remains available for comparison.

## Validate

```powershell
python -m unittest discover -s tests -v
node --check web/app.js
node tests/test_pace.mjs
node tests/test_copilot_bridge.mjs
dotnet build native/AgentQuotaMonitor.csproj -c Release
python tests/check_native_requests.py dotnet
```

Clean-machine, accessibility, mixed-DPI, and sleep/resume testing remains limited. Startup command checks do not replace a real Windows sign-in test.

Documentation images render the same native controls with synthetic sample data. Run the executable with `--screenshots-output` followed by an output directory to regenerate them. This does not start provider readers.

## Contributions

Build the installer with `./Build-Installer.ps1 -BundleDirectory <portable-build-folder> -Version <version>` after installing Inno Setup 6. Validate installation, upgrades, removal, and preservation of settings before publishing. Run update policy tests with `dotnet run --project tests/native-updates/UpdateTests.csproj`.

Include Windows version, app version, provider names, and sanitized error categories in issues. Never include credentials, authorization codes, private provider files, or personal quota screenshots. See [provider interfaces](provider-evidence.md) and [third-party notices](../THIRD-PARTY-NOTICES.md).
