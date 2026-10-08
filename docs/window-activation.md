# Optional quota window activation

Monitoring remains read-only. Activation is a separate opt-in action that consumes subscription allowance. Fresh installs and the first introducing upgrade leave it off. Later upgrades preserve choices and attempt history. Each new stable account, group, and window starts unselected. Existing Claude five-hour selections retain their original selection key and receipts.

## Supported windows

| Provider | Windows | Fixed model |
| --- | --- | --- |
| Codex | Weekly | GPT-6 Luna, low reasoning |
| Direct Claude | Five-hour and all-model weekly | Haiku 4.5 |
| Antigravity Gemini | Five-hour and weekly | Gemini 3.8 Flash Low |
| Antigravity Claude/GPT | Five-hour and weekly | GPT-OSS 120B Medium |

Account identity, quota group, and window remain separate. Codex plans without a five-hour limit do not receive an invented one. A single prompt may start both windows in its group. AQM reserves both observed inactive windows before that prompt, even when only one was selected, without enabling the other window. The two Antigravity groups require separate prompts and receipts.

## Evidence and dispatch

The worker samples the existing monitor snapshot every ten seconds without causing extra quota reads. Distinct successful provider read timestamps advance evidence. Repeated cache snapshots do not. The same source and account must continuously report inactivity for the selected 30, 60, or 120 minutes, with at least two reads. Gaps longer than eleven minutes, failures, source changes, sleep gaps, or clock discontinuities break the streak. Restarting rebuilds evidence.

Direct Claude inactivity requires exact zero usage and an explicitly reported null reset, with extra usage explicitly off. Codex and Antigravity require exact full quota and a deadline approximately one full window ahead of the provider read. Across distinct reads at least 30 seconds apart, that deadline must move with elapsed time within a 15-second tolerance. A fixed future deadline means running, even at full quota. The full-duration comparison allows 60 seconds for read latency. Numeric percentages are never rounded into eligibility.

Missing, duplicate, malformed, disabled, stale, or exhausted windows block sending. The all-model weekly allowance and any reported five-hour allowance must be positive. Identity and saved opt-in settings are rechecked before dispatch. Unknown reset representations never count as inactivity.

An encrypted durable reservation precedes launch. The journal uses an exclusive Windows file lock and an atomic replacement after flushing. Storage failure blocks sending and never resets a corrupt journal to defaults. Reservations, sent results, and uncertain delivery suppress another prompt for that account and group, including across restarts and updates. There is no blind retry or paid/API fallback. If the Codex or Antigravity runner finds a changed identity or credential revision immediately before launch, no process starts. AQM then releases that reservation, restores the previous receipt, does not count it toward the daily limit, and waits five minutes before checking again. A failure after launch remains uncertain.

Two distinct post-dispatch readings with a fixed deadline near the expected end confirm each window separately. Later activation requires expiry of that window's confirmed deadline and a new inactivity streak.

A window started by a prompt ends at its dispatch time plus the window length, and confirmation allows ten minutes of tolerance. After that latest possible deadline passes, the prompt can no longer own a live window and can never be confirmed. This happens when the PC sleeps through the window or other use starts it first. AQM then marks the reserved, sent, or uncertain receipt and its history entry expired, keeping its original time and outcome details. An expired receipt no longer blocks its group. The next prompt still requires a new inactivity streak that begins after the expiry, so inactivity observed before that point never counts. Within a coalesced group, the other window's receipt keeps blocking the group until it is confirmed or its own deadline passes. Existing receipts are preserved on upgrade and expire by the same rule. At most five prompts are allowed per account and quota group per rolling 24 hours. A prompt covering two windows counts once. Disabling activation stops future dispatches. An already-dispatched request cannot be undone.

## Official subscription runners

Claude supports 2.1.280 and newer 2.x clients, verifies the account UUID, organization, and Pro or Max subscription, and uses safe mode, empty strict MCP configuration, disabled hooks and tools, and no saved session. Usage sources that omit extra-usage status cannot authorize an inactive-window prompt.

Codex requires CLI support for ignoring user configuration and ephemeral execution. It verifies the signed-in account, forces ChatGPT authentication and the OpenAI provider, and uses an empty workspace, read-only sandbox, low reasoning, disabled shell, hooks, apps, delegation and web search. Official credentials remain in their original location and are not copied or renewed by AQM.

Antigravity requires the official CLI quota source with a verified Google subject. It checks the credential revision again before sending. Plan mode, an empty workspace, disabled slash-command expansion, and no automatic permission approval bound the request. Configured CLI plugins or MCP servers pause activation. The CLI has no verified invocation-wide tools-off switch. Its request timeout is 60 seconds, with a 75-second process bound. Codex and Claude processes are bounded to 90 seconds. Output is limited in memory and only numeric usage counters are retained.

Runners remove API-key and alternate-provider environment overrides from their child processes. They do not change official settings or use a fallback model. CLI context can use thousands of tokens despite a short reply. Activation uses allowance and does not promise negligible cost.

## Settings and suggestions

Preferences and receipts live in Windows-user-encrypted activation.dpapi alongside the monitor data. Receipts contain no response text or credentials. Choose individual account windows in Settings and save. Failed saves leave the previous preferences active.

After three hours of eligible inactivity while activation is off, AQM can show one quiet suggestion in the visible main window. It does not open a window or send an OS notification. Configure opens Settings without opting in. Later snoozes for a week. Don't show again persists until suggestions are explicitly re-enabled. The app must be running to observe and act.

## Verification

Synthetic tests cover moving versus fixed deadlines, exact quota precision, group isolation, coalesced windows, legacy receipt preservation, source and clock changes, uncertain delivery, unconfirmed receipt expiry, released pre-launch reservations, storage failures, post-send confirmation, daily limits, and native controls. Controlled provider observations establish the representations used here. A Codex request in an already-running weekly window verified the runner without claiming a controlled weekly activation. Natural-reset follow-ups remain supplementary evidence and are not a release prerequisite.
