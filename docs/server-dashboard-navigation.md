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
Worker prepare links scroll to it; the Home Server connection link scrolls to the guided Settings panel. Operation history filters to the selected node; when
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
rendering check that their initiating view is still current. Rendering a view never starts a provisioning operation or registers polling timers or global listeners. The Server connection challenge has one transient deadline timer to remove it from the UI at expiry. Stable Home markup is not
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

Local automated validation for the guided Server connection change:

- `node --test --test-isolation=none tests/dashboard/*.test.cjs`: 60 passed,
  including explicit consent/local policy, preparation refusal, completion,
  challenge expiry, queued cancellation, reload and lost-response recovery.
- `dotnet build CodexWorker.sln --no-restore -m:1 -p:UseSharedCompilation=false`:
  passed with zero warnings/errors.
- `dotnet test CodexWorker.sln --no-build --no-restore -m:1`: 1,474 Worker/Server
  and 269 toolbox tests passed. The revised Server connection API regression
  also passed separately after making its setup independent of the background
  dispatch timer. Tests verify authenticated challenge recovery/cleanup and
  recovery beyond the general history limit, alongside existing executor and
  storage failure/cancellation/expiry coverage.
- Initial restore could not reach NuGet and the sandbox denied MSBuild/test
  sockets. Offline restore from the existing package cache succeeded; tests ran
  with local socket access. Worker-run configured validation remains the
  authoritative gate before integration.

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


## Guided Server GitHub device login

Home → Connect Server GitHub opens Settings → Server GitHub connection. The
panel uses the existing typed provisioning commands, managed GitHub CLI directory,
and service-account authentication probes. It never stores a browser token or
starts login on render. The authenticated `GET /api/v1/nodes/server/github-connection`
returns the latest 30 Server GitHub operations with active operations first, plus
Server-local provisioning/elevation policy. Unrelated history cannot hide an
in-flight login. The node inventory also includes these operations for readiness.

The operator explicitly authorizes preparation/device login, and separately
installation elevation when required. Node-local permission remains authoritative.
After successful preparation, Start device login queues `Login`; only a running,
unexpired challenge shows the fixed GitHub verification URL and temporary code.
Successful process submission is pending, not Connected. Connected requires a
current, healthy Server GitHub authentication observation in the managed service
account context. Repository reads, Issue writes, Worker login and repository pushes
remain separate checks; use Projects and Workers for their evidence.

Queued operations can be cancelled. Running operations stop at their deadline;
expired or uncertain operations must be confirmed stopped before retrying.
Submission/cancellation response loss disables actions until a fresh snapshot
recovers the actual operation. Reload, navigation and provider return use that
same snapshot without submitting again. Completion/expiry removes the challenge
from active UI/API responses; terminal reports erase it from existing durable
storage. Existing protected storage and log redaction remain unchanged. Preparation
refuses unrelated operator authentication rather than replacing it. Advanced node
capabilities and history remain available; Worker remote login is unchanged.

Manual HTTPS acceptance (not run as part of automated tests):

1. Use a disposable Server with a dedicated service account and the existing
   trusted HTTPS deployment/administration session. Do not change operator VMs.
   Confirm disabled provisioning explains recovery and still permits auth checks.
2. With local provisioning enabled, authorize preparation. If GitHub CLI is
   missing, separately authorize installation only where local elevation is allowed.
   Confirm an unrelated operator configuration is refused and left intact.
3. Prepare, start login, open the verification URL and approve only the displayed
   code. Navigate away/back and reload before approval; the same operation/code
   should return. No new Login should appear in advanced history.
4. Complete GitHub verification. Confirm the challenge disappears and Connected
   appears only after the service-account authentication check. Check repository
   reads and Issue write authorization in Projects, and Worker login/push separately.
5. Repeat without approval until expiry; confirm code removal and retry only after
   terminal confirmation. Cancel a queued operation. Simulate a lost POST response
   and confirm refresh recovers the existing operation rather than replaying it.
6. Sign out while a challenge is visible; confirm challenge/consent are cleared.
   Verify no device code/token appears in Server logs. No live provider credentials
   are required by the automated dashboard, store, executor or API tests.

