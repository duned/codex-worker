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

## 30.5.1: stage instrumentation and baseline protocol

**Measurement status: deployment capture pending.** No representative Server,
Worker, dashboard session or registry database is supplied in this checkout.
There is no measured distribution from which to identify a dominant component.
30.5.2 must not treat the original single sample as a baseline or attribute it to
SQLite connection open without the capture below.

The original sample `registry;dur=0.142` represents **0.142 milliseconds**
(142 microseconds), not 142 milliseconds: Server-Timing durations and this
implementation's Stopwatch conversion are both in milliseconds. Verify the raw
header if the intended observation was 142 ms (`dur=142.000`).

The existing `registry` metric retains its scope, including resource disposal and
status projection, excluding JSON serialization. Additional Server-Timing entries:

| Entry | Scope |
| --- | --- |
| `registry_open` | OpenAsync, including pooled connection acquisition |
| `registry_execute` | ExecuteReaderAsync, including SQLite's initial stepping and any provider busy/locked retries |
| `registry_reader` | ReadAsync, schema check, typed column access and freshness construction |
| `registry_projection` | ServerStatus response-object construction |
| `server_headers` | Status middleware entry through response headers starting (includes response preparation, excludes subsequent body writing) |

The four registry stages are disjoint, but do not sum exactly to registry: command setup,
disposal, instrumentation and intervening code remain in the aggregate.
ExecuteReader may perform actual row access; these are provider API boundaries,
not separate SQLite CPU and lock-wait measurements. The existing CodexServer.Status
meter also exposes `codex.server.status.stage.duration` (ms), tagged only with the
four bounded stage names. Failed/cancelled stages record their elapsed time;
failed reads may omit stage headers. No per-request log is emitted. The existing
`codex.server.status.duration` remains full Server processing through response writing, not client RTT.
The capture reports `server_headers` per request alongside registry and RTT; this is
a lower bound on full Server processing. Export the existing histogram for full
processing duration rather than treating the header as equivalent.

Once per initialization, the registry logs queried `PRAGMA journal_mode` and
`PRAGMA busy_timeout`, plus actual connection DefaultTimeout and command
CommandTimeout in seconds. These diagnostics contain no database path or SQL
values. The busy timeout pragma alone does not describe the provider's managed
busy/locked retry deadline; record both timeout values. Startup values describe
that connection at that time, not proof of an absence of subsequent contention.

### Repeatable capture

1. Record commit/build, runtime/provider versions, OS/kernel, CPU/memory, Server
   process start time, deployment/proxy/TLS topology, dashboard browser/version,
   client location and background load. Record the registry's operator-known
   location and mount/storage type (local SSD, network filesystem, container
   volume, etc.) in the private capture notes; do not publish sensitive paths.
   Record database size, project/Worker/execution counts and the startup SQLite
   diagnostic. Do not substitute this checkout's filesystem for deployment storage.
2. Keep the same dashboard pages/tabs, polling cadence and normal Worker heartbeat,
   lease and execution reporting workload across before/after runs. Record active
   clients, Worker count, write rates and workload transitions. Do not invent
   production writes or hold a production lock for this measurement. Use a
   disposable deployment for a separately labeled induced-lock control if needed.
3. Run `python3 tools/capture-status.py https://SERVER --count 300 --interval 5
   --output warm.json` from the chosen client while that workload runs. It uses
   one HTTP/1.1 connection, verified TLS, a 60-second request timeout and nearest-rank
   p50/p95/p99. RTT includes connection establishment when needed and reading the
   entire body. It prints a JSON summary and stores timestamps, HTTP codes and durations, never response
   bodies or URLs, and emits no poll log stream. Keep raw capture artifacts with
   the environment notes. Run independent repetitions; do not pool distinct loads.
4. For process-cold samples, restart the disposable Server between captures and
   capture the first request with `--count 1 --interval 0`; collect at least 30
   independent starts under the same startup/load procedure. Merge their samples
   using the same nearest-rank calculation. State explicitly that process-cold
   is not OS/page-cache-cold. OS-cache-cold requires a separate controlled host
   procedure; do not silently drop caches on a shared host. Include every first
   request rather than discarding startup outliers.
5. Export the existing HTTP histogram and the stage histogram over the same UTC
   window. Align slow samples with existing write/transaction diagnostics or a
   bounded hosting/provider trace. Compare idle and normal-write windows. Record
   whether a write transaction overlaps each tail sample and whether there is
   direct evidence of a busy/locked wait. Overlap alone is correlation; elapsed
   ExecuteReader time alone cannot establish a lock wait. If wait evidence is
   unavailable, report it as unknown, not zero.
