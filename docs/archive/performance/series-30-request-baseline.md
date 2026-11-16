# Series 30: dashboard request baseline

## Scope and evidence

Audit date: 2026-10-26. This is a diagnostic audit of the current React Server dashboard, not a polling or caching change. Paths below are relative to `src/CodexServer/worker-poc/src` unless prefixed with `src/`. The legacy Worker dashboard is covered separately below.

**Evidence level:** intervals, dependencies and rates below are derived from source, not an observed production session. No representative live Server/browser session was available in this checkout. There is no measured latency distribution or measured GitHub request count. Do not use these estimates as measured performance results or claim the measurement acceptance criteria are complete.

Local setup: Linux checkout, Node 24.21.0; no dashboard `node_modules`, no resolvable Playwright, no supplied dashboard URL/session or deployment dataset. `npm ci --ignore-scripts --offline` failed with `ENOTCACHED` for zustand 5.0.11. No credentials, private Issues or live providers were accessed. Live sample duration, project count, Worker count and execution count: **not measured**. The reproducible workload below specifies these explicitly for the follow-up capture. Dependency installation is a local validation limitation; production traffic evidence remains outstanding.

## Refresh ownership and triggers

| Source / code reference | Trigger and cadence | Consequence |
| --- | --- | --- |
| `shared/api/runtime.ts:createQueryClient` | retry false; staleTime 0; gcTime 0; focus/reconnect/interval refetch false | No React Query retry loop or built-in interval. Successful data is immediately stale; remounts can fetch again, and unused queries are discarded. |
| `shared/api/session.tsx:useApiRead` | Authenticated mount, path/query-key change, explicit refetch | Cache identity is session generation + exact path. Equal keys share a query in a tab; different limits, offsets or query strings do not. |
| `shared/api/runtime.ts:startLive` | First poll after 5 s, then 5 s after awaiting active-query refetches | Every active private query is polled except exact `/api/v1/workers` while stream state equals `Live · connected`. Refetches in a round can run concurrently; rounds await completion. Effective round period is 5 s + slowest participating request. `cancelRefetch:false` reuses an in-flight query rather than restarting it. |
| Same runtime, event stream | `/api/v1/events/stream` once per tab; retry 3 s after termination/failure | A healthy stream replaces only Worker inventory polling. It does not replace status, nodes, executions, projects or Issue reads. Invalid event processing can stop the stream loop. |
| `shared/api/runtime.ts:mutate` | Successful mutation | Cancels queries then invalidates the whole session prefix, refetching active reads across the view. Fresh uncached authoritative reads also precede mutations. Do not count them as timer duplicates. |
| View buttons and forms | Refresh buttons, pagination, Issue filter changes, preview/verification, reconciliation | Explicit refetch/read or a new key. Search/filter operations done locally do not inherently issue HTTP. Preview and mutation calls are user initiated, not periodic writes. |
| `shared/api/session.tsx:SessionProvider` | Mount GET session; pagehide resets/aborts; persisted pageshow restores session | Back/forward cache restore starts a new generation and remount reads. Session expiration resets rather than silently renewing. Ordinary tab visibility change has no listener. |
| `features/workers/WorkerJournal.tsx:JournalRead` | Open panel: mount read; pending result: 2 s timer; 80 s UI timeout | Additional polling overlaps the shared 5 s owner. The global query stays active even after terminal journal data or the UI timeout until panel unmounts. |
| Enrollment, node consent, Server preparation and connection challenge timers | Local expiration/deadline timers | Update/redact local UI state; these timers are not periodic network refreshes. |

Audit searches included all `setTimeout`, `setInterval`, `useQuery`, `useApiRead`, `useEffect`, refetch/invalidation and page lifecycle handlers in the dashboard source. The direct `useQuery` callers in maintenance and Server connection use the same runtime key prefix and therefore participate in the shared poll. There is no visibility pause, cross-tab query cache, or cross-tab stream owner.

## Recurring endpoint and downstream map

All rows use mount + shared polling unless a different trigger is stated. Parameters are significant: preserve the full URL in counts.

