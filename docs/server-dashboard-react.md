> Current routing: all canonical Server dashboard screens use the shared React app.
> Preview URLs redirect to canonical routes; the separate PoC bundle and legacy
> Server frontend are removed. See [final cutover evidence](server-dashboard-cutover.md).
> Migration-stage descriptions and earlier validation below are historical evidence.

# Production React dashboard architecture and build

`src/CodexServer/worker-poc` is the single frontend package, named
`codex-server-dashboard`. The historical directory name preserves the accepted
Worker detail design and official public Untitled UI source/license provenance.
All canonical screens use one Vite graph, React root and BrowserRouter. There is
no legacy entry, PoC application, DOM bridge or dashboard JavaScript request owner.
The standalone Worker dashboard is unchanged.

See [navigation](server-dashboard-navigation.md) for canonical routes and query
context, and [final integration acceptance](server-dashboard-final-review.md) for
the route/action/API checklist and current evidence. Preview and PoC bookmarks
redirect to canonical routes; the Server rejects unknown routes and assets rather
than serving a catch-all shell.

`src/app` owns mounting, routing and errors; cohesive modules under `src/features`
own Home, Projects/Issues, Workers, Executions and Settings composition. Shared
shell, dialogs, status, forms and API/session hooks live under `src/shared`.
`src/untitled` retains public upstream navigation, inputs, buttons, badges, tables,
checkboxes and tokens. The accepted `detail.jsx` and `model.js` remain reused
presentation modules, not a separate application. Custom CSS handles composition.

`SessionProvider` creates one `DashboardRuntime` and TanStack Query cache per tab.
The runtime owns CSRF, generation fencing, query cancellation/invalidation, expiry,
one bounded Worker SSE reader and sequential fallback polling. Zustand persists
only a versioned light/dark preference. Project/credential workspaces retain
transient drafts and uncertain attempts, not separate registries. Shared node
components and Server readiness projections serve multiple screens. Diagnostics
validation, including positive safe-integer reported revisions, has one shared
boundary. Secret inputs and device challenges never enter persistent preferences
or the private query cache. Server authorization/readiness and node-local consent
remain authoritative; no render, route, refresh or hydration submits a mutation.

## Development and validation

Build machines require .NET 10, Node >=22.12 and npm with locked public packages.
Installed nodes require no Node/npm, CDN or frontend process.

```sh
npm ci --prefix src/CodexServer/worker-poc --no-audit --no-fund
npm run check --prefix src/CodexServer/worker-poc
npm run dev --prefix src/CodexServer/worker-poc
node --test --test-isolation=none tests/dashboard/*.test.cjs
```

Vite serves canonical paths such as `/home` and `/settings` at its root. For
cookie-authenticated development, use a trusted disposable HTTPS reverse proxy
whose external origin exactly matches Server `AdministrationOrigin`. Route UI,
Vite modules and WebSocket traffic to loopback Vite, and `/api` to the Server.
Keep upstream Host consistent with the configured authority and management-network
policy. `DASHBOARD_API_ORIGIN` optionally configures Vite's API upstream; it does
not rewrite Host/Origin, bypass TLS, enable CORS or persist tokens. Production
assets use the fixed same-origin `/dashboard-assets/preview/` prefix.

## Embedded assets and publish

Normal Debug/Release builds typecheck, lint and regenerate the Vite graph.
`build.mjs` invokes only Vite, empties its output and records every local asset's
SHA-256. MSBuild embeds shell/manifest/assets from configuration-specific output;
always rebuilding handles deleted sources and stale bundles. Startup validates
manifest paths, hashes and shell references; only listed assets receive endpoints.
Source, node_modules and private dependencies do not become deployment content.
Upstream MIT source licenses remain in source and production JavaScript banners.

Publish rejects `--no-build`. Both packaging entry points use normal publish and
therefore the same current embedded graph. No separate copy or runtime install is
required. Validation may use a disposable publish directory or extracted archive:

```sh
dotnet publish src/CodexServer/CodexServer.csproj -c Release -r linux-x64 --self-contained true -o /tmp/dashboard-publish
(cd src/CodexServer/worker-poc && node build.mjs /tmp/dashboard-reference)
node tests/dashboard/worker-poc-artifact-review.cjs /tmp/dashboard-publish /tmp/dashboard-reference
bash tests/release-tests.sh
NODE_PATH=/path/to/playwright/node_modules node tests/dashboard/dashboard-preview-browser-review.cjs
```

Artifact checks compare every served byte with fresh reference output, exercise
canonical deep links/reload and redirect targets, reject unknown paths/assets and
check protected API authorization with Node/npm absent from the Server PATH.
Playwright/Chromium and OS libraries are separate review tools, never product
dependencies. Feature browser harnesses use Server-shaped deterministic fixtures;
see final acceptance for commands, evidence and remaining live-deployment limits.