6. Report attempted/successful samples, non-200/transport failures, readiness
   failures, and missing timing headers separately. The tool aggregates only
   HTTP 200 responses; inspect readiness independently since not-ready also uses
   HTTP 200. Do not label this probe's RTT as browser RTT: capture browser/CDP
   timings separately for the actual dashboard if that distinction matters.

| Required distribution (n, p50/p95/p99 ms) | Warm normal writes | Process-cold normal writes |
| --- | --- | --- |
| Registry aggregate | Pending | Pending |
| Open / execute / reader / projection | Pending | Pending |
| Full client HTTP round trip | Pending | Pending |
| Full Server processing histogram | Pending | Pending |
| Write overlap / verified lock waits | Unknown | Unknown |

Identify the dominant component using paired tail samples as well as percentiles;
independent stage percentiles cannot be subtracted or summed to explain aggregate
p99. If aggregate residual dominates, investigate that residual before proposing
an optimization. Retain the same workload and capture method for 30.5.2.

### Operator configuration and local validation

`/api/status` is public and requires no management token or browser session. Use
`Server:ListenUrl` (environment variable `Server__ListenUrl`) or the deployed
reverse proxy origin as the URL; the tool appends `/api/status`. For example:

```sh
python3 tools/capture-status.py https://server.example --count 300 --interval 5 --output warm.json
```

No Server configuration change is required. HTTPS uses the system trust store;
configure the deployment CA there if necessary. Do not put credentials in the URL;
the tool rejects URL credentials and does not follow redirects. Run from a host
that can reach the Server origin. A proxy authentication challenge is reported as
a failed request; use the reachable Server listener within the trusted network.
Missing or invalid stage headers are counted separately for each successful
response. Non-200 and transport errors are excluded from all percentiles.

Local checks (no operator deployment required):

```sh
dotnet test tests/CodexWorker.Tests --filter FullyQualifiedName~CodexServerTests
python3 tests/capture-status-tests.py
```

The HTTP Server regression uses a temporary SQLite database and verifies stage
projections; an exact-value unit test verifies that 1,420 ticks formats as
`dur=0.142`. The Python test uses a loopback test Server and checks percentiles,
missing/malformed headers, non-200 responses and transport failures.

### Checkout validation environment

Local development measurements do not describe deployment latency. The operator's
managed or standalone deployment capture remains an outstanding field measurement,
not an implementation prerequisite. No production distribution or latency dominance
has been established.

## 30.5.2: bottleneck decision and next capture

No request-path cache or background persistence probe is introduced. The supplied
observation bounds the registry component at 0.142 ms for that request; it does
not locate the remainder of the reported roughly 260 ms response. In particular,
there is no paired full Server processing observation. Neither SQLite dominance
nor a latency boundary outside Server processing has been established. Claiming
an improvement, or replacing the transactional counters with a memory projection,
would therefore be speculative. Deployment latency remains unresolved.

The capture tool now reports `http_after_headers_ms` for each successful response
with a valid `server_headers`, and its nearest-rank distribution. This is the
paired difference `http_ms - server_headers`, not a subtraction of independent
percentiles. It includes body writing/transfer, connection and proxy overhead;
it is **not** network time or full Server duration. Small negative values can
occur from timing precision and are retained rather than silently clamped.
Missing header samples are excluded from that distribution. Existing registry,
header and HTTP measurements retain their original scopes.

The concrete operator next step is two captures under the repeatable protocol
above: one from the affected client through the deployed proxy/TLS route and one
on the Server host directly against its configured listener. Keep build, dataset,
load, sample count and interval equal and record the UTC windows. Export
`codex.server.status.duration` for each same window, including sample count and
p50/p95/p99; preserve individual slow-request hosting traces to avoid inferring
per-request boundaries from separate histograms. Also record readiness failures
separately. Compare all three boundaries:

- Registry dominates full processing: inspect stage timings and verified provider
  waits before changing the registry read.
- Full processing dominates registry: trace middleware, response preparation and
  body writing before changing persistence.
- Client RTT dominates full processing: compare direct-listener RTT with proxy
  RTT, then capture proxy upstream/total timing and client connection/TLS/TTFB
  timing for slow samples. Only this evidence supports an operator-side boundary.

No field samples or local Server latency distributions were captured for this
change. The local build waited on frontend dependency setup; the focused .NET
check stopped at NETSDK1004 (missing restored test assets), and Python HTTP tests
could not create a loopback socket in the sandbox. These are validation limits,
not latency evidence. The Worker runs its authoritative checks separately.

