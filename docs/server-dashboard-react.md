# Production dashboard foundation (temporary migration entry)

`src/CodexServer/worker-poc` is the single production frontend package. Its
historical directory name remains to preserve tooling and source provenance;
`package.json` now names it `codex-server-dashboard`. The approved Worker PoC,
official public Untitled UI source, tokens and MIT license are retained. There is
no second application package or copied component library. The legacy entry and
Vite SPA share the shell, components, Worker presentation and presentation helpers.

Open `/dashboard-preview` or `/dashboard-preview/workers/{id}`. The preview has a
TypeScript application/router boundary, app error boundary, typed session/read
hooks and page/feature/shared layout. Dark mode is the default; the theme control
persists only a light/dark preference. Worker detail reuses the fixture-tested PoC
presentation through a typed boundary. Its control rail is omitted: administration
uses the current dashboard. Home now renders the operational overview described below.
Projects and Issue administration now use shared React flows; see the
[project and Issue parity inventory](server-projects-react.md). Executions now use
shared React list, detail and recovery controls; see the
[execution and recovery parity inventory](server-executions-react.md). Remaining lists and
Settings administration explicitly link to their current implementation
with the selected resource and query intact.
These views do not offer simulated observations or unfinished actions. Legacy `/home`,
resource routes and `/workers/{id}/poc` remain usable; default-route cutover and PoC
removal belong to 24.12.9.

## Route and migration inventory

React Router owns preview history routes. The Server has the same explicit GET
shell allowlist, prefixed with `/dashboard-preview`. `/dashboard-preview` normalizes
to preview `/home`, preserving its query. No catch-all Server fallback exists.
Unknown sections, nested paths, API/protocol/health paths and unsupported methods
cannot receive the shell. The canonical map remains
[Server dashboard navigation](server-dashboard-navigation.md).

| Canonical route | Query context | Read APIs / later migration scope |
| --- | --- | --- |
| `/home` | none | Workers, projects, latest bounded executions, nodes, Server status; authoritative setup projections |
| `/projects`, `/projects/{id}` | `issue`, `issueState`, `label`, `issues`, preparation context | Migrated in preview: projects, GitHub access/issues/relationships; verified revision-fenced create/edit/lifecycle/delete, Issue preview/editing/eligibility/enqueue. [Parity inventory](server-projects-react.md) |
| `/workers`, `/workers/{id}` | `step`, `project` | Worker registry/diagnostics, nodes/commands, projects and latest 50 executions; enrollment, preparation, activation/drain/disable, API-token and delivery revocation |
| `/executions`, `/executions/{id}` | `project`, `state`, `issue`, `offset` | Execution list/detail; queued cancellation and evidence-based uncertain integration reconciliation |
| `/settings`, `/settings/{credentialId}` | `node` | Credential metadata, nodes, Server GitHub connection, command history; credential create/replace/assign/revoke and typed provisioning |

`src/app` owns mounting, routes and error handling; `src/pages` owns section pages;
`src/features/workers` owns Worker presentation; `src/features/projects` owns
project configuration and GitHub Issue administration; `src/features/executions`
owns execution browsing and evidence-based recovery; `src/shared`
owns the shell, theme and typed API/session hooks. `src/untitled` remains upstream
source. The retained PoC `detail.jsx` and `model.js` are reused presentation modules;
further migration should type them alongside their feature work, rather than copy
them. The compatibility entry `shell.jsx` re-exports the shared TypeScript shell.

The preview and retained PoC share React-owned administration sessions, validated
TanStack Query reads, a single Worker SSE stream/fallback refresh owner and Zustand
theme preferences. See the shared infrastructure contract below. Read-only screens
present observations; administration actions remain available through explicit links
to the current dashboard. No backend or standalone Worker API changes are included.

## Development and validation

Build machines need .NET 10, Node >=22.12 and npm with the locked public packages.

```sh
npm ci --prefix src/CodexServer/worker-poc --no-audit --no-fund
npm run check --prefix src/CodexServer/worker-poc
npm run dev --prefix src/CodexServer/worker-poc
```

