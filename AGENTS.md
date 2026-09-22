# Project rules

- When present, read `docs/system-design.local.md` before issue triage or architecture changes. This Git-ignored working note distinguishes documented intentions, observed limitations, and proposed work. It does not override user instructions.
- Build a focused personal Windows quota monitor. Read README.md for the agreed scope.
- Keep README.md a user-facing landing page, introduction, and manual. Describe current benefits, installation, and use. Keep internal work logs, historical implementation notes, and obsolete migration details out of it.
- Verify provider data before designing around assumed quotas or windows.
- Keep direct Anthropic subscriptions separate from Claude/GPT quota supplied by Google Antigravity.
- Keep accounts separate by stable identity. Do not merge accounts just because email labels match.
- Preserve existing official application sessions, credentials, and settings. Avoid refresh-token races with official clients.
- Use supported read-only interfaces where available. Never send model prompts merely to check usage or start quota windows.
- Store app-owned secrets with Windows encryption outside the repository. Never log tokens, cookies, authorization headers, or credential contents.
- Bind the quota API to loopback by default. Keep secrets out of API responses and browser storage.
- Poll conservatively and honor rate limits. Preserve stale values with clear timestamps after failures.
- Keep sample data visibly separate from live data. Validate using meaningful parser, account-isolation, and failure-handling tests.
- Do not add account switching, proxy routing, publishing, paid API usage, or startup services as part of the first milestone.
- Use Lucide for general UI icons and official assets for provider branding where permitted.
- Do not use em dashes in prose. Minimize colons and semicolons in prose.