Regression coverage adds rollback and store reinitialization checks against the
persisted committed markers, and verifies that a successful readiness observation
does not survive schema invalidation or a subsequent SQLite read failure.
Existing coverage exercises committed project, Worker and execution mutations.
Authorization, scheduling authority, process health and per-request persistence
checks remain unchanged; there is no stale-ready probe window.

## 30.5 parent integration review

The integrated status path retains the transactional SQLite freshness counters and
per-request schema/readiness observation. Committed mutations advance persisted
markers; rolled-back mutations do not, and reinitializing the store reads the
same committed markers. Schema invalidation and subsequent read failure cannot
reuse an earlier successful readiness result. See `CodexServerTests` for these
regressions. No memory cache, background probe or scheduling authority was added.

Execution display summaries reuse `github_issue_reads`, keyed by normalized
repository and Issue number. Successful full snapshot reads commit the title
with the snapshot. `ExecutionIssueTitles` joins the requested keys in one local
SQL batch, independently of snapshot expiry/invalidation; a cached title replaces
an older persisted title, while a cache miss retains a persisted title or null.
Repository aliases share metadata, different repositories do not. This policy is
deliberately display-only: it does not refresh eligibility or authorize scheduling.
The dashboard `IssueTitle` consumes the projected title and renders the localized
title-unavailable fallback without an Issue request. Opening an Issue detail view
continues to use the full read contract.

The parent regression `RepeatedExecutionPagesReuseDurableSummariesAcrossRepositoryAliases`
defines a reproducible workload: 50 executions across two repository aliases,
one full Issue read, store reinitialization, then five 50-row list reads. Its
request-count assertions require one upstream batch before and after all five
pages (zero additional GitHub requests), with normal cache misses on the other
rows. The dashboard regression renders five concurrent groups of 50 titles with
an Issue-read hook that throws if called. These are deterministic contract
checks, not captured deployment request counts.

Parent checkout validation on 2026-10-26 was environment-limited: focused .NET
tests stopped at missing restored assets (NETSDK1004); the full repository
validation command `python3 tools/validate.py --base origin/main --full` reached
solution restore but NuGet access was denied (NU1301). The dashboard title test
could not load the uninstalled `esbuild` dependency. Capture-tool tests could not
open loopback sockets under the sandbox. These attempts do not establish passing
tests; Worker validation remains authoritative.

Field latency and browser/upstream request-count captures remain outstanding.
Use the operator command and paired direct-listener/proxy protocol above, and
record Issue-detail and upstream batch counts for the same five-page workload
before and after deployment. The 0.142 ms sample does not identify a responsible
code-controlled contributor to the roughly 260 ms RTT. No measured latency
improvement or completed bottleneck diagnosis is claimed by this integration
review; that acceptance item still requires the field capture.

## 30.6: end-to-end diagnosis

**Field capture pending; no Server bottleneck or latency reduction established.**
The supplied single response reports `registry=0.148 ms`, `open=0.002 ms`,
`execute=0.102 ms`, `reader=0.004 ms`, `projection=0.001 ms`, and
`server_headers=0.192 ms`. `0.100` is **0.100 ms (100 microseconds)**;
100 ms is `100.000`. `registry_execute` times the SQLite provider's
`ExecuteReaderAsync` call, including initial stepping and possible provider
wait/retry work, not just SQLite CPU time. These observations do not explain the
roughly 260 ms client report: no paired full Server duration was supplied.
Do not subtract those observations from an unrelated client sample.

The #397 capture tool now separates three disjoint client boundaries, all in ms:

| Report key | Scope |
| --- | --- |
| `connect_ms` | Explicit connection establishment, including DNS, TCP and TLS for HTTPS; on a reused connection only the connection-check overhead |
| `request_headers_ms` | Sending the request through parsing all response headers; includes Server work, proxy and network waits, and may include reconnect work if a reused socket fails |
| `body_read_ms` | Reading the remaining body after header parsing until EOF/content completion, discarding chunks |
| `http_ms` | Sum of the above boundaries, including the complete response body |

`connection_established` identifies samples that explicitly establish a connection.
The default remains connection reuse; `--connection fresh` forces establishment
for every sample. Connection setup combines DNS/TCP/TLS; it cannot distinguish
those individually. Body bytes may already be buffered while headers are parsed;
`body_read_ms` is client receive completion time, **not Server body-writing time**.
The Server histogram measures middleware entry until downstream request handling
returns, including serialization and response writes, but excludes transport drain
and client receipt. `server_headers` stops at headers starting. Neither boundary
covers the full round trip. No Server request logging or new Server stages are
needed until a field capture demonstrates a material gap there.

