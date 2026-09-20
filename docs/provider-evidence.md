# Provider interfaces

These integrations were exercised on Windows during development. Internal provider interfaces may change and are not guaranteed public APIs.

## Codex

Reads the official Codex session and its account-scoped `/wham/usage` response. Account identity must match. Window durations are interpreted explicitly rather than assuming primary means five hours. Official login owns credential renewal. The [app-server interface](https://learn.chatgpt.com/docs/app-server) is a possible future replacement for the internal HTTP reader.

## Direct Claude

Reads the official Claude Code session. `/api/oauth/profile` verifies account identity before `/api/oauth/usage` is accepted. Product attribution is excluded from quota windows. [Official Claude Code login](https://code.claude.com/docs/en/cli-reference) can be launched by the dashboard. Tokens are never logged or refreshed by the monitor.

## Antigravity

Discovers a running official local language service and reads `GetUserStatus` and `RetrieveUserQuotaSummary`. Checks the account label before and after the quota read. Local source plus email supplies a local identity only because a stable Google provider subject is not returned. Google Gemini and Claude/GPT quota groups remain separate. Missing five-hour windows are unreported.

[Official CLI authentication](https://antigravity.google/docs/cli/install) is initiated at startup when a saved session is absent. No dedicated login command is advertised by the tested CLI. Anonymous CLI quota output is not attached to a named account.

## GitHub Copilot

Uses the official Copilot SDK quota API through the installed client. `Setup-Copilot.ps1` configures the optional SDK runtime outside the repository. Existing GitHub authentication is required. Copilot model-specific billing or quotas must not be inferred from other providers.

## Research

Compared CodexBar, WhereMyTokens, shuvquota, and onWatch. The application implements its own bounded connection coordinator, explicit quota model, and Windows presentation. See THIRD-PARTY-NOTICES.md. Account values and development snapshots are not included in this repository.
