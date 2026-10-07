# Final React + Untitled UI integration review (24.12)

All canonical Server dashboard routes use the promoted Worker-detail React
application. The Server legacy HTML/scripts, hidden control targets, DOM bridge
and separate PoC bundle are absent. Historical preview/PoC URLs only redirect.
No backend contract, standalone Worker dashboard, product version or release
publication changes are included. The checked-in source version remains unchanged;
24.12 is an Issue series, not a product version.

## Route / action / API parity checklist

All API paths below are existing Server contracts under `/api/v1`, except the
public `/api/status` and `/api/version`. Each checked row is implemented and has
deterministic model/runtime and production-bundle browser evidence. Read-only
observations never authorize mutations. Detailed field/action inventories remain
in the linked feature guides; this table identifies the combined acceptance owner.

| Reviewed workflow and route | Existing API / explicit action | Primary information versus advanced evidence | Evidence |
| --- | --- | --- | --- |
| ✓ Login / every deep link | Administration session GET/POST/DELETE; cookie + CSRF | Sign in/session errors, safe origin help; token input cleared before submission | `react-infrastructure.test.cjs`, navigation and Worker browser harnesses |
| ✓ Home `/home` | Workers, projects, latest 50 executions, nodes, Server status, Server GitHub connection GET | Current work, independent setup/next actions, recovery; advanced bounded activity, source/freshness/limits | `home-overview.test.cjs`, `home-browser-review.cjs` |
| ✓ Project list/detail `/projects[/{id}]` | Projects GET | Friendly names, repository, lifecycle, blockers; advanced requirements and discovery observations | `react-projects.test.cjs`, `projects-browser-review.cjs` |
| ✓ Project create/edit | GitHub repository discovery GET, projects/verify POST, projects POST/PUT | Three-step verified form; advanced typed requirements, version/scope, labels, all discovery policy fields | Projects harness: discovery/manual entry, failed verification, conflict and lost save |
| ✓ Project enable/disable/delete | Project lifecycle PUT / project DELETE with expected revision | Explicit consequences; Server revision/in-use checks, retained history | Projects harness: lifecycle/delete and lost-response reconciliation |
| ✓ Project Worker preparation | Workers/diagnostics GET, canonical preparation link | Missing/stale requirements, revision and checkout state; advanced independent auth/sync evidence | Projects and Workers harnesses, shared diagnostics regression |
| ✓ Project Issue list/detail | Project github/access and github/issues GET; `issue`, `issueState`, `label`, `issues` URL context | Titles, eligibility, labels, blocked-by, repository-read check; bounded list limits | Projects harness: permission failure, filters, reload/Back |
| ✓ Issue create/edit | Issue previewOnly POST/PATCH, explicit apply | Managed draft/preview; retain draft and uncertain attempt across SPA navigation | Projects harness: create/edit, lost response and draft resumption |
| ✓ Issue labels/dependencies | Configured labels and native blocked-by PUT with preview/apply | Configured labels and positive Issue number; no substitute relationship workflow | Projects model and browser relationship/label checks |
| ✓ Issue enqueue/eligibility refresh | Issue enqueue and eligibility/refresh POST | Explicit admission confirmation and current reasons; uncertain enqueue requires positive matching request evidence | Projects harness: enqueue, timestamp-based refresh and lost responses |
| ✓ Worker list/detail `/workers[/{id}]` | Workers, diagnostics, nodes, latest executions GET | Name, independent connection/prerequisites/capacity, current work; advanced IDs, platform, scoped capabilities and retained plans | Worker model/render/migration tests, both Worker browser harnesses |
| ✓ Worker enrollment/association | Public version GET, workers/onboarding/authorize POST | Guided public pairing request, transient one-use authorization, explicit node consent; existing identity/history and reconciliation instructions | `workers-migration.test.cjs`, Workers enrollment/resume/reload campaign |
| ✓ Worker preparation | Nodes/commands GET, provisioning/commands POST | Reported readiness, scoped project revision/materialization, explicit advertised tool/auth actions; advanced diagnostics | Workers harness: current/stale/missing evidence, consent, lost command response |
| ✓ Worker scheduling/revocation | Scheduling-policy PUT; authentication/revoke and credential-access/revoke POST | Separate Activate/Drain/Deactivate/API-token/delivery confirmations; fresh Server checks | Worker browser campaigns: changed readiness, pending locks, separate revocation |
| ✓ Executions `/executions[/{id}]` | Executions GET; project/state/Issue/offset filters | Friendly identities, stage/outcome, timing, recovery reason; advanced assignment/lease, lineage and integration evidence | `executions-react.test.cjs`, `executions-browser-review.cjs` |
| ✓ Execution queued cancellation | Execution cancel POST after uncached state check | Confirmation only for queued work; no running-work cancellation | Executions harness: pending dialog lock, changed-state rejection, response loss |
| ✓ Uncertain integration recovery | Execution reconcile POST | Explicit disposition/evidence/commit and returned retry link; advanced retained recovery/attempt records | Executions harness: both dispositions and authoritative refresh |
| ✓ Settings `/settings[/{credentialId}]` | Credentials list/detail GET | Safe metadata, assignment and status; advanced secret reference/version/timestamps, never secret delivery | `react-settings.test.cjs`, Settings metadata reload/deletion campaign |
| ✓ Credential create/replace/assign/revoke | Credentials POST; secret/assignment PUT; revoke POST | Managed secret form and consequence dialogs, clear input before submission; metadata-only uncertain reconciliation | Settings harness: lifecycle, response loss across navigation, logout cleanup |
| ✓ Server GitHub preparation | Nodes/server/github-connection GET; existing typed provisioning actions | Same readiness projection as Home, separate preparation/login consent, deadline-bounded device instructions; advanced capabilities/history | Home + Settings harnesses: pending/failed/expired/authenticated projections |
| ✓ Command cancellation/reconciliation | Provisioning command cancel/reconcile POST | Pending/running status and explicit quiescence confirmation; advanced fixed advertised/elevated actions and public SSH identity | Workers + Settings harnesses: queued cancellation, expiry and uncertain reconciliation |
| ✓ Navigation/themes/dialogs | One BrowserRouter and versioned theme preference; no mutation | Dark-first login/screens/dialogs, selectable light, responsive upstream navigation, contained/restored dialog focus | Navigation, shared-form and feature browser harnesses at 1280/375px; Worker design also 640px |
| ✓ Installed Server shell/assets | Explicit GET routes and manifest-listed local assets | Deep-link/reload parity, preview redirects, unknown-route rejection, protected API | `CodexServerTests`, `ServerEventStreamTests`, published artifact harness |

