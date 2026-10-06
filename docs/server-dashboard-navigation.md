# Server dashboard screen and flow map

This is the approved navigation and onboarding map for subsequent dashboard
work. The Server retains its embedded HTML/plain JavaScript frontend, dark teal
header, light content surfaces, compact resource rows and contextual next actions.
All displayed operational facts and mutations use existing Server APIs. There are
no simulated actions, completion flags, framework dependencies or new scheduling
rules. The local Worker dashboard and standalone ownership mode are unchanged.

## Screens and stable context

| Screen | Route and context | Content and administration entry points |
| --- | --- | --- |
| Home | `#/home` | Resumable setup milestones, actual bounded execution activity, connectivity/scheduling blockers, Server status and capacity. Links to the next resource action. Configured systems show completed milestones in a collapsed disclosure. |
| Projects | `#/projects`, `#/projects/{id}`, optionally `?issue={number}` with `issueState`, `label`, `issues=1` list context | Central definitions, create/edit/enable/disable/delete, requirements, Worker-reported preparation/revision/freshness, linked Workers, project-filtered executions, GitHub read checks and Issues. Issue create/edit, configured eligibility labels, blocked-by relationships, enqueue and eligibility refresh retain their existing previews and authorization. |
| Workers | `#/workers`, `#/workers/{id}`, `#/workers?prepare=1` | Connection, capacity, readiness and preparation; project eligibility and materialization diagnostics; scheduling enable/drain/disable and separate API-token/delivery-authorization revocation. Contextual node provisioning for registered Workers and advanced operation history. Registration remains the existing node-local procedure. |
| Executions | `#/executions`, `#/executions/{id}` | Bounded queue, outcomes, assignment, leases, recovery evidence and attempt lineage. `project`, `state`, `issue`, `offset` query parameters preserve filters/pagination on detail, reload and Back. Queued cancellation and uncertain integration reconciliation retain the existing evidence requirements. |
| Settings | `#/settings`, `#/settings/{credentialId}`, `#/settings?node=server` | Server GitHub login/preparation, Server tool capabilities and advanced provisioning diagnostics/history; credential metadata/create/replace/assign/revoke. Credential detail opens its metadata dialog after session restore. No secret is retrieved by metadata views. |

Main navigation returns to each resource list. Resource links and browser Back /
Forward use URL fragments, so the same Server `/` endpoint serves reloads without
new path handling. Unknown or malformed routes fall back to Home. A missing or
deleted resource produces its API/context error; it does not silently select a
different resource. The selected resource remains in the URL across sign out and
sign in. URLs contain IDs and filters only. Dialog edit/secret drafts are transient
and are cleared/closed when leaving the context.

The shared node panel selects only Workers in Workers and the Server in Settings.
Prepare links scroll to it. Operation history filters to the selected node; when
no node is selected, the advanced history can show all operations. Capability
command IDs and the Worker provisioning summary live in advanced disclosures.
Public SSH identities, active device-login instructions, command cancellation,
quiescence-confirmed reconciliation and all existing elevation/destructive-action
warnings remain available. Tools are still dispatched only through advertised,
typed, allowlisted actions, subject to node-local policy.

## Resumable setup from authoritative state

All milestones are independently actionable; project-first and Worker-first setup
are supported. Navigation is always available to an authenticated administrator.
There is no required wizard order or browser-persisted completion bit.

| Milestone | Completion source | Next action when incomplete or unavailable |
| --- | --- | --- |
| Connect Server GitHub | `/api/v1/nodes`: Server GitHub CLI installed, authentication satisfied, healthy capability and non-stale observations | Settings → Server node → GitHub login preparation/check authentication. Authentication does not establish repository write or Git push permission. |
| Create a project | `/api/v1/projects`: at least one persisted central definition | Projects → Add project or inspect definitions. A disabled project is still a created definition, not permission to schedule it. |
| Prepare a Worker | `/api/v1/nodes`: a connected Worker with current observations and authoritative `ready` execution prerequisites | Workers → register through the supported node-local procedure, then inspect tools, login and configuration. Preparation does not grant project eligibility, scheduling permission or provider access. |

Failures and unavailable inventory reset the corresponding projection to
Unavailable. Stale observations cannot complete GitHub/Worker milestones.
Configured systems show actual activity and next steps without forcing setup.
Home activity comes from a separate unfiltered latest 50-request snapshot; it is
not a total-history count or health score. Worker connection/scheduling blockers,
pending/eligibility/recovery evidence, and recent execution outcomes remain
separate. Project preparation links use the central IDs in Worker managed
observations, preserving cached, stale revision and stale heartbeat distinctions;
execution-time project names alone do not establish a central association.

