# Isolated Worker detail React proof of concept

Open `/workers/{workerId}/poc` directly (using the same registered ID as
`/workers/{workerId}`). Reload restores the existing administration cookie and
CSRF session. The existing Worker detail and every other route retain their
ordinary dashboard assets. The PoC links back to the existing Worker preparation and administration page
and presents scheduling and Worker API-token controls through the existing
management contracts; no scheduling or readiness rules are introduced.

The Server renders its existing dashboard shell and login, inserting a React
mount and two same-origin asset references only for this explicit route.
`dashboard-navigation.js` recognizes the isolated route and ordinary links leave
it by full navigation, loading the ordinary dashboard again. The existing
session, pending-read generation, overview reads and single Worker SSE owner
supply snapshots to the React view, including the shared project, node, command
and bounded execution observations. The route also reads the existing Worker
diagnostics endpoint through the same authenticated, cancellable request owner. No additional fetch, token storage, polling,
SSE subscription or frontend router is created. Logout and unavailable Worker
observations clear the projection. React escapes displayed resource text.

## Build and package

Build machines require Node.js 22 or newer and npm alongside the .NET 10 SDK.
From the repository root:

```sh
npm ci --prefix src/CodexServer/worker-poc --no-audit --no-fund
npm run build --prefix src/CodexServer/worker-poc
dotnet build src/CodexServer/CodexServer.csproj
dotnet publish src/CodexServer/CodexServer.csproj -c Release -o /tmp/codex-server-poc
node --test --test-isolation=none tests/dashboard/*.test.cjs
dotnet test tests/CodexWorker.Tests/CodexWorker.Tests.csproj --filter FullyQualifiedName~ServerStartsAndExposesStatusHealthAndVersion
```

Server build/publish automatically runs lockfile-based `npm ci` when its inputs
change, then bundles production React and CSS with esbuild into the configuration's
intermediate output directory. Standalone `npm run build` writes `obj/worker-poc`
for frontend inspection; the Server uses its own configuration-specific outputs.
The JavaScript and CSS are embedded resources in the Server assembly, including
single-file/self-contained release packaging. The existing release scripts need
no extra asset-copy stage. Installed nodes need neither npm nor a frontend server
nor a CDN. Generated bundles and node_modules are not committed. Dependency
updates require reviewing the lockfile and rebuilding; no runtime downloads occur.