See [Projects/Issues](server-projects-react.md), [Workers and Home](server-dashboard-react.md),
[Executions/recovery](server-executions-react.md), [Settings](server-settings-react.md),
and [canonical navigation](server-dashboard-navigation.md). Deployment settings
(origin, provisioning/elevation policy, runtime configuration) remain deployment-owned;
the UI displays their applicable policy rather than inventing configuration writes.

## Combined architecture and integration fixes

One `app/main.tsx` mounts one BrowserRouter/SessionProvider. `shared/api/client.ts`
is the HTTP boundary; one `DashboardRuntime` owns session generation, CSRF,
private QueryClient, expiry, cancellation, mutation fences, one bounded Worker SSE
reader and sequential polling. Stream replacement waits for reader release.
Successful mutations cancel stale queries and invalidate the private observation
cache. No component owns a second Server-fact cache or scheduling/readiness rule.
The preference store contains only a versioned light/dark selection.

Project/Issue and credential workspaces retain transient drafts/attempt lineage
across SPA navigation, inside the authenticated view. Session replacement unmounts
private views and clears caches. Uncertain mutations require explicit authoritative
reconciliation; polling, navigation and refresh do not replay writes. Stored secret
payloads are never fetched; secret inputs/challenges stay transient and screenshots
are captured without tokens, secrets or device codes. Enrollment retains only its
validated public request for tab resume. Node-local consent and Server lifecycle,
revision, lease, authorization and CSRF checks are unchanged.

Review closed two integration gaps: credential metadata/assigned-Worker links now
use canonical router paths, preserving the session and pending-attempt owner;
Projects now reuses shared diagnostics validation instead of validating the same
Server contract twice. Positive safe-integer reported revisions remain validated.
Project fixtures now include the Server's required project name and the Home reads
used by cross-feature navigation. Browser coverage asserts that metadata navigation
retains the document and adds dark/light desktop/mobile login screenshots. Artifact
coverage explicitly asserts canonical reloads, redirect locations and rejection of
unknown canonical paths as well as the complete fresh asset graph.

Shared public Untitled UI source, upstream MIT license/bundle banners, navigation,
forms, table surfaces, badges, buttons and checkboxes remain reused. React Aria owns
managed form/confirmation dialogs, Escape handling and focus containment/restoration.
Advanced disclosures keep necessary diagnostics accessible without raw process logs.