The development URL is `/dashboard-preview/`. Vite supports direct history routes.
For authenticated development, use a trusted local HTTPS reverse proxy whose
external origin exactly matches Server `AdministrationOrigin`. Route preview
paths, `/src`, `/@vite`, `/@id`, `/node_modules` and Vite WebSocket traffic to the
loopback Vite process, and `/api` to the Server. Keep the proxy upstream Host
consistent with the configured administration authority. Use disposable local
registry data and existing management-network access. An ordinary direct HTTP
Vite port or a different-origin API URL cannot establish the secure Server session.
Do not disable cookie/origin checks, add CORS bypasses or persist management tokens.

Alternatively set `DASHBOARD_API_ORIGIN` to the Server upstream for Vite's `/api`
proxy, still behind that same trusted HTTPS external origin. The proxy does not
rewrite Host/Origin and does not disable TLS verification. Production asset base
is `/dashboard-assets/preview/`; development base is `/dashboard-preview/`.

Normal Server Debug/Release builds run typecheck, lint and production compilation.
`npm run check` also performs those checks independently; deterministic legacy
regressions remain `node --test --test-isolation=none tests/dashboard/*.test.cjs`.
Optional Playwright review uses the production graph and Server-shaped fixtures:

```sh
NODE_PATH=/path/to/playwright/node_modules node tests/dashboard/dashboard-preview-browser-review.cjs
```

## Embedded output and artifacts

`build.mjs` retains the PoC esbuild/PostCSS entry and invokes Vite in a separate
process. Tailwind's input cache must not be shared between these two compilations.
Vite empties its output directory each build, emits hashed assets and a Vite
manifest; the embedding manifest additionally records SHA-256 for every local
asset. Empty/missing JS/CSS fails the build. MSBuild embeds the shell, manifest
and local asset graph from configuration-specific intermediate output; regeneration
on every build handles deleted inputs and stale output without timestamp inference.
Frontend source and node_modules never become publish content. Source licenses
are retained and included in production JavaScript.

The Server verifies manifest paths, resource existence, hashes and shell references
on startup, then maps only manifest-listed asset URLs. There is no filesystem or
HTML fallback for unknown assets. Publish must build: `--no-build` is rejected
because it cannot establish that the assembly contains current frontend inputs.
Both release entry points already perform normal `dotnet publish` and therefore
use this same pipeline, without a separate copy stage or runtime Node process.

```sh
dotnet publish src/CodexServer/CodexServer.csproj -c Release -r linux-x64 --self-contained true -o /tmp/dashboard-publish
(cd src/CodexServer/worker-poc && node build.mjs /tmp/dashboard-reference)
node tests/dashboard/worker-poc-artifact-review.cjs /tmp/dashboard-publish /tmp/dashboard-reference
bash tests/release-tests.sh
```

The artifact check accepts a clean publish directory or extracted Linux Server
archive, compares every served byte with fresh output and runs the installed
Server with Node/npm absent from PATH. It covers both entries, legacy isolation,
unknown routes/assets, unsupported methods and protected API authorization.
These local checks do not replace Worker-configured authoritative validation or
deployed HTTPS session/real-node acceptance.

## Local review evidence

Local typecheck/lint/production builds passed, as did the 126 deterministic Node
regressions, nine focused Server route/session tests and Debug compilation with
zero warnings/errors. A self-contained Release publish and actual disposable
Linux archives passed the expanded artifact check, with Node/npm absent from the
installed Server PATH. The stubbed release suite covered both packaging entry
points; no GitHub release was published. `--no-build` publish was rejected as
intended.

Chromium reviewed the final production bundle at 1280px and 375px. The fixture
harness passed direct resource/query context, reload/Back, SPA navigation retaining
its session owner, dark/light persistence, failed/deleted reads and logout during
a coordinated in-flight read, with no resource mutations or horizontal overflow.
Desktop/mobile screenshots were inspected locally. Initial package/socket/browser
restrictions were resolved with permitted tooling access and browser libraries
and fonts extracted under `/tmp`; no machine packages were installed. Live HTTPS,
provider actions and real nodes were not exercised. Worker-configured validation
remains a separate authoritative gate.