| View / source | Recurring endpoints beyond shared status | Downstream work |
| --- | --- | --- |
| Shell: `shared/ServerStatus.tsx` | `/api/status` | In-memory `ServerStatus`; no database, Worker RPC or GitHub. |
| Home: `features/home/HomePage.tsx`, `features/server/useServerReadiness.ts` | `/api/v1/workers`, `/projects`, `/executions?limit=50&offset=0`, `/nodes`, `/nodes/server/github-connection` (all under `/api/v1`) | Registry reads; nodes performs capability discovery/health, provisioning-plan and Worker reads, then command reads per node. Connection reads command metadata, not a GitHub authentication probe. Execution titles add Issue endpoints below. |
| Projects list: `features/projects/ProjectsPage.tsx` | `/api/v1/projects`, `/api/v1/executions?limit=50&offset=0` | Registry only; recent activity is fetched even on the detail route. List displays reference numbers without IssueTitle lookups. |
| Project detail: `features/projects/Detail.tsx`, `Readiness.tsx`, `Issues.tsx` | Parent list queries plus Workers, nodes, `/executions?projectId=P&limit=50&offset=0`, per-Worker `/workers/W/diagnostics`, `/projects/P/github/access`; opened Issue list/detail adds `/projects/P/github/issues?...` or `/issues/N` and queue `/executions?projectId=P&workType=github-issue&limit=100&offset=0` | Diagnostics derived from registry Worker/projects/plans/commands. Access and Issue list hit GitHub read services; list enumerates numbers before snapshot retrieval. Detail readiness and table titles can add subscriptions. |
| Workers list: `features/workers/WorkersPage.tsx` | `/api/v1/workers`, `/api/v1/nodes` | Registry + node dependencies above. |
| Worker detail: `features/workers/WorkerDetailPage.tsx` | Workers, nodes, projects, executions limit 50, `/workers/W/diagnostics`, `/nodes/W/commands` | Registry/command reads, no direct Worker RPC. Open editor adds `/workers/W`; open journal adds `/workers/W/journal`. |
| Worker administration: `features/workers/WorkerPocPage.tsx` | `/workers/W`, `/workers/W/diagnostics`, `/api/version`, `/workers/W/credential-access` | Registry/configuration/credential assignment metadata; version in memory. No secret delivery from metadata reads. |
| Executions list: `features/executions/ExecutionsPage.tsx` | Filtered `/api/v1/executions?...`, projects, Workers, nodes | Registry list plus IssueTitle per distinct rendered project/Issue. |
| Execution detail: same file | `/api/v1/executions/E`, projects, Workers, nodes | Registry; rendered IssueTitle lookup; maintenance section adds Worker-filtered maintenance history and selected operation detail. |
| Issue detail/history: `features/projects/IssueHistoryPage.tsx` | `/projects/P`, `/projects/P/github/issues/N`, Workers, two `/executions` queries scoped to project/work type/work ID, with limits 25 and 100 | Registry history/status reads are separate URLs, even at offset zero. Issue read uses GitHub snapshot provider. See `issue-history-model.mjs:issueHistoryPath/issueStatusPath`. |
| Execution maintenance: `features/executions/ExecutionMaintenancePage.tsx` | Projects, Workers, nodes, status, `/maintenance/executions?limit=50&offset=O`, latest offset=0, selected `/maintenance/executions/ID` | Persistent maintenance command history/detail (`src/CodexServer/ExecutionMaintenanceApi.cs`), not a recurring remote cleanup. At offset zero history/latest share a key; on later pages both poll. |
| Settings: `features/settings/SettingsPage.tsx`, `ServerPreparation.tsx` | `/credentials`, Workers, selected `/credentials/ID`, nodes, `/nodes/server/commands`, `/nodes/server/github-connection` | Protected store metadata and provisioning command history, not credential payloads. `useServerConnection.ts` uses a `transient-view` suffix; another ordinary observer of the same URL would have a different key. |
| Node preparation: `features/nodes/NodeProvisioning.tsx` | Nodes and `/nodes/ID/commands` | Cached discovery/registry and persisted command observations. Command submission is explicit. |
| IssueTitle: `features/projects/IssueTitle.tsx:IssueTitleRead` | `/api/v1/projects/P/github/issues/N` for every distinct rendered valid GitHub Issue identity | Full Issue snapshot for a title; shared with same-key detail observers, but no browser batching across Issues. |
| Stream: `src/CodexServer/ServerApplication.cs:StreamWorkerUpdatesAsync` | One long-lived HTTP request per tab; initial snapshot then 5 s ticks | `GetWorkersAsync` and full serialized Worker snapshot on every tick, including unchanged snapshots. Database work continues with zero Workers. |
| Journal: `src/CodexServer/WorkerJournalApi.cs` | Browser GET panel read | Registry availability check and ephemeral journal request/result; Worker separately polls `/journal/request` and posts result. Not a synchronous browser-to-Worker HTTP proxy. |