## Current local validation evidence

Evidence below is from this combined checkout, not inherited child acceptance or
the Worker's separately configured authoritative gate. Review tools and artifacts
use disposable `/tmp/issue270-*` paths; no operator packages or services were changed.

- Locked npm dependencies restored offline from the existing cache; the first
  sandbox attempt denied esbuild subprocess execution (`EPERM`). Permitted local
  subprocess access completed restoration. NuGet restored from the existing local
  package feed with audit disabled for that offline restore.
- `npm run check --prefix src/CodexServer/worker-poc`: typecheck, lint and fresh
  production build passed. Vite reports an existing >500 kB application chunk
  advisory; it is not a compiler/analyzer failure and was not suppressed.
- `node --test --test-isolation=none tests/dashboard/*.test.cjs`: 60 passed after
  correcting the missing project-name fixture. Final focused infrastructure/project
  checks passed all 25 tests after the canonical Settings link change.
- `dotnet build CodexWorker.sln --no-restore -m:1 -p:UseSharedCompilation=false`:
  passed, zero compiler/analyzer warnings and errors. The .NET suite ran once:
  `dotnet test CodexWorker.sln --no-build --no-restore -m:1` passed 1,565
  Worker/Server and 269 toolbox tests, no failures or skips. Local socket/process
  access was used. No production .NET contracts changed during review.
- Chromium production-bundle harnesses passed: `home`, `projects`, `workers`,
  `executions`, `settings`, `worker-poc`, `shared-form` and `dashboard-preview`.
  They cover desktop/mobile, both themes, direct links/reload/Back, stale/failed/
  empty data, managed-dialog keyboard focus, pending/uncertain operations, consent,
  CSRF and secret cleanup. Projects/Settings/navigation reran after their fixture,
  canonical-link and login-coverage fixes; correctly passing child campaigns were
  not repeatedly run. Browser dependencies/libraries/fonts remained under `/tmp`.
- Release Server publish to `/tmp/issue270-publish` passed. This local artifact
  is framework-dependent (.NET 10 present), not a product release. A separately
  regenerated `/tmp/issue270-reference` passed every-byte asset/shell comparison
  using `worker-poc-artifact-review.cjs`, with Node/npm absent from the Server PATH.
  Canonical list/detail deep links and reload, historical redirect locations,
  unsupported methods, removed resources, unknown routes and protected API rejection
  all passed. No external provider or runtime frontend service was used.
- `bash tests/release-tests.sh`: passed the existing deterministic packaging
  orchestration regression. No release was published. Diff/architecture/security
  review confirmed no version, backend ownership or standalone Worker changes.

Representative screenshots were inspected for hierarchy, wrapping and theme/dialog
composition. The retained fixtures contain no entered secrets or device codes:

| Surface | Review artifact |
| --- | --- |
| Dark desktop login | [Login](images/server-dashboard-final/login-dark-1280.png) |
| Light mobile login | [Login](images/server-dashboard-final/login-light-375.png) |
| Dark Home / independent setup | [Home](images/server-dashboard-final/home-1280.png) |
| Mobile project form | [Project](images/server-dashboard-final/project-form-375.png) |
| Dark Worker detail | [Worker](images/server-dashboard-final/worker-375.png) |
| Execution list | [Executions](images/server-dashboard-final/executions-1280.png) |
| Dark mobile credential form | [Settings](images/server-dashboard-final/settings-form-375.png) |
| Light Server preparation consent | [Preparation](images/server-dashboard-final/preparation-light-1280.png) |

These are local self-checks. The Worker runs its separately configured authoritative
validation gate before commit/integration.

## Existing API and campaign limits

Execution/history/Issue lists are bounded, not totals. Contracts lack full stage
history, complete recovery chronology and some Issue titles; the UI states these
limits and does not fabricate percentages or progress. No credential idempotency
or secret-comparison API exists: inconclusive uncertain outcomes stay fenced.
Browser-only attempt metadata does not survive a fresh session/reload; inspect
Server/GitHub state before resubmission. Repository read, Server auth, Worker auth,
checkout/push permission and scheduling readiness remain independent.

Deterministic fixtures and a disposable published Server do not establish live
provider login, credential delivery, real Worker provisioning or deployed HTTPS/VPN
behavior. Those campaigns were not run or authorized here. Use the designated
[HTTPS acceptance](remote-https-acceptance.md) and
[stream acceptance](dashboard-stream-acceptance.md) environment for them. No product
release publication or operator-machine change is part of this review.
