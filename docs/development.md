# Building and contributing

The Windows application uses WPF on .NET 10. A separate Python backend reads quotas and exposes a loopback API for the native app. It does not serve a browser dashboard.

## Build

On Windows x64, install .NET 10 SDK and Python 3.12, then run `./Build-Windows.ps1`. It creates a portable ZIP and SHA-256 file under `dist/` and runs an offline packaged rendering check. The default release version comes from `native/AgentQuotaMonitor.csproj`. Use `-Version` only when building an explicit version override.

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

Use a .NET 10 SDK and Node.js 22 for the validation commands. Icon generation requires the build environment above and creates the ignored `build/robot-ring.ico` resource. The tests use synthetic provider responses and isolated loopback fixtures. They do not require a running monitor or provider sign-in.

The Windows validation workflow runs these checks for pull requests and pushes to `main`. It also runs the native rendering self-test. GitHub Actions prepares Python 3.12, Node.js 22, and .NET 10, installs the pinned build dependencies, and generates the icon before compiling. CI does not package or publish releases.

Clean-machine, accessibility, mixed-DPI, and sleep/resume testing remains limited. Startup command checks do not replace a real Windows sign-in test.

Main-window appearance checks cover compact countdown boundaries, missing and stale resets, glyph spacing inside the existing rings, and custom colors that leave floating windows, tray icons, and pace warnings unchanged. Synthetic Settings tests cover picker cancellation, serialized saves, save failures, and restoring defaults. HTTP tests verify color validation and persistence without changing pins or other preferences. These checks do not exercise a live provider's natural reset or the installed upgrade.

Documentation images render the same native controls with synthetic sample data. Run the executable with `--screenshots-output` followed by an output directory to regenerate them. This does not start provider readers.

The native account tests exercise ordering, draft conflicts, account controls, and separate operation and connection feedback through synthetic HTTP responses. Append `-- <output.png>` to their command to render a sample Settings window without connecting provider accounts.

Python persistence tests cover discovery misses, Claude session expiry, network failure classification, wake requests, and the Antigravity CLI install offer. Upgrade tests cover the reader's version echo and quota name validation. Native account checks cover problem actions per provider and source, the Node.js update script against synthetic winget and node commands, serialized provider saves, quota names, and the Details and Copy details contents. The rendering self-test covers uniform cards, the ring context menu, drag offsets, and label fitting. Update policy tests also cover installer detection and SHA-256 verified downloads with redirect restrictions.

Backend tests cover compatibility, startup deadlines, cancellation, process ownership, and replacement of a reader from another app version. Lifecycle tests exercise delayed responses during shutdown, malformed snapshots, floating-window preferences, and simultaneous launches. The backend identity marker prevents accidental connection to a different service but does not authenticate local callers.

Activation tests use synthetic provider readings and isolated encrypted journals, never live prompts. They cover disabled defaults, continuous inactivity, precision and malformed resets, stable identity, duplicate suppression across restarts, storage failures, uncertain delivery, expiry of unconfirmed receipts, two-read confirmation, suggestions, and native save errors. See [activation design](window-activation.md). Release QA must preserve `activation.dpapi` across upgrades and verify that introducing the feature leaves activation off.

The real startup test uses Windows sockets and the native connection flow to launch and stop a Python reader. It creates isolated encrypted settings with all providers disabled. To check a packaged reader, append `--backend <path-to-quota-backend.exe>` to the startup test command.

## Contributions

Build the installer with `./Build-Installer.ps1 -BundleDirectory <portable-build-folder>` after installing Inno Setup 6. Both build scripts read the same default version from the native project. If the portable build used `-Version`, pass the same override to the installer script. The installer script rejects a version that does not match the packaged executable, and the Inno Setup definition requires an explicitly supplied `AppVersion`.

The installer records the files it installs in `installed-files.txt` in the app folder. During an update it deletes files named in the previous record that the new version no longer ships, then removes folders left empty. Other files in the folder are never touched. Installations from before this record existed are cleaned with the fixed list in `packaging/legacy-files.txt`. It applies only when the folder already holds the app and its uninstaller.

Validate installation, upgrades, removal, and preservation of settings before publishing. Run update policy tests with `dotnet run --project tests/native-updates/UpdateTests.csproj`.

Release checklist:

1. Update the version in `native/AgentQuotaMonitor.csproj` and the download links in `README.md` and `docs/windows.md`.
2. Run the validation commands, `./Build-Windows.ps1`, and `./Build-Installer.ps1`.
3. Install over the previous release and confirm that settings, pins, and cached quota survive.
4. Publish the installer, the portable ZIP, and both `.sha256` files with the release. **Install update** in the next version requires the installer and its checksum file.
5. From the previous installed version, use **Install update** once the release is public.

Include Windows version, app version, provider names, and sanitized error categories in issues. Never include credentials, authorization codes, private provider files, or personal quota screenshots. See [provider interfaces](provider-evidence.md) and [third-party notices](../THIRD-PARTY-NOTICES.md).

## CLI release checks

`quota/cli_updates.py` owns the independent 24-hour release-check schedule in `cli-updates.dpapi`. The minute worker discovers local versions only for enabled providers. Remote attempts reserve their next check before network I/O, including failures. Manual checks coalesce and respect a one-minute floor plus Retry-After. The status endpoint performs no checks. Provider switches are checked again before network requests and before an update handoff. Installation fingerprints prevent an offer from being reused for a different binary or release channel.

Release checks read bounded HTTPS public metadata with redirects rejected. Codex, Claude Code and Copilot use their official npm package dist-tags. Antigravity uses the Windows x64 manifest referenced by its [official installer](https://antigravity.google/cli/install.ps1). Only stable three-part versions are compared. Unknown versions and prereleases fail closed. Claude's user-level `autoUpdatesChannel` selects the stable or latest dist-tag. Enterprise or project-level release policies are not resolved by AQM and remain the official updater's responsibility. WinGet remains responsible for package availability, which can lag vendor metadata.

The native panel uses the existing visible PowerShell confirmation flow with fixed commands. It revalidates the provider and installation after confirmation. Unknown installation methods open fixed official documentation links. Updater exit alone is not treated as successful installation. A subsequent local version check verifies the result. No live CLI installation or upgrade is exercised by automated tests.

Reference update paths are documented by [OpenAI](https://developers.openai.com/codex/cli/), [Anthropic](https://code.claude.com/docs/en/setup#update-claude-code), and [GitHub](https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/install-copilot-cli). Tests use synthetic clocks, stores, public metadata and launch callbacks. They cover daily persistence, cooldowns, disabled providers, failures, changed binaries, request coalescing, and confirmation cancellation without provider prompts or real installers.