## Shared React API, session, live updates and preferences (24.12.2)

Both `/dashboard-preview` and `/workers/{id}/poc` now use the same React-owned
infrastructure. The PoC response contains only the React mount and local assets;
it loads no legacy scripts, hidden DOM targets or `window.codexWorkerPoc` bridge.
Its approved presentation and public Untitled UI source are reused. The PoC
preserves its scheduling/API-token control rail through React Aria managed
confirmation dialogs. Projects in preview now support explicit administration;
other preview sections
retain their current-dashboard links. Both entries show bounded node
command evidence. The current-dashboard link retains full administration. Ordinary
dashboard routes retain their existing implementation until their feature migration.

`shared/api/client.ts` is the only HTTP transport: native fetch, same-origin
cookies, no-store, local API paths, bounded request timeout, composed cancellation,
in-memory CSRF for writes, fixed safe errors and session origin/Host diagnostics.
Response validators consume unknown JSON and check the existing narrow Server
projections. New endpoints must supply their own validators; do not use unchecked
casts in screen components or introduce another client.

`DashboardRuntime` owns each root/tab's session generation, private QueryClient,
expiration timer, cancellation, mutation fences and live owner. `SessionProvider`
restores the cookie once, subscribes React to that owner and handles pagehide and
persisted pageshow. Replacement/logout/rejected authorization/expiry cancels
requests and stream readers, clears private query and mutation caches, and unmounts
private forms/drafts. Login input is reset before submission; no authentication
state, CSRF, token or Server observation enters persistent storage. A lost logout
response retains only transient CSRF for explicit retry. No login request retries.

Use `useApiRead(path, validator)` for queries. Generation/path query keys isolate
sessions and deduplicate concurrent observers; unused reads consume Query's abort
signal. Retries, focus/reconnect refetch and per-query polling are explicitly off.
One sequential five-second owner refetches active observers, without cancelling or
duplicating existing reads. Workers use one cancellable fetch SSE reader, with a
1 MiB event bound, three-second reconnect, fixed unavailable/processing diagnostics,
and runtime validation. New streams wait for the cancelled reader to release.
While connected, SSE replaces Worker data and cancels older Worker GETs; fallback
refresh covers disconnected streams. No component may open its own stream/timer.

Use `useApiMutation` or its shared `runtime.mutate` owner for feature mutations.
It never retries a write and
requires a fresh uncached authoritative check before submission; the Server remains
the final authority for authorization, revision, lease, readiness and local consent.
Callers use `runtime.read` for those checks, not Query cache or refetch status.
A submitted write with a rejected/lost/malformed response locks its resource. Cache
updates, SSE, polling and navigation cannot release that fence. Only explicit
`reconcile` with successful fresh Server evidence releases it; callers must verify
that evidence establishes the operation outcome. Failed reconciliation preserves
the lock. No mutation occurs on render, rehydration, navigation or refresh.

`shared/preferences.ts` is the Zustand persist owner. Version 1 allowlists only
`theme`; writes and rehydration discard other fields. Missing, corrupt or disabled
storage falls back to dark and still permits changing this visit's theme. Resource
IDs, filters, pagination, preparation steps and project context remain in the URL.
Server snapshots, secrets, device codes, credentials, authorization, completion
flags and drafts must never be added to preferences.

Focused deterministic fixtures in `react-infrastructure.test.cjs` cover session
races, cache isolation, read cancellation/deduplication, authorization rejection,
stream release/replacement, poll suspension, response-loss locks, authoritative
checks, explicit reconciliation, safe diagnostics and preference reload/storage
failure. Browser review covers the built shared preview at desktop/mobile widths.
Live HTTPS/provider/node acceptance remains a deployment check, and Worker-run
validation remains the authoritative gate.

