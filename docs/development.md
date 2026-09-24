# Building and contributing

The Windows application uses WPF on .NET 8. A separate Python backend reads quotas and exposes a loopback API for the native app. It does not serve a browser dashboard.

## Build

On Windows x64, install .NET 8 SDK and Python 3.12, then run `./Build-Windows.ps1`. It creates a portable ZIP and SHA-256 file under `dist/` and runs an offline packaged rendering check. The default release version comes from `native/AgentQuotaMonitor.csproj`. Use `-Version` only when building an explicit version override.

For development, prepare the backend Python environment and the build tools, then generate the icon before running `./Start-Windows.ps1`.

```powershell
python -m venv .venv-desktop
python -m venv .venv-build
.\.venv-build\Scripts\python.exe -m pip install -r requirements-build.txt
.\.venv-build\Scripts\python.exe packaging/make_icon.py
.\Start-Windows.ps1
```

The backend uses Python's standard library. Its development environment must be named `.venv-desktop` for the WPF launcher to find it. Pillow generates the application icon during the build.

## Validate

```powershell
python -m unittest discover -s tests -v
node --check quota/copilot_bridge.mjs
node --check quota/copilot_bridge_data.mjs
node tests/test_copilot_bridge.mjs
.\.venv-build\Scripts\python.exe packaging/make_icon.py
dotnet build native/AgentQuotaMonitor.csproj -c Release
python tests/check_native_requests.py dotnet
dotnet run --project tests/native-accounts/AccountTests.csproj -c Release
dotnet run --project tests/native-backend/BackendTests.csproj -c Release
dotnet run --project tests/native-lifecycle/LifecycleTests.csproj -c Release
python tests/check_native_startup.py dotnet
dotnet run --project tests/native-updates/UpdateTests.csproj
```

Use a .NET 8 SDK and Node.js 22 for the validation commands. Icon generation requires the build environment above and creates the ignored `build/robot-ring.ico` resource. The tests use synthetic provider responses and isolated loopback fixtures. They do not require a running monitor or provider sign-in.

The Windows validation workflow runs these checks for pull requests and pushes to `main`. It also runs the native rendering self-test. GitHub Actions prepares Python 3.12, Node.js 22, and .NET 8, installs the pinned build dependencies, and generates the icon before compiling. CI does not package or publish releases.

Clean-machine, accessibility, mixed-DPI, and sleep/resume testing remains limited. Startup command checks do not replace a real Windows sign-in test.

Documentation images render the same native controls with synthetic sample data. Run the executable with `--screenshots-output` followed by an output directory to regenerate them. This does not start provider readers.

The native account tests exercise ordering, draft conflicts, account controls, and separate operation and connection feedback through synthetic HTTP responses. Append `-- <output.png>` to their command to render a sample Settings window without connecting provider accounts.

Python persistence tests cover discovery misses, Claude session expiry, network failure classification, wake requests, and the Antigravity CLI install offer. Upgrade tests cover the reader's version echo. Update policy tests also cover installer detection and SHA-256 verified downloads with redirect restrictions.

Backend tests cover compatibility, startup deadlines, cancellation, process ownership, and replacement of a reader from another app version. Lifecycle tests exercise delayed responses during shutdown, malformed snapshots, floating-window preferences, and simultaneous launches. The backend identity marker prevents accidental connection to a different service but does not authenticate local callers.

The real startup test uses Windows sockets and the native connection flow to launch and stop a Python reader. It creates isolated encrypted settings with all providers disabled. To check a packaged reader, append `--backend <path-to-quota-backend.exe>` to the startup test command.

## Contributions

Build the installer with `./Build-Installer.ps1 -BundleDirectory <portable-build-folder>` after installing Inno Setup 6. Both build scripts read the same default version from the native project. If the portable build used `-Version`, pass the same override to the installer script. The installer script rejects a version that does not match the packaged executable, and the Inno Setup definition requires an explicitly supplied `AppVersion`.

Validate installation, upgrades, removal, and preservation of settings before publishing. Run update policy tests with `dotnet run --project tests/native-updates/UpdateTests.csproj`.

Release checklist:

1. Update the version in `native/AgentQuotaMonitor.csproj` and the download links in `README.md` and `docs/windows.md`.
2. Run the validation commands, `./Build-Windows.ps1`, and `./Build-Installer.ps1`.
3. Install over the previous release and confirm that settings, pins, and cached quota survive.
4. Publish the installer, the portable ZIP, and both `.sha256` files with the release. **Install update** in the next version requires the installer and its checksum file.
5. From the previous installed version, use **Install update** once the release is public.

Include Windows version, app version, provider names, and sanitized error categories in issues. Never include credentials, authorization codes, private provider files, or personal quota screenshots. See [provider interfaces](provider-evidence.md) and [third-party notices](../THIRD-PARTY-NOTICES.md).