Server route evidence: `src/CodexServer/ServerApplication.cs` lines 175 (nodes), 294 (status), 628 (Workers), 639 (diagnostics), 662 (stream), 684 (projects), 695–756 (GitHub reads), 931–947 (execution list/detail), and 1005 (stream implementation). Registry-backed execution lists do **not** themselves call GitHub. The fan-out originates in rendered title components.

## Code-derived request budget

Define `U` as the number of distinct `(projectId, Issue number)` title queries currently mounted, not execution row count. Repeated executions of the same Issue share a query; two project aliases for one repository do not share a browser key. Let `L` be the slowest request duration in a poll round, in seconds. Approximate steady-state rate per active query is `60 / (5 + L)` requests/minute, bounded above by 12. These are steady-state estimates excluding initial loads, mutations, stream reconnects and manual refreshes; finite one-minute boundary counts can differ.

| View, healthy stream | Polling queries | Upper steady-state HTTP requests/minute |
| --- | ---: | ---: |
| Home | status + projects + executions + nodes + connection + U titles | `60 + 12U` |
| Projects list | status + projects + executions | 36 |
| Workers list | status + nodes | 24 |
| Worker detail, editor/journal closed | status + nodes + projects + executions + diagnostics + commands + U | `72 + 12U` |
| Executions list | status + projects + nodes + execution list + U | `48 + 12U` |
| Issue history/detail | status + project + Issue + history + status executions | 60 |

Each row also opens one stream, with approximately 12 Worker database reads/minute plus its initial read. Initial Worker GETs can occur before stream connection. A disconnected stream adds up to 12 Worker GETs/minute where observed and approximately 20 failed stream connections/minute if failures return immediately. For example, Home with 10 executions for 10 distinct Issues implies up to 180 polling HTTP requests/minute plus one stream; 10 executions for one Issue imply 72. Issue history alone implies 24 execution-list calls/minute (two limits), versus 12 on Home. Project detail also retains the parent global execution list alongside scoped lists.

Two healthy Home tabs with U=10 imply up to 360 polling requests/minute, two streams and approximately 24 stream-driven Worker database reads/minute. Client deduplication does not cross tabs. Two different views in separate tabs add their budgets. Same-key subscriptions within one React root deduplicate concurrent fetches, but stale remounts can cause successive GETs. No measured duplicate/in-flight count is available.

### GitHub fan-out and cache qualification

The registered snapshot path is `ServerGitHubAdministrationService.GetIssueAsync` → project registry lookup → `GitHubIssueReadProvider.GetIssueAsync/GetSnapshotsAsync` → `ServerGitHubSnapshots.ReadSnapshotsAsync` → `gh api graphql`. The route also checks the project, so Issue GETs have registry work even on cache hits. Provider cache access uses SQLite and a shared semaphore; a slow GitHub miss can delay other Issue reads behind that gate and prolong the entire browser poll round, including the next cheap status refresh.

`GitHubIssueReadProvider.cs` lines 168–197: ordinary snapshots live for 30 seconds; closed Issues with the completion label live for 30 days. Metadata and relationships share snapshots. Missing snapshots in one provider call are batched in tens. Separate per-title HTTP calls usually arrive as singleton provider calls, so 10 title requests do not automatically form one batch. Repository-keyed persistence shares transport snapshots across project aliases and tabs, subject to expiration and invalidation. Mutations can invalidate repository snapshots; failures/missing Issues must not be assumed to have the same hit rate as successful reads.