### Matched field procedure

Use the environment/workload inventory above. Run these two probes concurrently
(or in repeated matched windows with unchanged load), from their stated hosts:

```sh
# Affected client, through the same proxy/TLS origin as the slow route:
python3 tools/capture-status.py https://server.example --count 300 --interval 5 --output route-reuse.json
# Server host, against its actual configured listener (example only):
python3 tools/capture-status.py http://127.0.0.1:5000 --count 300 --interval 5 --output listener-reuse.json
```

Use matching build, dataset, normal workload, count, interval and connection mode.
Repeat both commands with `--connection fresh` and separate output files to test
connection setup. Keep fresh and reused populations separate. Preserve report UTC
windows and private topology notes. Do not change listener exposure or bypass TLS
verification to run the probe. The tool sends no credentials, follows no redirects,
prints one summary and saves no URLs, bodies or sensitive exception text. If the
actual browser remains slow while this probe is fast, capture its queueing,
connection/TLS, TTFB and content-download timings using browser network tools.

During **each same UTC window**, export `CodexServer.Status` instruments
`codex.server.status.duration` and `codex.server.status.stage.duration` from the
operator's metrics/diagnostics collector. This checkout declares meters, not a
built-in HTTP metrics exporter; the capture CLI cannot obtain the Server histogram
from response headers. Retain count, units, window bounds and histogram buckets
(or raw observations), then calculate window p50/p95/p99; label bucket-derived
quantiles approximate. Use window deltas for cumulative buckets, never lifetime
percentiles or an average in place of quantiles. If the collector exports only
interval quantiles, retain those intervals; do not average their percentiles into
a window percentile. Report unavailable exports explicitly. Server counts can
include dashboard/other probe requests and failed requests, unlike the tool's
HTTP-200-only distributions; inventory that traffic before comparing populations.

For selected slow requests, retain a bounded hosting/proxy trace correlated using
the existing `X-Codex-Request-Id` and UTC timestamps, with per-request Server
processing and proxy upstream/total duration. Avoid payload/header dumps and
credentials. A histogram alone cannot pair requests. Compare paired durations
where available; never subtract independent p95/p99 values to assign a component.
The existing `http_after_headers_ms` remains a paired boundary residual, not a
network measurement.

### Report to complete after capture

Each distribution cell must contain **n; p50 / p95 / p99 in ms**, separately for
reuse/fresh and each workload window. The tool's `summary` supplies nearest-rank
client and header distributions; metric export supplies full Server processing.

| Distribution | Affected route | Direct listener |
| --- | --- | --- |
| Client RTT (`http_ms`) | Pending | Pending |
| Connection setup/check | Pending | Pending |
| Request through headers | Pending | Pending |
| Remaining body receive | Pending | Pending |
| Full Server processing histogram | Pending | Pending |
| `server_headers` | Pending | Pending |
| Registry aggregate | Pending | Pending |
| Registry open | Pending | Pending |
| Registry execute | Pending | Pending |
| Registry reader | Pending | Pending |
| Registry projection | Pending | Pending |
| Paired RTT minus Server headers | Pending | Pending |

Also report attempts, successful responses, HTTP failures, transport failures and
missing/invalid headers per stage separately (provided by the CLI). Observe
readiness failures independently: HTTP 200 does not guarantee ready, and the tool
intentionally discards the response payload. Include missing Server exports and
unpaired slow samples instead of treating them as zero.

Use this evidence to choose the next action:

- High connection setup: split DNS/TCP/TLS in client diagnostics and inspect
  connection reuse, resolver and TLS termination with the operator.
- Low full Server processing and low direct RTT, high routed RTT: inspect proxy
  upstream/total timings and client/network traces for the matched slow requests.
- High Server processing after headers: trace serialization and response writes,
  including downstream backpressure; client body receive alone cannot identify it.
- High Server processing before headers: inspect middleware/preparation and the
  measured registry stages before changing the responsible code.

The measured boundary currently established is only the sub-millisecond registry
and header sample. The two field windows, Server histogram exports and paired
slow traces are still required to locate the remaining delay. They are operator
follow-up, not a prerequisite for this local implementation. No speculative
SQLite, cache or persistence change is introduced. Exact-unit .NET assertions
cover both `0.100` and `100.000`; capture regressions protect client boundaries,
sub-millisecond parsing, fresh mode, failures and missing headers. Local test
fixtures are validation, not deployment performance measurements.