## Shared shell and composition (24.12.3)

Compose new screens in this same package. Import product patterns from `shared/`
and the original public components from `untitled/components/`; never copy screen
variants or load legacy dashboard CSS. `Application` owns responsive navigation,
selected links, skip navigation and login/session presentation. `PageHeading`
owns the h1, breadcrumb landmark and secondary resource ID; use `ResourceIdentity`
for name-first list identities. Route changes focus the heading, while query-only
changes and observation refreshes retain focus. Mobile navigation closes after a
navigation choice and uses React Aria focus restoration.

Use `TableCard`/`Table` for surfaces and accessible tables, `Input` with its label,
required/invalid state and hint for forms, `Notice` for live feedback, `ViewState`
for loading/empty/error surfaces, and `AdvancedDisclosure` for technical evidence.
Use `StatusBadge` with **text** and an explicit gray/success/warning/error tone
chosen by the feature's existing evidence mapping. Visual primitives do not
compute lifecycle, readiness, permissions or scheduling policy. The approved
Worker capability/command composition remains in the reused Worker presentation;
its detection and command evidence are not converted into new readiness rules.
Do not add speculative universal tables, forms or provisioning controllers.

Use `ConfirmationDialog` for action consequences, or `ActionDialog` with labeled
inputs for form submission. Both compose `FormDialog`, upstream Untitled UI
buttons/tokens and React Aria ModalOverlay/Modal/Dialog. React Aria owns focus
containment/restoration and dismissal; no native browser confirmation or unmanaged
dialog is used. Cancel is initially focused. Pending submissions disable fields and controls
and dismissal, and a synchronous guard prevents double acceptance. Native form
validation precedes submission. Supply action-specific consequences and a precise
action label; destructive actions use the upstream destructive button variant.
Mount dialogs only for the selected operation and key them by operation/resource
so transient drafts and failure state cannot leak into another operation.

The submit callback must use the shared mutation owner with fresh authoritative
checks. The dialog never retries or releases resource fences. Present uncertainty
and explicit reconciliation through the feature/data owner, even after a dialog
closes. A callback rejection disables further acceptance and shows a fixed safe
message; it must not expose response bodies or exception details. Existing Worker
controls continue to reconcile only on an explicit authoritative refresh.

Theme remains the version-1 Zustand preference owner, allowlisting only light/dark.
The tiny classic `public/assets/theme.js` boundary reads that same versioned theme
before CSS/visible paint in both production entries. Missing, corrupt, unsupported
or unavailable storage defaults to dark regardless of OS. Its version/allowlist
must remain synchronized with `shared/preferences.ts`; focused bootstrap fixtures
cover this contract. Vite injects the same-origin blocking script before styles,
and its bytes are included in the verified embedding asset manifest. No inline
script, external theme service or secret storage is introduced. Theme changes
update both upstream `dark-mode` tokens and native control color-scheme.

The browser fixture review covers dark/light shell, login and confirmation at
1280px, 375px and 640px (the CSS layout width of a 1280px viewport at 200% zoom),
focus containment/restoration, cancellation with no writes, pending dismissal and
exactly one acceptance write, fresh activation rejection and lost-response fences.
It uses Server-shaped fixtures; deployed HTTPS/provider/node checks remain separate.

Local review for 24.12.3 passed typecheck, lint, production compilation, 25 focused
Node regressions, and all three Chromium reviews (preview, Worker confirmation,
and shared form composition). Screenshots under `/tmp/shared-shell-review` were
inspected. The 640px layout checks cover zoom reflow; they do not claim an actual
browser zoom/OS assistive-technology campaign. Chromium initially required
permitted process access; locked npm restore required permitted network access.
The optional .NET test attempt with `--no-restore` could not run because this
checkout had no restored test assets (`NETSDK1004`). Worker-configured checks
remain the authoritative gate.


## Home operational overview and parity inventory (24.12.4)

