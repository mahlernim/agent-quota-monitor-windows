# Provider interfaces

These integrations were exercised on Windows during development. Internal provider interfaces may change and are not guaranteed public APIs.

## Codex

Reads the official Codex session and its account-scoped `/wham/usage` response. Account identity must match. Window durations are interpreted explicitly rather than assuming primary means five hours. Official login owns credential renewal. The [app-server interface](https://learn.chatgpt.com/docs/app-server) is a possible future replacement for the internal HTTP reader.

## Direct Claude

Reads the official Claude Code session. `/api/oauth/profile` verifies account identity before `/api/oauth/usage` is accepted. Product attribution is excluded from quota windows. [Official Claude Code login](https://code.claude.com/docs/en/cli-reference) can be launched from Settings. Tokens are never logged or refreshed by the monitor.

## Antigravity

Discovers a running official local language service and reads `GetUserStatus` and `RetrieveUserQuotaSummary`. Checks the account label before and after the quota read. Local source plus email supplies a local identity only because a stable Google provider subject is not returned. Google Gemini and Claude/GPT quota groups remain separate. Missing five-hour windows are unreported.

[Official CLI authentication](https://antigravity.google/docs/cli/install) is initiated at startup when a saved session is absent. No dedicated login command is advertised by the tested CLI. The separate CLI reader runs the built-in `agy -p /usage --print-timeout 20s` command, which [the official documentation](https://antigravity.google/docs/cli/headless/) identifies as CLI-handled rather than a model prompt. Windows CLI 1.1.27 returned tab-separated group, window, remaining percentage, and reset timestamp with the desktop app closed.

The CLI reader reads the existing `gemini:antigravity` Windows Credential Manager session and verifies its Google subject and verified email through the [Google UserInfo endpoint](https://developers.google.com/identity/openid-connect/reference). It binds that identity in an app-owned DPAPI descriptor containing identity metadata and a one-way session fingerprint, never raw credentials. It checks the session before running the command and verifies identity again after reading quotas. Only the official CLI renews its own credentials. API-key/custom-provider mode is excluded.

CLI cards have stable IDs derived from the Google subject in a separate CLI namespace. They are never merged into a desktop card by email. Discovery prefers the CLI without querying the desktop service. After a successful CLI read, legacy desktop cards are suppressed in the display, including during a temporary CLI failure. Legacy cache and pins remain stored without identity migration. Desktop discovery remains a fallback when CLI discovery is unavailable. The monitor's normal five-minute interval, failure backoff, and stale-value behavior apply. Initial identity discovery also honors backoff. Missing windows stay absent, and malformed or changed CLI output is rejected rather than guessed. A fresh CLI sign-in and `agy -p /usage` may be needed before initial identity binding if its saved access token has expired.

## GitHub Copilot

Uses the official Copilot SDK quota API through the installed client. `Setup-Copilot.ps1` configures the optional SDK runtime outside the repository. Existing GitHub authentication is required. Copilot model-specific billing or quotas must not be inferred from other providers.

## Research

Compared CodexBar, WhereMyTokens, shuvquota, and onWatch. The application implements its own bounded connection coordinator, explicit quota model, and Windows presentation. See THIRD-PARTY-NOTICES.md. Account values and development snapshots are not included in this repository.