The following feature-stage descriptions record the migration design and earlier
local checks; final integration evidence supersedes their route/build claims.

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

## Worker parity inventory (24.12.6)

Canonical `/workers` and `/workers/{id}` promote the accepted detail hierarchy and
model without a second package, copied detail application or legacy DOM bridge.
The historical asset entry imports the same application; the historical PoC URL
redirects. Other canonical sections retain their current implementation until their
cutover. Navigation within Workers retains the React session/query owner; leaving
Workers opens the existing resource screen. Unknown paths/methods remain rejected.

| Workflow / evidence | React surface and existing contract |
| --- | --- |
| Inventory and detail | Friendly name then secondary ID; independent connection, working state, prerequisites, freshness, this Worker's active/max slots and available capacity. Registry, nodes, diagnostics reads. |
| Concurrent and recent work | All matching assigned/running requests from latest 50, reported current stage, linked project/Issue/execution; up to 10 actual terminal outcomes. Missing details, timing, titles, stage history and totals are never invented. |
| New enrollment / safe association | Add Worker shared dialog, pinned public version GET, exact public pairing request and protected authorize POST. Existing-machine instructions retain drain/reconciliation, stopped service, identity/history retention and explicit node consent. |
| Enrollment resume / acknowledgement | URL `enroll=1`; only a validated public request survives tab reload. Authorization is volatile, hidden by default and cleared at expiry/close. Resume/check progress observes registration, active API authentication and heartbeat before preparation; it never resubmits. |
| Preparation | URL-addressable registration / preparation / project / activation, preserving `project`; reported version, config sync, service-account Codex/GitHub authentication and preflight. Scoped capabilities, project revision/freshness/requirements and materialization are separate. Missing checkouts stay lazy. |
| Capability / provisioning | Shared `features/nodes` composition reuses accepted capability cards and typed node history; only advertised allowlisted actions, explicit elevation and repository input, node-local policy, public SSH identities and deadline-bounded device instructions. Same views can be reused in Settings. |
| Pending / uncertain commands | Fresh uncached node/command checks before writes; response loss fences actions until explicit authoritative refresh. Retained pending/running commands survive navigation/reload. Queued cancellation and expired-running reconciliation use shared dialogs; reconciliation requires explicit verified quiescence. No automatic retry. |
| Scheduling | Activate / Drain / Deactivate share confirmation dialogs and fresh registry/activation checks. Server validates current ownership/readiness. Existing assignments keep leases; preparation never schedules work. |
| Revocation | Separate API-token and credential-delivery metadata, endpoints and confirmations. API revocation can expire leases into recovery; delivery revocation stops future delivery without removing delivered credentials or revoking provider-side authorization. |
| Advanced evidence | Scoped Worker capabilities, platform/registration/active-project observations, diagnostic reasons and operational errors, freshness/version/config, typed command IDs/status/diagnostics and existing legacy plan history remain accessible in disclosures, with an execution recovery link. Process output and private authentication material are excluded. |
| Session, presentation | Shared CSRF/session cancellation, query/SSE ownership, Untitled UI components/license/tokens, responsive shell, dark default and persistent light/dark preference. No native dialogs or mutations on render/navigation/refresh. |

Focused fixture coverage is in `workers-migration.test.cjs`, existing Worker model /
render / shared infrastructure tests, and `workers-browser-review.cjs`. The browser
harness uses production assets and deterministic Server responses at 1280/375px,
including concurrent slots, empty/history/stale/failed/unavailable states, keyboard
confirmations, fresh activation rejection, separate delivery revocation, lost
provisioning responses, reload, queued cancellation, device expiry, quiescence,
public enrollment resume, theme and sign out. Run after the production build:

```sh
NODE_PATH=/path/to/playwright/node_modules node tests/dashboard/workers-browser-review.cjs
```

No backend contract expansion, live provider/node campaign or standalone Worker
dashboard migration is included. Worker-configured checks remain authoritative.

Local migration validation: the dashboard suite passed (146 tests; obsolete
legacy PoC controls tests were superseded by production browser/runtime coverage),
and the final focused contract/render/runtime checks passed (32 tests), the solution built with zero warnings/errors, and 40 focused .NET
route/enrollment/preparation/diagnostics/scheduling/revocation tests passed.
Chromium fixture review passed at desktop/mobile widths; screenshots were inspected
from `/tmp/workers-migration-review`. Initial sandbox npm/NuGet access and browser /
VSTest process restrictions required approved dependency restore and local process
access. The resumed review also verified the existing Home `prepare=1` enrollment
entry and rejected unknown availability or invalid heartbeat as pairing
acknowledgement. Chromium required the existing temporary font configuration;
no node or provider was modified. These are local self-checks, not the
Worker's authoritative configured validation.

