# Isolated Worker detail React proof of concept

Open `/workers/{workerId}/poc` directly (using the same registered ID as
`/workers/{workerId}`). Reload restores the existing administration cookie and
CSRF session. The existing Worker detail and every other route retain their
ordinary dashboard assets. The PoC is read-only and links back to the existing
Worker preparation and administration page; no scheduling or readiness rules
are introduced.

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
diagnostics, shared snapshot delivery, logout clearing and ordinary-page exit. Focused model and React rendering tests use JSON-shaped Server fixtures to
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
registered available typed actions and pending/latest command status. Actions are
informational text; mutations stay in existing administration. Diagnostic codes
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
