# Provider interfaces

These integrations were exercised on Windows during development. Internal provider interfaces may change and are not guaranteed public APIs.

## Codex

Reads the official Codex session and its account-scoped `/wham/usage` response. Account identity must match. Window durations are interpreted explicitly rather than assuming primary means five hours. Official login owns credential renewal. The [app-server interface](https://learn.chatgpt.com/docs/app-server) is a possible future replacement for the internal HTTP reader.

## Direct Claude

Reads the official Claude Code session. `/api/oauth/profile` verifies account identity before `/api/oauth/usage` is accepted. Product attribution is excluded from quota windows. [Official Claude Code login](https://code.claude.com/docs/en/cli-reference) can be launched from Settings. Tokens are never logged or refreshed by the monitor.

The credential file's non-secret `expiresAt` value (milliseconds) is read at discovery and shown in Settings. On Windows in September 2026 the access token lasted about eight hours after Claude Code renewed it. Once it has expired, the reader reports `session_expired` without sending a request. Only Claude Code renews the session. When Claude Code rewrites the credential file, the changed file revision allows an immediate read. Expired sessions retry at the normal interval without escalating backoff.

## Antigravity

Discovers a running official local language service and reads `GetUserStatus` and `RetrieveUserQuotaSummary`. Checks the account label before and after the quota read. Local source plus email supplies a local identity only because a stable Google provider subject is not returned. Google Gemini and Claude/GPT quota groups remain separate. Missing five-hour windows are unreported.

[Official CLI authentication](https://antigravity.google/docs/cli/install) is initiated at startup when a saved session is absent. No dedicated login command is advertised by the tested CLI. The separate CLI reader runs the built-in `agy -p /usage --print-timeout 20s` command, which [the official documentation](https://antigravity.google/docs/cli/headless/) identifies as CLI-handled rather than a model prompt. Windows CLI 1.1.27 returned tab-separated group, window, remaining percentage, and reset timestamp with the desktop app closed.

The CLI reader reads the existing `gemini:antigravity` Windows Credential Manager session and verifies its Google subject and verified email through the [Google UserInfo endpoint](https://developers.google.com/identity/openid-connect/reference). It binds that identity in an app-owned DPAPI descriptor containing identity metadata and a one-way session fingerprint, never raw credentials. It checks the session before running the command and verifies identity again after reading quotas. Only the official CLI renews its own credentials. API-key/custom-provider mode is excluded.

CLI cards have stable IDs derived from the Google subject in a separate CLI namespace. They are never merged into a desktop card by email. Discovery prefers the CLI without querying the desktop service. After a successful CLI read, legacy desktop cards are suppressed in the display, including during a temporary CLI failure. Legacy cache and pins remain stored without identity migration. Desktop discovery remains a fallback when CLI discovery is unavailable. The monitor's normal five-minute interval, failure backoff, and stale-value behavior apply. Initial identity discovery also honors backoff. Missing windows stay absent, and malformed or changed CLI output is rejected rather than guessed. A fresh CLI sign-in and `agy -p /usage` may be needed before initial identity binding if its saved access token has expired.

When the CLI cannot be used and no desktop service is found, discovery reports the CLI's error, for example `antigravity_cli_unavailable` when `agy.exe` is absent. Settings then offers the official installer from the [install documentation](https://antigravity.google/docs/cli/install). The monitor shows the exact command and runs it in a visible PowerShell window only after the user confirms. It never downloads or stores the script itself. Desktop discovery first lists running process names through the Windows process snapshot API and runs the PowerShell command-line lookup only when a language server is present. Command lines, which contain a local secret, are read only in that lookup.

Gemini CLI is a separate product. After June 18, 2026 it stopped serving Google AI Pro, Ultra, and free-tier users, whose access moved to the Antigravity CLI ([announcement](https://github.com/google-gemini/gemini-cli/discussions/27274)). It does not report Antigravity subscription quota and is not used.

## GitHub Copilot

Uses the official Copilot SDK quota API through the installed client. `Setup-Copilot.ps1` configures the optional SDK runtime outside the repository. Existing GitHub authentication is required. Copilot model-specific billing or quotas must not be inferred from other providers.

## Failure handling

- **Discovery misses.** When an official source disappears, the last reading keeps its status until it is older than two poll intervals, and the reason is recorded. Retry timers from earlier reads are cleared unless a provider cooldown applies.
- **Network failures.** DNS failures and refused or unreachable connections never reach the provider. They are reported as `network_unavailable` and retried at the normal interval. The native app asks the reader to retry connection failures after Windows resumes or the network returns, at most once a minute. Provider responses keep exponential backoff up to one hour and every `Retry-After` cooldown.
- **Format changes.** Unexpected provider output is rejected as `schema_changed` rather than guessed. The app then runs one update check for that session when automatic checks are enabled.
- **Reader versions.** The native app passes its version to the reader it launches. A reader started by another version is stopped through its process-bound shutdown request and replaced.

## Research

Compared CodexBar, WhereMyTokens, shuvquota, and onWatch. The application implements its own bounded connection coordinator, explicit quota model, and Windows presentation. See THIRD-PARTY-NOTICES.md. Account values and development snapshots are not included in this repository.
