# Codex account quota observations

The Worker makes read-only `codex app-server` JSON-RPC observations immediately before and after the initial Codex run, including structured failed and blocked outcomes. Validation repairs and Git operations do not trigger additional reads. Lookup failures do not affect execution outcomes or scheduling. Final Telegram notifications append available observations without sending a separate message or changing delivery checkpoints.

The app-server runs under the Worker service user, using the same resolved Codex executable and `CODEX_HOME` as execution. That user must already be authenticated. Ensure the service PATH or configured Codex binary resolves a CLI supporting `account/rateLimits/read`. No authentication tokens are read or exported. The child uses the existing isolated Codex environment, with GitHub credentials and Git credential helpers removed. No agent task is started.

The installed CLI 0.159.3 generated protocol schema was inspected: `initialize` accepts `clientInfo` (name/version), followed by `initialized`; `account/rateLimits/read` returns a nullable `rateLimits` snapshot whose windows contain `usedPercent`, nullable `windowDurationMins`, and nullable Unix-seconds `resetsAt`. Unsupported methods, authentication failures, malformed responses and missing snapshots are unavailable. Schema support varies by CLI version. The Worker does not scrape interactive status or call private backend endpoints.

Reads have an eight-second deadline including serialization behind a local gate. Concurrent events reuse a two-second cache, including unavailable results, scoped to the process's fixed local credential context. Only one app-server child runs at a time. Output is capped at 128 KiB; stderr is drained and discarded, never included in diagnostics. Children are terminated and disposed after each query, with a bounded termination wait.

Example journal entry:

```text
Codex quota end · execution 01234567-89ab-cdef-0123-456789abcdef · sample #17 · observed 2026-10-09T12:00:00.0000000+00:00 · 5h: 75% remaining (reset 2026-10-09 15:00 UTC) · weekly: unavailable
```

Telegram appends `Codex account quota: 5h: 75% remaining · weekly: 40% remaining` when a recent observation is available. Observations older than two minutes are stale and omitted from Telegram. Missing or invalid percentages are unavailable, never zero or 100 by default. Durations of 300 and 10080 minutes identify 5h and weekly allowances regardless of primary/secondary position. Other durations remain in the normalized DTO without acquiring either label; duplicate durations are treated as ambiguous.

Limits are shared by the authenticated account across Issues and Workers. These observations cannot measure exact per-Issue usage and must not be summed or interpreted as usage deltas. No account/provider identifiers, plan metadata, credit balances, or raw response fields are retained. The optional normalized DTO stores observation time, status, window duration, validated remaining percentage, and reset time. Start observations remain in the existing operational journal; end observations live in execution memory and the existing durable completion report where completion persistence is used, following its retention policy. There is no new database, Server authority, or dashboard UI. Changing the service credential context requires restarting the Worker to discard its local cache.