## Dashboard languages

The shared shell and form dialogs offer English and Spanish without changing the
route, session owner or transient drafts. English is the initial default. The
existing version-1 `codex-dashboard-preferences` store allowlists only theme and
language (`en` / `es`); older theme-only preferences remain valid. Corrupt or
unavailable storage keeps a usable visit preference. No credentials, API data or
form contents enter this store.

`shared/i18n.ts` uses the existing Zustand subscription rather than adding a
localization dependency. Feature/shared keys live in `shared/locales/en.ts` and
`es.ts`; TypeScript requires matching Spanish keys. Use `t` for dashboard copy,
parameterized resources for messages with values, and `statusLabel` for API values.
`localizeText` re-presents retained dashboard messages and static action labels in
the current language, preserving unknown external text. Known status labels never
change filter values, mutation payloads or readiness rules. Unknown future values
remain readable and React-escaped. Dates keep the existing local-browser timezone
and `dd/MM/yyyy, HH:mm:ss` formatter used by the Server sidebar in both languages.
Shared inputs supply localized validation and password-control labels; navigation
labels are passed to the retained Untitled UI components.

Validation includes catalog key/parameter parity, preferences, rendered shell and
Worker evidence, retained messages, unknown statuses and timestamp stability in
`tests/dashboard/localization.test.cjs`. After building, the optional
`tests/dashboard/localization-browser-review.cjs` Playwright fixture exercises real
routes, project/credential drafts and dialogs, reload, status filter API values,
error/empty states and mobile navigation. It submits no resource writes.

## VM1 local development deployment

`cw dashboard deploy` (exact alias `cw dd`) checks and builds only the React
frontend from `CW_REPO_DIR` (default `~/projects/codex-worker`), including uncommitted
edits. It works through the `cw` symlink on PATH from any working directory.
It uses `npm ci` and `npm run check`, never a product build, release, or Worker update.
Node matching the dashboard package's engines, npm, Python 3, systemctl and
non-interactive sudo permission to restart **codex-server only** are required.

This is for the VM1 development installation only. The installed Server must
already contain development override support (install that version once).
Deployment probes the installed binary with `--dashboard-override-capability`;
only the exact supported capability response is accepted. An old binary, probe
failure or timeout stops deployment before building, changing assets or restarting.
Upgrade the installed Server once to a build containing this capability and the
override, then retry `cw dd`; setting the environment alone cannot add support.
Create a dedicated directory outside the checkout and `/opt`, for example
`/var/lib/codex-server/development-dashboard`, writable by the developer and
readable by the Server account. Do not use symbolic links in this directory or
its ancestors. Configure an explicit local systemd drop-in for `codex-server`:

```ini
[Service]
Environment=DOTNET_ENVIRONMENT=Development
Environment=CODEX_SERVER_DEVELOPMENT_DASHBOARD_DIR=/var/lib/codex-server/development-dashboard
```

Run `systemctl daemon-reload`, then `cw dd`. The command reads these explicit
service properties; values supplied only by an EnvironmentFile are not accepted.
It validates the installed `/opt/codex-server/current/CodexServer` service target,
stages and verifies the full manifest, hashes and shell references, and swaps
the assets before restarting the Server. It then compares served HTML and every
manifest asset byte for byte against the candidate through `http://127.0.0.1:5090`.
For a different local listener, add an explicit systemd Environment entry
`CODEX_SERVER_DEVELOPMENT_DASHBOARD_VERIFY_URL=https://localhost:PORT` (or a
loopback HTTP origin). Verification accepts only loopback origins, uses no proxy,
credentials or redirects, and retains normal certificate verification. Use the
actual configured listener and trusted certificate; do not relax Server security
settings. An unreachable/rejected endpoint is reported separately from a served
generation mismatch; both fail deployment and restore the previous generation.
The running Server retains its old
assets in memory until restart; no partially staged build is served. The prior
asset set is retained as `previous`; activation failure restores it and restarts
the Server, reporting any rollback failure. An empty development directory uses embedded assets until the first deployment;
first-deployment rollback returns to those embedded assets.
Server configuration, database, sessions and Worker services are not modified.
Browser refresh uses the canonical routes and asset prefix. Normal installations
continue using embedded assets. Invalid enabled overrides fail startup explicitly.
To return to embedded assets, remove both drop-in entries, daemon-reload and restart
only `codex-server`. No remote upload endpoint or filesystem static hosting is added.
