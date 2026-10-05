# Optional quota window activation

Monitoring remains read-only. Activation is a separate opt-in action that consumes subscription allowance. Fresh installs and the first introducing upgrade leave it off. Later upgrades preserve choices and attempt history. Each new stable account identity starts unselected. The initial supported window is direct Claude five-hour; other windows and providers remain unavailable.

## Evidence and dispatch

The activation worker samples the existing monitor snapshot every ten seconds. It does not cause additional provider reads. Distinct successful provider read timestamps advance evidence. Repeated cache snapshots do not. The same source and account must continuously report validated inactivity for the selected 30, 60, or 120 minutes, with at least two reads. Gaps longer than eleven minutes, failure, source changes, sleep gaps, or clock discontinuities break the streak. Restarting conservatively rebuilds evidence.

A Claude window is inactive only when the raw response explicitly supplies zero usage and a null five-hour reset. The all-model weekly allowance must be positive and extra usage explicitly disabled. Both window durations must match their known identities. Running deadlines, disabled windows, missing or malformed data, unknown billing state, and stale snapshots block sending. Numeric precision is retained rather than rounding to 100 percent.

Before dispatch, the runner rechecks its verified version and subscription identity. The latest snapshot and saved opt-in settings are checked again afterward. A per-account reservation is written before launching the single bounded request. The encrypted journal uses an exclusive Windows file lock and an atomic replace, including a flush before replace. Storage failure blocks dispatch and never resets a corrupt journal to defaults.

Reservations, sent results, and uncertain delivery all suppress another prompt. Two distinct post-dispatch readings showing a fixed deadline near the expected five-hour end confirm the observed transition. A later cycle needs expiry of that confirmed deadline and a new full inactivity streak. At most five reservations are allowed per account per rolling 24 hours. An unresolved receipt stays blocked, including across updates. There is no manual retry button or paid/API fallback. Disabling activation stops future dispatches; a request already dispatched cannot be undone.

Preferences and receipts live in Windows-user-encrypted `activation.dpapi` alongside the existing monitor data, without changing official client files. Receipts contain no response text or credentials. The first runner is Claude Code 2.1.280 with a fixed Haiku model, safe mode, disabled hooks and tools, empty strict MCP configuration, and no saved session. Unsupported client versions pause activation. Monitoring keeps its existing version policy.

## Suggestions and controls

After three hours of eligible inactivity, an off-by-default account can get one quiet suggestion in the visible main window. The app does not open a window or send an OS notification. Showing it is recorded, so an update cannot bring it back. Configure opens Settings without opting in, Later clears the acknowledgement and snoozes suggestions for a week, and Don't show again persists until suggestions are explicitly re-enabled in Settings. Settings edits require Save and show failures without claiming the previous choices changed.

## Limits and future support

All-model weekly-only activation is deferred. A supported five-hour prompt may also start a weekly window. Codex and each Antigravity group require their own validated eligibility representation, account identity checks, safe subscription runner, and per-cycle reservation before support can expand. A full percentage alone is never enough. Separate Antigravity Gemini and Claude/GPT allowance must never be merged. Codex plans without a five-hour window do not get an invented one.

Tests use synthetic providers and isolated storage. They cover opt-in defaults, continuous fresh evidence, precise quota values, malformed resets, identity isolation, clock and source changes, durable reservation failures, uncertain delivery, restart protection, post-send confirmation, new cycles, suggestion persistence, loopback request guards, and native save feedback. The controlled provider experiment supplies evidence for the window semantics; it is not rerun as part of CI or packaging.
