# Series 30 status projection

The #393 baseline in [the request audit](series-30-request-baseline.md) contains
source-derived request counts, **no measured Server or round-trip latency**.
Its status call chain is HTTP → in-memory ServerStatus → JSON. There are no
GitHub calls, collection reads, sequential probes or measured database/lock
bottlenecks to remove from that path. The observed dashboard delay identified
there is the shared polling round waiting on slower queries; changing dashboard
refresh scheduling is outside this status projection change.

The new call chain is HTTP → one registry query → JSON. The query reads the
singleton schema version and three primary-key counter rows, independently of
collection size. A missing database/schema or failed SQLite read returns
not-ready with persistenceAvailable=false and freshness=null. Process health
remains distinct from control-plane readiness. GitHub and Worker availability
are not Server readiness dependencies, matching /readyz.

Freshness projects/workers/executions are opaque persisted monotonic counters.
SQLite triggers advance them in the same transaction as inserts, updates and
deletes, including lease, execution metadata and Worker authentication changes.
Rolled-back changes do not advance visible counters. Counters may advance for
no-op writes; clients must compare equality, not interpret increments as counts.
Compare startedAtUtc too, and invalidate local freshness on session changes or
Server replacement. There is no Issue collection revision in the existing API;
these markers do not claim GitHub freshness.

Time-dependent Worker stale/offline transitions still require periodic refresh:
a counter describes persisted changes, not passage of time. Detail, readiness,
stale and partial-state projections retain their existing ownership and meaning.
No cached snapshot or freshness marker grants scheduling or mutation authority.

## Timing and comparable capture

Every status response includes Server-Timing registry;dur in milliseconds,
covering registry open/read and projection, excluding serialization.
The low-volume, tag-free CodexServer.Status meter exposes
codex.server.status.duration (ms), covering middleware through response writing.
Existing X-Codex-Request-Id correlates HTTP traces without payload logging.
Neither measure includes client queueing, connection, proxy or network time.
No per-poll log is added.

Repeat the exact workload and cold/warm capture protocol in #393's audit.
Collect the histogram or hosting trace for Server processing, Server-Timing for
the registry component, and browser/CDP duration for round trip. Report sample
count, failures and p50/p95/p99 independently; RTT minus processing is only an
approximate residual. Do not compare endpoint-only timing with full processing.

| Evidence | Before | After |
| --- | --- | --- |
| Source status dependencies | In-memory object | Bounded schema/counter read |
| Server processing p50/p95/p99 | Not captured in #393 | Not captured |
| Browser RTT p50/p95/p99 | Not captured in #393 | Not captured |
| Live workload/session | Unavailable | Unavailable |

No material latency improvement is claimed: the prior path was already
in-memory, and honest readiness/freshness introduces a small database read.
A comparable live before/after result requires the authorized deployment,
dataset and browser session absent from both audits.

Local regression coverage checks persisted markers across project create,
update and delete, Worker registration and execution enqueue, independent store
instances, and status reads despite an invalid project detail payload. Existing
HTTP status coverage now checks readiness, freshness and timing headers.
Local dotnet test --no-restore could not run (NETSDK1004 missing assets).
Restore also failed in this environment without reporting a NuGet/compiler
diagnostic. The Worker's configured authoritative checks remain separate.