## Navigation, request and session ownership

`dashboard-navigation.js` owns fragment routing, active navigation semantics,
view generation and Home projections. Resource responses and mutation follow-up
rendering check that their initiating view is still current. Rendering a view
never registers polling timers or global listeners. Stable Home markup is not
replaced on every unchanged poll; refreshed Worker/project/execution rows preserve
the identity of focused resource actions.

`dashboard-session.js` owns session restore/sign in/sign out, CSRF, request
cancellation and one polling owner. Polling runs sequential cycles of independent
reads every five seconds after the preceding cycle settles. Session replacement,
logout and page teardown stop polling; obsolete in-flight cycles cannot restart
it. The existing `dashboard-stream.js` retains one cancellable Worker event
reader/retry owner per tab, including back-forward cache restoration. Stream
interruption marks Worker connectivity unavailable while the owner reconnects.

The management token is used only for the transient sign-in request and is then
cleared. Reload restores the existing secure administration cookie and CSRF
session. No token is put in a URL, local/session storage or persistent browser
state. Existing HTTPS, allowed origin, identity, cookie, expiry and API
management authorization rules remain unchanged. Node-local provider login,
Server credential delivery authorization and Worker scheduling permission remain
separate boundaries.

Resource administration, execution/Issue flows and provisioning have separate
script modules. `ServerApplication.ReadDashboard` embeds their source into one
self-contained response inside the administration scope, preserving packaging
without additional asset requests. The deterministic dashboard source helper
assembles these same modules for the existing Node/VM tests.

## Validation and manual HTTPS acceptance

Local automated validation for this change:

- `node --test --test-isolation=none tests/dashboard/*.test.cjs`: 53 passed. Tests
  cover the assembled dashboard's first-use/alternate-order/configured/stale/
  unavailable states, route/session restore, filters, Back-style transitions,
  malformed routes, delayed success/failure publication, teardown of in-flight
  polling, contextual node selection, and existing administration controls.
- The existing Server dashboard HTTP test now checks all navigation links and
  that the embedded module placeholders have been expanded.
- `dotnet test CodexWorker.sln -m:1 --nologo -p:UseSharedCompilation=false`:
  unavailable at restore (`NU1301`, permission denied connecting to
  `api.nuget.org:443`). Compilation and .NET assertions did not run. Worker-run
  configured validation remains the authoritative gate.

The Node/VM seams do not establish browser layout or screen-reader behavior.
Live HTTPS/provider actions, a real browser accessibility/responsive review and
real-node provisioning were not run here. No operator VM was modified. Use a
separate disposable HTTPS test deployment following
[remote HTTPS acceptance](remote-https-acceptance.md), with a trusted certificate,
the configured administration origin and disposable registry data:

1. Open `/` in a fresh browser profile. Confirm protected reads require sign in.
   Sign in; check the transient token field clears, only one Worker stream is
   active, and reload restores the session without re-entering the token. Sign
   out and check pending requests stop and resource URLs retain context.
2. Start with no projects/Workers and unavailable GitHub login. Create a project
   first, then return Home. In a second disposable dataset register a Worker
   first. Confirm each independent milestone resumes from Server facts. On an
   already configured dataset, verify collapsed milestones and real activity.
3. Open project, Worker, execution and credential details. Reload and use Back /
   Forward across lists/details/Issue views; check selected IDs, filters and
   pagination. Remove a test resource and reload its old URL; inspect the
   explicit unavailable/deleted response rather than another resource.
4. Temporarily interrupt the disposable Server connection and resume it. Verify
   unavailable/stale state is visible, provisioning actions do not use invalid
   observations, and reconnect/restored tabs retain one stream and one polling
   owner. Expire/reject a session and verify sign-in recovery.
5. Use keyboard-only navigation at desktop and 360px viewport widths, then 200%
   zoom. Check navigation current-page announcement, visible focus, view heading
   announcement, form labels, dialog names/Escape/Close, native advanced
   disclosures and status/error text. Check long IDs/public keys wrap, rows and
   action groups reflow, and content remains reachable without horizontal page
   scrolling. Confirm unaffected polls do not move keyboard focus.
6. Verify each table-listed administration entry point using disposable data
   and existing deterministic API fixtures where possible. Inspect previews,
   revision conflicts, drain/lease warnings, credential metadata restrictions,
   elevation consent and uncertain reconciliation confirmation. Real provider
   login/credential delivery/provisioning requires a separately authorized live
   campaign; it is not needed for automated dashboard validation. Keep tokens,
   device codes and secrets out of screenshots, exported traces and notes.
