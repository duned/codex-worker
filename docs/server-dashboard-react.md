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
uses the current dashboard. Home, lists, Projects, Executions and Settings explicitly
link to their current implementation with the selected resource and query intact.
They do not offer simulated observations or unfinished actions. Legacy `/home`,
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
| `/projects`, `/projects/{id}` | `issue`, `issueState`, `label`, `issues` | Projects, GitHub access/issues/discovery/relationships; revision-fenced create/edit/lifecycle/delete, Issue editing/eligibility/enqueue |
| `/workers`, `/workers/{id}` | `step`, `project` | Worker registry/diagnostics, nodes/commands, projects and latest 50 executions; enrollment, preparation, activation/drain/disable, API-token and delivery revocation |
| `/executions`, `/executions/{id}` | `project`, `state`, `issue`, `offset` | Execution list/detail; queued cancellation and evidence-based uncertain integration reconciliation |
| `/settings`, `/settings/{credentialId}` | `node` | Credential metadata, nodes, Server GitHub connection, command history; credential create/replace/assign/revoke and typed provisioning |

`src/app` owns mounting, routes and error handling; `src/pages` owns section pages;
`src/features/workers` owns the first read-only feature composition; `src/shared`
owns the shell, theme and typed API/session hooks. `src/untitled` remains upstream
source. The retained PoC `detail.jsx` and `model.js` are reused presentation modules;
further migration should type them alongside their feature work, rather than copy
them. The compatibility entry `shell.jsx` re-exports the shared TypeScript shell.

The preview restores the existing administration cookie, holds CSRF only in
memory, clears transient sign-in input, expires sessions, aborts reads on session
replacement/logout/unmount and discards obsolete route reads. It makes bounded
read requests on entry, with no polling/SSE or automatic resource mutations yet.
Reload retrieves current observations; this preview does not claim live updates.
Resource errors/deletion do not select another ID. Session logout can be explicitly
retried after a lost response using transient CSRF. Later mutations must preserve
Server authorization, consent, revision/lease checks and uncertain-response
reconciliation; they must use managed React Aria dialogs, never native dialogs.
Existing legacy mutation behavior remains under its existing owner during migration.
No API contract, scheduling/readiness rule or standalone Worker UI changes here.

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