The only Untitled UI component is an adapted MIT-licensed `Badge`, pinned to
[upstream source revision 4702dc0](https://github.com/untitleduico/react/blob/4702dc0ea8d140c3491a85670c7b4fab47b722da/components/base/badges/badges.tsx).
It keeps the medium colored pill variant and four palettes, replacing Tailwind
utilities with CSS scoped below `#worker-poc`. This avoids Tailwind preflight or
theme changes to the existing shell. The upstream license is retained in
`worker-poc/UNTITLED-UI-LICENSE` and included in the JavaScript bundle, alongside
React's retained legal notices. No paid components or Untitled UI CLI are needed.
The [upstream installation model](https://www.untitledui.com/react/docs/installation)
uses source components rather than an all-components runtime package.

## Route, session and deployment boundaries

Only GET on `/workers/{workerId}/poc` and the two fixed
`/dashboard-assets/worker-poc.js` and `.css` paths is added. Unknown asset names,
extra path segments, API/protocol/health failures and unsupported methods never
fall back to dashboard HTML. Asset URLs serve public code without private data;
protected data remains behind the existing authorized APIs.

The checked-in nginx recipe defaults all paths outside its method-specific Worker
protocol allowlist to management-network-only access. Both this UI route and its
assets therefore remain VPN-only without broadening the allowlist. HTTPS origin,
cookie, CSRF and management authorization behavior are unchanged. The repository
currently sets no CSP in the Server or nginx recipe; deployments with their own
CSP need same-origin script/style sources in addition to whatever policy already
permits the existing inline dashboard. No eval, external fonts or external asset
origins are required.

The .NET endpoint regression checks embedded asset responses, deep navigation,
normal-route asset isolation, missing routes and rejected methods. The Node/VM
harness checks existing session restoration, one stream, read-only Worker
diagnostics, shared snapshot delivery, registry control evidence, logout clearing and ordinary-page exit. Focused model and React rendering tests use JSON-shaped Server fixtures to
cover Worker filtering, stage/outcome/timing presentation, empty/stale/failed
reads, escaping and safe Issue links. Install the locked frontend dependencies
before running the dashboard suite. These tests do not establish browser
layout/accessibility. For parent integration review,
use a disposable packaged Server behind the existing HTTPS/VPN proxy: sign in,
open the PoC, reload, check online/stale/deleted observations, sign out, and follow
its existing-detail link. Review keyboard focus, narrow viewport and long IDs;
verify the network panel requests only local assets and the existing API/stream.
No live provider actions, production deployment, release or version change is
part of this work.

## Informational projection and contract gaps

The PoC displays the friendly name above the secondary ID, connection availability,
Worker lifecycle, node execution prerequisites, scheduling policy and capability
observation freshness separately. Slot usage is the selected Worker's reported
active executions / maximum capacity (registered capacity is the fallback), with
available capacity and active Server assignments stated separately. No Server-wide
capacity or client-derived admission rule is used. Readiness evidence comes from
Worker diagnostics; node tool authentication is displayed independently.

Current executions and recent terminal outcomes are filtered by `assignedWorkerId`
from the shared latest 50 Server execution requests. At most 10 terminal outcomes
are shown. The existing API has no Worker filter, so older history and even active
work may fall outside this window. The screen states that limit and explicitly
calls out reported active work whose detail is absent. It is not a complete Worker
execution-history store. Execution links open the existing details page.

`WorkReference` has type, ID and URL but no Issue title. The screen states that the
title is unavailable and links the Issue number using a validated canonical GitHub
Issue URL, falling back to the central project's owner/repository identity. Deleted
or unavailable projects retain their execution project ID; missing link evidence
remains plain text. URLs must be HTTPS GitHub Issue paths for the same number, with
no credentials, query or fragment. No live GitHub enrichment is requested.

Only `currentStage` is displayed; the contract has no stage sequence or per-stage
completion evidence, so no completed/upcoming steps or progress percentage are
invented. Elapsed time uses `startedAtUtc` and the refresh time, and terminal duration
uses the reported duration or start/completion timestamps. Missing/invalid timing
is unavailable. Failed and cancelled outcomes retain their reported state and
recovery classification without inventing an outcome from diagnostics.

Capability cards show installation/version, health, authentication/configuration
(including not-applicable), update and detection evidence, observed operations,
registered available typed actions and pending/latest command status. Capability actions remain informational text; provisioning mutations stay in
existing administration. Diagnostic codes
and command IDs live in secondary disclosures; raw failure detail and process
output are not displayed. Stable React component keys preserve focused links and
expanded diagnostic disclosures across polling and stream updates. A failed read
clears its projection, and session teardown clears all snapshots.

For visual review, use the deterministic fixtures in
`tests/dashboard/worker-poc-fixtures.cjs` at desktop and 375px widths, with a long
Worker ID/name, stale observations, a pending/failed capability operation, and
unavailable reads. Check wrapping, text status, keyboard link/disclosure focus,
scroll stability after refresh and correct Issue navigation. The scoped layout
switches status, execution and capability grids to one column below 700px.

Local fixture review used Chromium with the existing shell CSS at 1280px and
375px. Both layouts and a long-name/stale/unavailable variant had no horizontal
overflow. Issue hrefs, focused links, open diagnostic disclosures and scroll
position survived snapshot updates. This fixture review does not establish a
live provider or deployed-session integration campaign.

## Scheduling and Worker API authentication control rail

The right-side rail uses restrained secondary buttons and reflows below the
information panels on narrow screens. It shows the registry scheduling policy,
drain progress and Worker API-token status. Activate scheduling uses the existing
Server diagnostics `canActivate` and blocking reasons; the Server validates again
at mutation time. Drain and Deactivate affect new assignments and retain existing
assignments and leases. Already-applied policies and absent/revoked API tokens
have explicit unavailable reasons. Missing readiness diagnostics block activation
without combining readiness with the independent drain or token controls.

All mutations use the existing administration session, CSRF request owner and
native confirmation pattern. API-token revocation denies Worker API calls and may
cause active leases to expire into recovery. It does not revoke credential delivery,
node login or provider credentials; there is no second revocation action in this
rail. React receives state and action callbacks, and owns no requests or timers.

While a request is pending all mutations are disabled. After rejection, conflict,
timeout or another uncertain response, periodic refresh can display current state
but cannot unlock the controls. Use **Refresh authoritative state**, inspect the
registry policy/token and current activation evidence, then deliberately confirm
any further action. No mutation is automatically replayed. Failed registry reads
retain the lock. Successful mutations refresh registry/diagnostics and the shared
Worker overview. Session teardown and obsolete read generations prevent late
responses from publishing data. Errors use bounded static guidance rather than
raw response bodies or credential material.

Deterministic control-owner tests cover each contract, confirmation cancellation,
blocked activation, already-applied policy, absent tokens, unauthorized state,
pending duplicate submission, stale activation rejection, lost-response refresh,
failed refresh and late navigation/logout responses. Rendering tests cover state,
unavailable reasons and accessible description associations. These checks do not
replace a deployed browser review of layout, focus and the native confirmation.

## Parent integration review and evaluation

The integrated route retains the existing navigation shell while the React view
owns its friendly-name heading and secondary Worker ID. The generic shell title
and selected-ID text, plus the generic registration guidance, are hidden only on
the PoC. Query parameters such as
`?step=preparation` cannot expose the ordinary node administration panel alongside
the PoC. The top summary labels the heartbeat lifecycle separately from execution
prerequisites: a `running` lifecycle does not by itself mean a task is executing.
Reported update/restart failures, capability regression and incompatible
configuration receive red textual badges; transitional lifecycle states are amber,
and missing observations remain gray. No admission policy is derived from colors.

Final review found two material contract limits: Issue titles are unavailable in
execution references, and stage history/completed/upcoming evidence is absent.
The view therefore displays the linked Issue number with an explicit missing-title
message and emphasizes only the reported current stage. Validation can repeat
after rebase or recovery, so a stage name cannot safely imply prior stages completed
or a fixed remaining sequence. These are documented limitations against the visual
reference, not evidence of a complete history/progress API. Adding such evidence
would require separately scoped contract work. Worker capacity, readiness,
provisioning actions and token effects match the existing contracts; administration
still validates on the Server and uncertain responses require explicit refresh.

The optional browser harness renders the delivered shell and production bundle
against the shared deterministic fixtures, without a live provider or credentials:

```sh
npm run build --prefix src/CodexServer/worker-poc
# Make an independently installed Playwright available via NODE_PATH and install
# its Chromium browser/dependencies in the review environment.
node tests/dashboard/worker-poc-browser-review.cjs /tmp/codex-worker-poc-review
```

It checks desktop (1280px) and narrow (375px) layout, the administration rail,
direct navigation/reload, canonical Issue links, focus/disclosure retention,
long names, stale/unavailable observations, logout and exit to the ordinary route.
Representative fixture screenshots are retained below. These contain fixture data
only; API authorization/deep-route packaging is covered by the .NET tests, while
deployed HTTPS/VPN session behavior remains a separate integration campaign.

- [Desktop Worker detail](images/worker-detail-poc/worker-detail-1280.png)
- [Narrow Worker detail](images/worker-detail-poc/worker-detail-375.png)

Local integration validation passed the 124-test dashboard suite, production
frontend bundling, and `dotnet test CodexWorker.sln -m:1` (1,513 Worker/Server
tests and 269 toolbox tests). The final shell-guidance adjustment passed the
28-test navigation/model subset and the browser harness; screenshots reflect that
adjustment. The default sandbox blocked npm network restore and MSBuild process
pipes; package restore, .NET validation and Chromium review succeeded with the
available validation escalation. No live provider or deployed HTTPS campaign was
run. These are local checks, not the Worker's separately configured authoritative
validation gate.

Embedding this island required two generated assembly resources, an MSBuild
lockfile restore/bundle step and a snapshot/callback bridge into the existing
request/session owner. The production bundle is approximately 237 KB of JavaScript
and 3.5 KB of CSS before transfer compression. The installed Server has no Node or
CDN dependency, but **every Server build now needs Node/npm and the locked packages**,
even when operators use only the ordinary dashboard. Offline builders must provide
the npm cache as well as NuGet packages. Publish includes the assets through the
assembly resources rather than a second deployment artifact.

Maintenance costs include React/esbuild dependency and lockfile updates, the
adapted Untitled UI Badge source/license, scoped CSS, and tests for the JavaScript
snapshot boundary alongside existing dashboard tests. This is a small Untitled UI
evaluation: it does not establish the cost of its full component system, Tailwind,
paid components or a dashboard migration. React preserves focused elements and
disclosures without bespoke DOM restoration in this view; the shared request owner
avoids duplicate authentication, streams and scheduling state. The callback bridge
and separately maintained presentation model are additional seams to keep aligned
with Server contracts.

Continue evaluating this one route if the name/status/current-work hierarchy and
React's update behavior justify those build and maintenance costs. First resolve
the title/stage evidence requirements as explicit product decisions, and measure
operator usability and packaged deployment behavior. This review does not select
React or Untitled UI as the dashboard architecture or authorize other migrations.