For one unique ordinary Issue, upper browser demand is 12 detail GETs/minute; with successful regular reads, an unforced warm snapshot generally requires about 2 GitHub snapshot fetches/minute. A closed/completed Issue normally requires zero warm upstream fetches over a short capture. Ten distinct open Issues therefore imply up to 120 detail GETs/minute and roughly 20 upstream snapshot calls/minute, plus cold loading. These are conditional estimates, **not observed GitHub traffic**. A cache miss needs at least one GraphQL invocation; relationship pagination and reference hydration can add more (`ServerGitHubSnapshots.cs:ReadGraphConnectionAsync/ReadGraphReferenceAsync`). Issue lists add number enumeration plus batches of up to ten misses. Count actual transport calls and cache hits separately; neither HTTP detail count nor `gh` process count proves exact GitHub HTTP request count.

## Visibility, overlap and unexpectedly frequent sources

- Hidden tabs have no application visibility pause. Browser timer throttling may reduce cadence, but the stream still has its own Server timer. Reopening an ordinary hidden tab has no explicit focus refetch; resuming timers or stream interruption can produce fresh work. Actual browser throttling needs capture.
- `pagehide` aborts/reset state; a persisted `pageshow` rechecks session and reloads reads. This differs from merely hiding a tab.
- IssueTitle turns a title column into full metadata/relationship reads on every refresh round, even for completed executions. This is the main multiplicative browser source.
- Issue history deliberately polls two execution queries with different limits. Project detail retains global and project-scoped execution lists; Issue queue adds another scope. They overlap semantically without sharing query keys.
- Journal has two refresh owners. Its 80 s timeout only changes presentation; the active query remains in the shared loop. Successful responses also remain globally polled while open.
- The healthy stream still performs periodic full inventory database reads; “one HTTP request” does not mean one database read. Node inventory similarly expands into command queries for Server plus every Worker.
- Global mutation invalidation can refresh unrelated metadata. Equal-key component subscriptions are not evidence of duplicate HTTP, but remounts with zero stale/cache retention warrant measurement.

## Legacy Worker dashboard

`src/CodexWorker/dashboard.html` lines 28–44 uses a separate local Worker API. `refresh()` immediately and every 10 seconds fetches `/api/status`, `/api/projects`, `/api/executions?limit=30`, `/api/events?limit=30` and `/api/worker/drain` concurrently: 6 requests/minute per endpoint, 30 total, excluding initial load. A native EventSource connects to `/api/events/stream`; each subscribed lifecycle event first fetches events then calls the five-request refresh, adding six HTTP requests per event (including two event-list reads). Explicit lifecycle/configuration actions also refresh. There is no in-flight refresh gate or visibility pause; slow refreshes and bursts of lifecycle events can overlap. EventSource reconnects are browser managed. These endpoints present Worker local status, configuration, history and events rather than the Server registry or per-row GitHub lookups. Keep Worker and Server dashboard captures separate.

## Comparable live capture protocol (outstanding)

Use a disposable, authorized environment with synthetic Issue text. Record commit/build, browser/version, host CPU/memory, database size/provider, proxy/network topology and Server/GitHub cache state. Suggested reproducible workload: 2 projects, 2 online idle Workers, 10 retained executions referring to 10 distinct open synthetic Issues; repeat with 10 executions for one Issue and with closed/completed Issues. Disable automatic discovery for the controlled run, then separately measure the normal deployment with background discovery/Worker traffic identified by origin. Do not change polling code for the baseline.

1. Warm each view for 30 s, then capture 5 minutes each: idle Home; Projects list/detail with Issue list open; Workers list/detail (journal closed then pending/ready); Executions list/detail; Issue history at offset zero and next page; maintenance and Settings/preparation. Also record cold navigation separately.
2. Capture a 5-minute hidden interval and 1-minute reopen interval; record actual `document.visibilityState`. Repeat two Home tabs and Home + Executions tabs. Separately test pagehide/back-forward restoration. Label connected and disconnected stream runs.
3. In browser DevTools/CDP record method, normalized full endpoint, monotonic start/end, status, initiator, cancellation, response bytes, and overlap. For streams record connection lifetime, reconnect count and event timestamps rather than waiting for a response end. Count identical endpoint starts while an earlier request is unfinished, and successive starts within one second, separately. Normalize IDs in exports while retaining stable pseudonyms and query parameters.
4. Collect Server request processing duration with request IDs using existing hosting diagnostics or temporary local tracing, including response serialization; collect registry operations, snapshot cache hits/misses, GitHub transport invocations/pages and Worker protocol traffic. Correlate IDs/timestamps with browser requests. Avoid recording headers, request bodies, Issue contents or raw `gh` output. Use consistent timing boundaries: Server endpoint-only duration is not equivalent to full middleware request duration.
5. Report browser RTT and Server processing independently for `/api/status`, execution lists, Issue detail and nodes: sample count, failures/cancellations, p50/p95/p99/max. RTT includes connection/queue/proxy/transfer/browser overhead; subtracting Server duration is only an approximate residual, not a precise network measurement. Keep cold and warm samples separate. Do not include stream lifetime in ordinary latency percentiles.

