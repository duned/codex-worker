# Production React dashboard architecture and build

`src/CodexServer/worker-poc` is the single frontend package, named
`codex-server-dashboard`. The historical directory name preserves the accepted
Worker detail design and official public Untitled UI source/license provenance.
All canonical screens use one Vite graph, React root and BrowserRouter. There is
no legacy entry, PoC application, DOM bridge or dashboard JavaScript request owner.
The standalone Worker dashboard is unchanged.

See [navigation](server-dashboard-navigation.md) for canonical routes and query context. Preview bookmarks redirect to canonical routes.

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
settings. Connection failures are retried with short backoff for a 25-second readiness
window. HTTP errors (including redirects), certificate errors and generation
mismatches fail closed immediately. Diagnostics include the failing URL, cause,
elapsed time and attempt count; failure restores the previous generation.
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

### Development destination permissions after updates

`cw dd` checks effective ancestor traversal and destination read/write access
before building or restarting. If it reports an inaccessible path, inspect that
exact path with `sudo getfacl -p PATH`. A named `--x` entry with `mask::---` grants
no effective traversal. The installer now preserves explicit named-user `--x`
opt-ins on `/var/lib/codex-server` across updates, including repairing this old
ineffective-mask case. It leaves the dedicated dashboard subdirectory untouched.
Production installations without these explicit entries remain mode 0700.

For the development-only setup above, replace `DEVELOPER` with the local account:

```sh
sudo install -d -o DEVELOPER -g codex-server -m 0755 /var/lib/codex-server/development-dashboard
sudo setfacl -m u:DEVELOPER:--x,g::---,m::--x,o::--- /var/lib/codex-server
sudo getfacl -p /var/lib/codex-server
sudo -u DEVELOPER test -x /var/lib/codex-server
sudo -u DEVELOPER test -w /var/lib/codex-server/development-dashboard
```

Inspect existing ACL entries before changing the mask: its execute bit affects
all named entries. Grant no parent read/write access, use no recursive `chown` or
`chmod`, and keep persistent data files private. Do not run `chmod 0700` on the
parent after this opt-in: it disables the ACL mask again. Normal production
installs need no developer ACL. Retry `cw dd` once the effective permissions are
correct; do not restart services to diagnose filesystem access.

### Server updates reset the development dashboard

A successful `sudo codex-server update` retires the validated `current` and
`previous` dashboard generations deployed by `cw dd`. The restarted Server uses
its release-bundled React assets. New deployments contain only `index.html`,
`assets.json`, and the assets listed there; unused `.vite` build metadata is not
copied. Updates also accept legacy `.vite/manifest.json` metadata after validating
its schema and references to hash-validated assets. Unknown content, symlinks,
invalid manifests and unsafe roots still prevent retirement before release selection
changes. The override directory, unrelated files, and
systemd drop-in remain intact. Activation failure restores the generations before
restarting the previous release. Run `cw dd` again to explicitly deploy a local
development bundle after updating. Worker updates do not reset this override.