## Contextual Worker preparation and project association

Open a registered Worker to resume preparation. Connectivity, tools/configuration,
Codex service-account authentication and execution preflight, Worker GitHub
service-account authentication, scoped repository permissions and project
requirements are presented separately. Registration, health, a submitted command,
and Server repository readability never establish execution readiness. Stale,
cached, unavailable and unreported observations remain incomplete. Worker-local
project failures and revision mismatches remain visible beside healthy projects.

Project → Prepare / associate Worker and Worker → Prepare for this project use the
same Worker detail and the `project` query context. The existing managed ownership
model delivers the central catalog and revisions to authorized Workers; there is
no separate binding store or browser-only association. A registered Worker with no
central project links to Projects; a project with no Worker links to Add Worker.
Missing checkouts remain lazy until assignment. Scoped Worker Git/GitHub capability
evidence is distinct from checkout materialization and never performs a test push.
The Git capability's repository read verification action only verifies readability,
not push authorization. Repository permissions must still allow the configured
Worker identity to push, and Worker scoped eligibility remains authoritative.

Supported next actions use the node's registered typed actions and local policy.
Unadvertised actions explain the required service-account/node preparation. For
Codex, install the supported CLI if absent, authenticate in the Worker service
account (supported device login when available), then re-detect to run readiness
preflight. For GitHub, prepare product-managed authentication before supported
login; otherwise the node administrator must configure that service-account
context. Repair invalid local managed runtime defaults on the node and refresh
configuration. Requirements outside the supported tool catalog require node-side
preparation. No shell text, executable path or new installer protocol is accepted.

Rendering, navigation and reload do not submit installs/logins. The authenticated
`GET /api/v1/nodes/{nodeId}/commands` returns bounded node history with active
operations first, so unrelated history cannot hide a retained operation. Progress,
failure and expiry are visible next to each capability. Cancel queued operations
or, after verifying node quiescence, reconcile an expired running operation using
the contextual controls. Never retry an unconfirmed submission until refresh
recovers its operation. Advanced command history, credentials and uncertain
execution recovery remain available. Provider tokens stay off the browser.

Activation is explicit in Worker administration. The Server re-evaluates the
existing scheduling-policy request and returns conflict when evidence is missing:
current running heartbeat, active Worker API authentication, live synchronized
managed ownership, fresh typed tool/auth observations, successful Codex execution
preflight, and at least one enabled project with current revision and scoped
capability eligibility. Pending provisioning must be reconciled first. A valid
not-yet-materialized project can qualify; a failed project does not prevent another
healthy project from qualifying. Preparation and association never enable
scheduling, enqueue work or change Issue eligibility labels. Drain/disable and
active lease/recovery semantics are unchanged; assignment still checks authority.
Standalone Worker ownership remains supported and does not acquire managed
activation through this presentation.

Manual HTTPS acceptance (live checks not run by automated tests):

1. On disposable HTTPS Server/Worker nodes, register a Worker with no projects or
   tools. Confirm connectivity is visible while activation is blocked. Create a
   central project and open preparation from either resource.
2. Under explicitly permitted node-local policy, start one supported action and
   navigate/reload during progress. Confirm the same operation returns, even after
   unrelated history exceeds a page. Verify queued cancellation and expired-running
   reconciliation after node quiescence, including a lost submission response.
3. Leave a tool/auth step incomplete or deny its local action. Confirm the required
   node-side step is explained. Complete permitted Codex login and re-detect;
   confirm login alone never reports successful execution preflight.
4. Change the project revision, remove repository permission, and introduce a local
   managed configuration failure. Confirm stale/unavailable evidence blocks
   activation and healthy project diagnostics remain visible. Restore configuration
   and read/push permission without destructive test pushes.
5. With current eligible evidence, explicitly activate, then drain and disable.
   Confirm preparation never created queue entries or changed labels and existing
   uncertain executions remain accessible. Use the existing trusted HTTPS session,
   identity and credential administration paths; do not modify operator VMs.

A full provisioning assistant, arbitrary project runtime installation, automatic
provider authorization and autonomous remote login are deferred. This iteration
presents actual supported observations and operations, not simulated completion.