Required result sheet, currently unpopulated:

| Scenario / endpoint | Duration | Projects / Workers / executions / unique Issues | Count / requests per minute | Browser RTT p50/p95/p99 | Server duration p50/p95/p99 | In-flight duplicates / failures | GitHub calls / cache hits |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Live capture pending | N/A | N/A | N/A | N/A | N/A | N/A | N/A |

Status latency is therefore not yet quantified empirically. Code shows status processing is in-memory, while its refresh cadence is coupled to completion of slower active reads. Measure request latency and observation age separately: a fast `/api/status` can still be refreshed late. Preserve aggregate results with this document; keep any raw trace local and redact it before sharing.

## Prioritized follow-up for #395, #394 and #392

These are candidate work packages for the three child Issues; their detailed scopes are not present in this checkout, so confirm allocation against their descriptions before implementation.

1. Remove per-row full Issue-detail demand from execution title rendering; use bounded summaries/batching and appropriate reuse. Track detail GETs per unique Issue and upstream cold/warm fan-out. Preserve Issue eligibility checks and explicit authoritative mutation preflight.
2. Separate cheap status refresh from slow query rounds; apply resource-specific refresh policies, consolidate overlapping execution-list demand where semantics permit, and limit hidden-tab/background work. Compare status observation age as well as RTT. Preserve session expiry, cancellation and uncertain-operation fences.
3. Consolidate journal refresh ownership and stop terminal polling; narrow mutation invalidation; assess stream snapshot/database cost and node command-query expansion. Verify same-tab deduplication and cross-tab multiplication before adding coordination complexity.

For each of #395/#394/#392, reuse the exact workload and result sheet above. Record changes to request counts, unique-Issue fan-out, browser RTT, Server processing, failures and observation age. Avoid declaring performance improvement from source estimates alone. No production behavior, configuration, ownership, security boundary or product version changes are included here.

### Execution title snapshots (30.4)

Execution titles now come from `workReference.title`, captured from the Server's
verified GitHub read when enqueuing and persisted with the execution reference.
Browser `IssueTitle` rendering performs no reads. Cold, warm, repeated and
concurrent execution-list rendering therefore adds **zero** Issue-detail HTTP or
upstream GitHub requests, regardless of row count. The registry list routes remain
GitHub-independent. Historical references without a title display their Issue
number and the localized unavailable-title text; snapshots remain useful during
GitHub outages and do not expire or claim to describe current Issue status.
No additional summary cache, background refresh, or browser GitHub access is used.

Opening the Issue page or using its refresh controls reads the Server Issue-detail
endpoint, which explicitly refreshes that Issue through the existing read provider.
Other reads retain the existing repository-scoped freshness and batching policy.
Mutation eligibility checks remain authoritative. These are code-derived budgets;
no live browser/GitHub performance campaign was run for this change.

## Parent integration review (#391)

Review date: 2026-10-26. The preceding audit describes the **before** implementation;
its polling descriptions are not the current runtime policy. The 30.2, 30.3 and
30.4 changes are present in this checkout. This review adds regression coverage
and fixes recovery of failed initial collection loads.

### Comparable request budgets

The following comparison uses the audit's same one-minute steady-state window,
healthy visible tab, unchanged resources and `U=10` distinct execution Issues.
These remain **source-derived budgets, not measured browser traffic**. Initial
mount reads, user actions, changes and reconnects are excluded from both columns.

