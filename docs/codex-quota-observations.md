# Codex account quota observations

The Worker observes read-only `codex app-server` account limits for the Issue lifecycle. Start display uses the most recent cached snapshot (up to two minutes old), immediately beneath the Issue's model/effort line and before scheduler admission output. Production warms this cache after authentication preflight, before queue access; each independent execution refreshes it asynchronously without delaying scheduler admission. A cold, stale or unavailable cache displays unavailable. Validation repairs and integration do not print intermediate limits.

The final read bypasses the short read cache after validation, repairs, integration and GitHub outcome reporting, before constructing the existing completion notification. Its single display follows the overall completed/failed/blocked line before capacity release. Interrupted infrastructure and shutdown paths make a bounded best-effort terminal observation before releasing execution ownership. Lookup failures do not change task outcomes or trigger retries. Final Telegram notifications append this terminal observation without sending a separate message or changing delivery checkpoints.

The app-server runs under the Worker service user, using the same resolved Codex executable and `CODEX_HOME` as execution. That user must already be authenticated. Ensure the service PATH or configured Codex binary resolves a CLI supporting `account/rateLimits/read`. No authentication tokens are read or exported. The child uses the existing isolated Codex environment, with GitHub credentials and Git credential helpers removed. No agent task is started.

The installed CLI 0.159.3 generated protocol schema was inspected: `initialize` accepts `clientInfo` (name/version), followed by `initialized`; `account/rateLimits/read` returns a nullable `rateLimits` snapshot whose windows contain `usedPercent`, nullable `windowDurationMins`, and nullable Unix-seconds `resetsAt`. Unsupported methods, authentication failures, malformed responses and missing snapshots are unavailable. Schema support varies by CLI version. The Worker does not scrape interactive status or call private backend endpoints.

Reads have an eight-second deadline including serialization behind a local gate. Concurrent events reuse a two-second cache, including unavailable results, scoped to the process's fixed local credential context. Only one app-server child runs at a time. Output is capped at 128 KiB; stderr is drained and discarded, never included in diagnostics. Children are terminated and disposed after each query, with a bounded termination wait.

Example journal entry:

```text
↳ Limits · [01234567] · 5h 75% left · Weekly unavailable
```

Telegram appends `Codex account quota: 5h 75% left · Weekly 40% left` when a recent observation is available. Observations older than two minutes are stale and omitted from Telegram. Missing or invalid percentages are unavailable, never zero or 100 by default. Durations of 300 and 10080 minutes identify 5h and weekly allowances regardless of primary/secondary position. Other durations remain in the normalized DTO without acquiring either label; duplicate durations are treated as ambiguous.

Limits are shared by the authenticated account across Issues and Workers. These observations cannot measure exact per-Issue usage and must not be summed or interpreted as usage deltas. No account/provider identifiers, plan metadata, credit balances, or raw response fields are retained. The optional normalized DTO stores observation time, status, window duration, validated remaining percentage, and reset time. Start observations remain in the existing operational journal; end observations live in execution memory and the existing durable completion report where completion persistence is used, following its retention policy. There is no new database, Server authority, or dashboard UI. Changing the service credential context requires restarting the Worker to discard its local cache.

Human quota output uses the operational log when configured (including service stdout/event ingestion), otherwise the Worker console. It never writes to both sinks. Every line includes the short execution ID because parallel execution messages can interleave. Observation and reset timestamps remain in the structured DTO and detailed `Summary` diagnostic formatting.

Issue/profile/start limits and terminal status/final limits are grouped under the console's existing writer lock so concurrent executions cannot split those display groups. Every limits line retains its execution ID. Console limits use default foreground (with an interactive reset); operational limits are plain text without gray ANSI styling. The operational sink remains the sole owner when configured, with no duplicate console write. Capacity accounting and scheduler snapshots remain authoritative and immediate; quota does not reorder timestamps or delay the global scheduler.
