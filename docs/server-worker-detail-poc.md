# Accepted Worker detail design and canonical promotion

The accepted design is now canonical at `/workers` and `/workers/{id}`, including
enrollment, preparation, provisioning and administration. `/workers/{id}/poc`
redirects with its query context intact. See the complete
[Worker parity inventory](server-dashboard-react.md#worker-parity-inventory-24126).
The following PoC description and review evidence document the original accepted
visual starting point; its isolation and administration links describe that earlier
stage, not the current routes.

The package has been promoted into the [production dashboard foundation](server-dashboard-react.md).
The historical PoC entry remains during migration; Vite/TypeScript/Router now
also serve the explicit `/dashboard-preview` entry from this same package.

Open `/workers/{workerId}/poc` with a registered Worker ID. Only this route uses
this React application; ordinary dashboard routes retain their existing assets
and implementation. React owns the entire visible screen: product sidebar,
responsive navigation, session presentation, Worker heading, summary, current
execution, recent execution table/list, capabilities and secondary control rail.
The legacy dashboard theme is removed from the PoC response. The route now uses the shared React session, query and live-update infrastructure
without a hidden legacy DOM/request bridge. See
[shared React infrastructure](server-dashboard-react.md#shared-react-api-session-live-updates-and-preferences-24122).

## Components and styling

This uses the official [manual installation](https://www.untitledui.com/react/docs/installation)
and [source-component model](https://www.untitledui.com/react/docs/introduction),
with public MIT source from
[revision 4702dc0](https://github.com/untitleduico/react/tree/4702dc0ea8d140c3491a85670c7b4fab47b722da).
The source inventory and adaptations are documented in
`src/CodexServer/worker-poc/src/untitled/README.md`; the license is retained in
`worker-poc/UNTITLED-UI-LICENSE` and embedded in the production JavaScript.
No paid component, private registry, CLI account or hosted runtime is needed.

The shell uses `SidebarNavigationSimple`, `MobileNavigationHeader`, `NavList` and
`NavItemBase`. Mobile navigation uses React Aria's modal dialog, focus containment,
Escape dismissal and focus restoration. `Button`, `Input`, `Badge`, `FeaturedIcon`,
`TableCard` and `Table` provide the main visible patterns, including session UI.
The demo identity is replaced by Codex Server, and demo search/account content is
omitted. Workers is selected; Home, Projects, Executions and Settings are ordinary
links that leave the isolated application by full navigation.

Pinned dependencies include React/React DOM 19.3.0, React Aria Components 1.21.1,
Untitled UI icons 0.0.23, Tailwind 4.3.3, its PostCSS plugin, PostCSS 8.5.29,
Tailwind Merge 3.7.0, React Aria Tailwind variants 2.2.0 and animation utilities
1.0.7. esbuild 0.25.12 bundles production JavaScript. The upstream theme provides
spacing, type, colors, surfaces, focus and utility tokens; custom CSS is limited
to composition. The font stack uses local system fallbacks, with no font download.
The unused upstream prose typography plugin is omitted.

## Authoritative data, session and actions

React owns session restoration, CSRF, cancellable validated queries and the single
Worker SSE/fallback refresh owner. Sign-in clears the form before submitting the
transient token. Logout clears observations and private caches; obsolete session
generations cannot publish late private data. Theme persistence allowlists only
light/dark. React escapes resource text; Issue links remain canonical HTTPS URLs.

Connection, lifecycle, execution prerequisites, scheduling policy and observation
freshness remain distinct textual observations. Colors indicate their reported
states and do not authorize work. Slots use this Worker's active executions and
maximum capacity (registered capacity is the fallback), with available capacity
and active Server assignments reported separately.

Current execution has stronger emphasis than history, showing project, linked
Issue identity, elapsed time and the reported current stage. Recent history shows
project/Issue links, terminal outcome and available duration/time. Capabilities
show detected installation/version, health, authentication, configuration, update,
detection time, operation and pending/latest typed-command evidence. Available
typed actions are informational; provisioning mutations remain in administration.
Raw failure details and process output are not displayed.

The control rail preserves activation, drain, deactivation and API-token revocation.
React Aria confirmation dialogs provide focus containment, Escape dismissal and
focus restoration, using the shared theme and Untitled UI buttons. A fresh uncached
registry/readiness check precedes every submission; Server validation still governs
all effects. Pending writes resist double submission. Rejected, lost or malformed
responses retain the resource lock across polling and navigation until explicit
**Refresh authoritative state** succeeds. No write is automatically retried.
Scheduling and authentication effects remain governed by the existing Server
contracts; full administration remains linked from the retained route.

## Honest API limits

No backend contract is expanded. `WorkReference` lacks Issue titles, and the view
says so. It also lacks stage history or completed/upcoming evidence; only the
reported current stage is shown, with an explicit limitation. No step completion,
remaining sequence or percentage is inferred from a stage name.

Executions are filtered by assigned Worker from the shared latest 50 Server
requests, with up to 10 terminal outcomes. Older history or even active details
can fall outside that bounded view. Reported active work with absent execution
detail is called out. Missing/invalid timestamps do not produce invented durations.
Failed, stale, unavailable and deleted observations have explicit text states.

## Build, publish and packaging

Build/release machines require .NET 10, Node.js 22+ and npm. npm must reach the
public registry or have the complete locked package cache; NuGet has its usual
restore prerequisites. No tooling or network install is needed on installed nodes.

```sh
npm ci --prefix src/CodexServer/worker-poc --no-audit --no-fund
npm run build --prefix src/CodexServer/worker-poc
dotnet build src/CodexServer/CodexServer.csproj -m:1
dotnet publish src/CodexServer/CodexServer.csproj -c Release -r linux-x64 --self-contained true -o /tmp/poc-publish
node --test --test-isolation=none tests/dashboard/*.test.cjs
```

MSBuild restores the lockfile when its inputs change and regenerates JavaScript
and compiled Tailwind CSS on every normal Debug/Release build and publish. Always
regenerating avoids timestamp-only stale-asset detection, including deleted source
files. Outputs use configuration-specific intermediate directories and the same
two embedded assembly resources and asset URLs as before. Frontend source and
node_modules are excluded from publish. Installed artifacts serve local assets;
there is no CDN, separate frontend process or runtime Node/npm dependency.

Both `packaging/release-linux-x64.sh` and the `packaging/release.sh` orchestrator
reuse `dotnet publish`; embedding needs no separate copy stage. Packaging/version
policy is unchanged. Local disposable archives used for validation are not a
published release and do not change the source version.

The real artifact regression compares **every served asset byte** with a fresh
reference build, so missing or stale embedded output fails. It also checks the
route, legacy-theme isolation, ordinary-route isolation, unknown assets and
protected API authorization. It starts the published Server with Node/npm absent
from PATH. It accepts a publish directory or an extracted Linux Server archive:

```sh
(cd src/CodexServer/worker-poc && node build.mjs /tmp/poc-fresh-assets)
node tests/dashboard/worker-poc-artifact-review.cjs /tmp/poc-publish /tmp/poc-fresh-assets
# For local packaging validation, extract the Server tarball into /tmp/poc-extracted,
# then pass /tmp/poc-extracted instead of /tmp/poc-publish above.
bash tests/release-tests.sh
```

The existing stubbed release suite covers orchestration without external services;
the artifact regression covers actual published resources. Normal .NET endpoint
coverage checks asset content types, route isolation and method/path rejection.
Deployments retain the existing management-network/VPN policy and API authorization.
No proxy allowlist or CSP is relaxed. Assets require only same-origin script/style
sources, plus whatever the existing inline request owner requires.

## Deterministic browser acceptance

The browser harness uses the production bundle, delivered HTML and Server-shaped
fixtures, without live providers or credentials. Install Playwright/Chromium and
its OS libraries separately in the review environment; these are not build or
runtime dependencies of the Server.

```sh
npm run build --prefix src/CodexServer/worker-poc
NODE_PATH=/path/to/review/node_modules node tests/dashboard/worker-poc-browser-review.cjs /tmp/codex-worker-poc-review
```

It exercises desktop (1280px) and mobile (375px) screen composition, overflow,
selected navigation, responsive control rail, modal keyboard dismissal/focus,
canonical Issue links, focus/disclosure retention on snapshots, direct reload,
long-name/stale data, failed reads, empty/deleted observations, logout and exit to
the ordinary dashboard. Fixtures and rendering/model tests preserve independent
status/capacity evidence, safe URLs, unavailable timing and mutation descriptions.
These checks do not replace deployed HTTPS/VPN session or real-node acceptance.
No production publication or operator-machine change is part of this issue.

## Local review evidence for this change

The production bundle was reviewed in Chromium at 1280px and 375px using the
Server-shaped fixtures. The full sidebar/name/status/current-work hierarchy,
responsive recent history, capability surfaces and control rail were inspected.
The harness passed overflow, current navigation, keyboard drawer focus restoration,
snapshot focus/disclosure retention, canonical current/recent Issue links,
pending/failed capability operations, stale/long-name observations, failed reads,
empty/deleted state, logout, React sign-in and ordinary-route exit. Screenshots:

- [Desktop screen](images/worker-detail-poc/worker-detail-1280.png)
- [Mobile screen](images/worker-detail-poc/worker-detail-375.png)

Local checks passed the 125-test dashboard suite, normal Server build with zero
warnings/errors, all 1,513 Worker/Server and 269 toolbox .NET tests, the focused
Server route/asset regression and release-script regression suite. Self-contained
Linux publish and disposable Linux archive generation were exercised. The artifact
check passed against published/extracted Server output and rejected the earlier
stale bundle against a fresh reference. Dependency installation reported zero
known advisories after omitting the unused prose plugin.

Initial sandbox checks lacked restored .NET assets, network restore and browser
process permissions/libraries/fonts. Validation completed with approved process
access and browser dependencies extracted only under `/tmp`; no machine packages
were installed. No live provider or deployed HTTPS/VPN campaign was performed.
These are Codex local checks; the Worker's separately configured validation remains
the authoritative gate before commit/integration.