| View | Before HTTP requests/minute | After HTTP requests/minute |
| --- | ---: | ---: |
| Home | 180 | 12 status |
| Projects list | 36 | 12 status |
| Workers list | 24 | 12 status |
| Worker detail, editor/journal closed | 192 | 12 status |
| Executions list | 168 | 12 status |
| Issue history/detail | 60 | 12 status |

Home's unchanged steady-state budget falls by 168 requests/minute (93.3%).
The former stream is no longer opened by the React runtime, removing its twelve
periodic Worker inventory reads per minute per tab. Execution title rendering
adds zero Issue-detail reads and zero upstream GitHub calls, compared with up to
120 detail GETs and approximately 20 warm upstream snapshot calls per minute for
10 ordinary Issues in the audit's conditional example. Explicit Issue navigation
and refresh still use the existing GitHub read service.

Reproduction: after installing dashboard dependencies, run
`cd src/CodexServer/worker-poc && node --test tests/status-cache.test.mjs tests/issue-title.test.mjs`.
The Home budget test mounts the five resource queries and completes twelve status
rounds, asserting twelve status requests, one initial request for each resource,
and no Issue-detail requests. It does not render a browser or measure latency.
The title test renders fifty rows repeatedly with a read hook that throws if used.
Other runtime tests cover same-key deduplication, project-filtered execution caches,
inactive cache invalidation, Server restart with repeated counters, visibility,
backoff, mutation scoping and retained data after refresh failure. Failed initial
collection reads now also retry through the status loop, so a transient mount
failure cannot strand a view indefinitely when counters remain unchanged.

### Freshness and ownership review

- Status reads schema metadata and three trigger-maintained SQLite counters in
  one query. It does not deserialize collection payloads or call GitHub, Worker
  RPC, node discovery or provisioning. `Server-Timing: registry;dur=...` exposes
  registry processing duration for the live capture. Existing Server tests cover
  persisted counters, collection-independent reads and the timing header.
- The authenticated visible tab checks status every five seconds after a round
  completes. Unchanged counters do not refresh collections. Changed counters
  invalidate the relevant category, refresh active collections and leave inactive
  entries stale for navigation. Restart identity invalidates all three categories.
- Keys retain the full path (including project filters and pagination) and session
  generation. Repository snapshot sharing stays in the existing GitHub provider;
  the dashboard adds no independent repository cache. Session reset clears reads
  and aborts in-flight work. Same-key requests share in-flight work within a tab.
- Details and non-marker resources refresh on entry or explicit refresh; collection
  freshness does not expand identifiers into GitHub/detail calls. Local mutations
  invalidate their affected projections immediately. Uncertain mutation fences and
  uncached authoritative preflight remain separate from observational caching.
- Hidden tabs pause scheduled checks and check on visibility restoration. Failed
  rounds retain query data/errors and use bounded exponential backoff with jitter.
  Failed active collection loads retry even without another counter change.

### Remaining trade-offs and validation limits

Markers are category-wide, so a change in one project can refresh active execution
collections for several project filters. Worker heartbeats can change the Worker
marker even without an administrative change. Tabs have independent caches and
status loops; there is no cross-tab leader. Slow changed-collection refreshes still
extend the next status interval. Already-open details and non-marker resources
require navigation or explicit refresh; the journal retains its separate bounded
pending-result timer. The legacy Worker dashboard is outside this Server runtime
change and retains the behavior documented above.

No live Server/browser dataset or GitHub transport instrumentation was supplied;
therefore before/after p50/p95 endpoint RTT, registry timing, observation age and
actual external request volume remain **unmeasured**. The reported 200–300 ms
status latency has not been reproduced or claimed fixed numerically. Complete the
existing live capture protocol on both revisions before claiming empirical
performance acceptance. No wall-clock latency threshold is added to deterministic
tests.

Local validation in this review: dependency installation failed offline because
zustand 5.0.11 was not cached, and online because registry.npmjs.org DNS resolution
failed (`EAI_AGAIN`). Focused dashboard tests consequently could not load esbuild.
The focused .NET test could not run without restored project assets; restore also
exited unsuccessfully in this environment. Worker-configured authoritative
validation remains separate and has not been reported as passed.