`/dashboard-preview/home` renders Home in the shared React application. Canonical
`/home` retains the legacy entry until the planned 24.12.9 route cutover. Home uses
shared typed reads, the existing live/poll owner, upstream Untitled UI surfaces,
buttons and status badges. No backend or local standalone Worker change is included.

| Existing Home behavior | React parity / resource context |
| --- | --- |
| Current work and activity | Assigned/Running requests lead the page; latest activity uses a separate unfiltered latest-50 request snapshot, showing up to five latest requests. Execution links retain exact IDs; assigned Workers and central project Issue context are linked. |
| Pending, eligibility and recovery evidence | Needs attention shows reported pending/recovery/eligibility states, with retained reasons and summaries in native disclosures and links to the exact execution or project Issues. No new recovery action or retry is inferred. |
| Connectivity, execution prerequisites and scheduling | Worker availability, current/stale/unavailable node readiness and reported scheduling policy remain separate. Links resume the exact Worker's preparation step; Server health and GitHub observations remain independent. |
| Server GitHub setup | Home and the Settings connection summary consume `useServerReadiness` and the same `serverGitHubReadiness` projection. Current capability evidence and retained pending/running/failed authentication commands govern completion. Stale, disconnected and failed reads cannot complete it. Explicit links open existing Settings connection administration. |
| Central project setup | Persisted central definitions complete project creation independently of enablement, GitHub or Worker setup. Projects administration remains an explicit link. |
| Worker-first / project-first setup | Current connected authoritative ready Worker observations complete preparation independently of project creation. An existing Worker's exact preparation route is resumed; otherwise enrollment opens `/workers?prepare=1`. Preparation does not enable scheduling. |
| Configured systems | Completed setup actions and system diagnostics/capacity stay in collapsed native disclosures. Incomplete actions remain independently available. No browser completion flags exist. |
| Server status and capacity | Status/version, central definition and registered Worker counts remain available in diagnostics. Server-wide capacity is explicitly the aggregate of reported Worker slots, not a Server scheduling limit; per-Worker slots are separately linked. Missing values remain unavailable. |
| Refresh | Explicit refresh only refetches the six read contracts. React retains disclosure nodes and focused controls during observations; no setup action starts on render, navigation, reload or refresh. |

The existing APIs provide neither total execution history metrics nor a Server
scheduling limit. A latest-50 snapshot may omit older active execution details;
Home states this limit and links to the execution browser. No Issue title, progress
percentage or complete history is fabricated. Authentication does not prove
repository write permission, Worker execution preflight or scheduling authority.

Focused model/validation fixtures: `tests/dashboard/home-overview.test.cjs`.
Production-bundle desktop/mobile fixtures: `tests/dashboard/home-browser-review.cjs`,
covering empty, active/configured, stale/unavailable, independent setup and recovery,
shared Settings state, refresh focus/disclosures, exact links and absence of writes.
Run it with the same optional Playwright environment as the other browser reviews.
Worker-configured validation remains the authoritative gate.


Local Home validation passed typecheck, lint and production compilation, plus
42 focused Home/shared-infrastructure/legacy navigation regressions. Chromium
review passed at 1280px and 375px for the Home fixture campaign and the existing
preview navigation/session campaign; desktop/mobile screenshots were inspected.
Refresh was checked with a changed reported stage while retaining disclosure focus
and expansion. No resource mutations were submitted. Locked npm restoration needed
permitted network access; Chromium used existing temporary libraries and permitted
process access. The optional .NET Server test attempt with `--no-restore` could not
run because this checkout lacks restored test assets (`NETSDK1004`). No live
HTTPS/provider/node campaign or Worker-configured authoritative check is claimed.

## Settings, credentials and Server preparation (24.12.8)

The shared preview Settings routes now provide guided Server preparation, advanced
capabilities/history and credential administration. See the complete
[Settings parity inventory](server-settings-react.md) for route/action/API coverage,
transient input handling, authoritative recovery and deterministic review fixtures.
Canonical cutover remains separate.
