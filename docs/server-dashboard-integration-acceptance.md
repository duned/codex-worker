> This document records the earlier guided legacy-dashboard integration.
> The sole production UI is now React + Untitled UI. Use the
> [final migration checklist and evidence](server-dashboard-final-review.md) for
> current architecture, parity, browser and packaged-artifact acceptance.

# Guided Server dashboard integration acceptance

The implemented journey is administration sign in → Settings / Server GitHub
connection → Projects / Create project → Workers / Add Worker or associate an
existing Worker → contextual preparation → explicit Worker scheduling activation.
Each step can be resumed and performed in another order. Projects may exist
without Workers. Home derives milestones, activity and next actions from current
Server observations; it does not persist wizard completion or authorize execution.

## Ownership and architectural review

| Concern | Authoritative owner and dashboard behavior |
| --- | --- |
| Administration | Server administration sessions issue the cookie and CSRF token. The browser uses the management token only for sign in. Bearer management-token CLI/API clients remain supported independently of cookies and CSRF; an invalid explicit bearer does not fall back to a cookie. |
| Project definitions | The central registry owns identity, revisions, enabled state, requirements and discovery policy. Review verifies Server repository/base-branch reads; edits retain policies and reject stale revisions. A new definition retains the existing enabled default but does not activate a Worker, enqueue Issues or apply ready labels. |
| Worker enrollment | The existing pairing protocol authorizes the specific Worker identity, destination and operation. One-use authorization is transient. Association preserves local identity, history and uncertain resources; previous leases must be reconciled locally. Workers initiate outbound communication; no inbound Worker port is required. |
| Preparation | Typed provisioning commands and node-local policy own actions and durable operation state. Current node capabilities plus retained operations drive both Home and guided Server connection. Active Worker commands remain visible ahead of newer terminal reports. Refresh recovers progress; navigation never submits actions. |
| Execution readiness | Server Worker diagnostics derive project revision/freshness, scoped eligibility and activation blockers. Scheduling-policy mutations recheck this evidence. Worker Codex preflight and GitHub authentication are independent of Server GitHub connection. A cached snapshot cannot grant scheduling authority during an outage. |
| Requests and events | One session polling owner and one cancellable Worker SSE owner per tab. Equivalent pending ordinary reads share a bounded request registry; route/session changes cancel obsolete reads, and successful mutations invalidate pending observations. Late completions cannot publish another resource's context. |
| Recovery and credentials | Existing contextual execution/Issue administration, credential metadata/delivery permissions and advanced operation reconciliation remain reachable. Browser views do not expose stored credential payloads or discard Worker recovery resources. |

The embedded module bundle remains the deployed frontend; no parallel registry,
association workflow, scheduler or readiness persistence was added. Server
settings such as administration origin, provisioning/elevation policy and runtime
configuration remain deployment-owned; Settings displays applicable policy and
advanced capabilities, rather than silently changing deployment configuration.
Standalone Worker ownership and existing API/session/proxy authorization are
unchanged. Server connection consent clears when leaving its context.

## Deterministic acceptance evidence

The delivered-bundle tests cover clean and configured state, project-first and Worker-first setup,
resource reload and Back context, session expiry/rejection, cancelled and stale
reads, shared reads, uncertain submissions, command deadlines and resumption.
Focused integration regressions verify that Home and Settings agree during
pending/running/failed/timed-out authentication and verified completion, that
navigation clears Server connection consent, and that active Worker commands
retain contextual reconciliation controls despite newer terminal reports.

Inherited SSE coverage is present in `server-stream-lifecycle.test.cjs`,
`server-navigation.test.cjs` and `ServerEventStreamTests.cs`. It exercises reader
cleanup/replacement, JSON and rendering errors, bounded event buffers, transport
interruption/retry, teardown/cache restoration, session rejection, and HTTP/SSE
serialization parity with the real dashboard renderer. Issue #251/#252 are
published in 0.23.1. The reported #253 merge `3af7fdee2b0c` and closed status are
historical references, not proof of an installed artifact or live behavior.

Local checks for this integration are recorded below. Worker-configured checks
run separately and remain authoritative before integration.

- `node --test --test-isolation=none tests/dashboard/*.test.cjs`: 96 passed.
- `dotnet build CodexWorker.sln --no-restore -m:1 -p:UseSharedCompilation=false`: passed with zero warnings/errors.
- Restore initially could not reach NuGet; offline restore succeeded with
  `dotnet restore CodexWorker.sln --source /home/azureuser/.nuget/packages -p:NuGetAudit=false -m:1`.
- `dotnet test CodexWorker.sln --no-build --no-restore -m:1`: the sandbox
  denied VSTest loopback sockets; the run with approved local socket access passed
  1,509 Worker/Server tests (including API/session/SSE regressions) and 269 toolbox
  tests, with no failures or skips.

## Disposable HTTPS acceptance campaign

No disposable live deployment was supplied for this task. No operator VM was
modified. No artifact/source-commit verification, live browser layout/accessibility
review, provider login, real Worker preparation or HTTPS outage campaign was run.
Automated fixtures do not establish those results.

Use a designated disposable deployment with a trusted certificate and configured
administration origin. Follow [stream/artifact acceptance](dashboard-stream-acceptance.md),
[HTTPS identity/proxy acceptance](remote-https-acceptance.md),
[project acceptance](project-onboarding-validation.md), and
[Worker enrollment/association](worker-onboarding.md). Record artifact version,
source commit, OS/browser, public origin, and each result without secrets.

1. Fresh browser and empty registry: sign in, reload without token entry, connect
   Server GitHub through explicit device authorization, create a project, enroll a
   Worker, prepare the selected project, then explicitly activate scheduling.
   Before activation, verify no new execution or Issue readiness changes.
2. Separate datasets: create the project before connecting GitHub using deliberate
   manual repository entry (saving still requires verification); enroll a Worker
   before any project; inspect an already configured deployment. Return to Home
   and confirm independent milestones and actionable incomplete/unavailable state.
3. Reload/Back across project, Worker, execution, credential and Issue contexts.
   Navigate during preparation; recover the same operation without resubmission.
   Exercise failed preparation, stale revisions/heartbeats, expired challenge,
   queued cancellation, and lost mutation responses. Reconcile uncertain operations
   only after checking node quiescence. Confirm focus survives unaffected polls.
4. Expire/reject/logout the session and interrupt/restart the disposable Server.
   Count active requests after cancellation settles: one SSE stream per authenticated
   tab, one polling owner, no duplicate equivalent pending reads and no stale view
   publication. A restart may invalidate the in-memory session and require sign in.
5. Verify management-token CLI/API access, invalid bearer rejection, HTTPS proxy
   origin/identity restrictions, independent Worker authentication and outbound
   communication. Review keyboard, screen-reader, 360px layout and 200% zoom.
   Do not export tokens, pairing authorizations, device codes or raw Network traces.

## Deferred scope

Full automatic Worker tool/account provisioning, comprehensive credential
migration/rotation/recovery, automatic DNS/TLS/firewall installation, and the
separate historical-terminal-report migration defect are deferred. Association
preserves history; this integration does not claim to repair that reporting defect.
No product version change or release is part of this task.


## Sidebar and path-navigation acceptance (#263)

The embedded Server shell now uses the specified sidebar, compact session header,
centered login, Home next actions and one-current-step project/enrollment/Worker
assistants. Canonical product links use same-origin paths and query context. The
Server maps an explicit UI GET allowlist; the proxy retains default VPN protection
for all shell routes. Authorization, revisions, recovery, node-local consent,
provider scope, one polling/SSE owner and standalone Worker behavior are retained.

Deterministic coverage includes actual History API push/replace/pop behavior,
legacy-bookmark normalization, deep-link session hydration, resource/filter/offset
context, stale read cancellation, scroll restoration, observation disclosure/focus
retention and one-step project form behavior. API tests cover supported anonymous
shell deep links, unknown API/protocol/health/UI paths and unsupported methods.
Proxy tests cover both denied and management-network UI deep links.

Local self-validation for this change:

- `node --test --test-isolation=none tests/dashboard/*.test.cjs`: 101 passed.
- `dotnet build CodexWorker.sln --no-restore -m:1 -p:UseSharedCompilation=false`:
  passed with zero warnings/errors after offline restore from the package cache.
- `dotnet test CodexWorker.sln --no-build --no-restore -m:1`: 1,512 Worker/Server
  and 269 toolbox tests passed with local socket access. The initial sandbox run
  was aborted because VSTest's loopback communication socket was denied.
- `python3 tests/remote-https-proxy-tests.py`: passed with local socket access,
  disposable nginx/TLS configuration and an HTTP stub; initially skipped by the
  sandbox's loopback restriction.
- HTML nesting and diff consistency checks passed. These are Codex self-checks;
  separately configured Worker validation remains the authoritative gate.

Rendered browser review remains required on a disposable test deployment. This
execution environment has no browser executable or browser tool, so desktop,
360px mobile, 200% zoom and representative screenshots were unavailable; DOM
coverage does not establish visual fidelity. No operator VM or live provider
credentials were used. Review these screens with deterministic local data:

1. Login (including origin/session errors), Home with empty and configured state,
   each sidebar selection and narrow wrapping navigation.
2. Project and Worker lists/details, missing-resource errors and long names/IDs;
   check primary preparation versus advanced administration and Back to list.
3. Every project step, manual fallback, invalid advanced fields, review, revision
   conflict and lost-save recovery; each enrollment step and authorization expiry.
4. Each Worker preparation step with stale/missing/ready evidence and project
   context; local consent, retained operation cancellation/reconciliation and
   explicit activation safeguards must remain visible on their relevant step.
5. Settings connection/credentials and expanded advanced diagnostics; keyboard
   labels, visible focus, native dialogs, status text and long errors/public keys.
6. Scroll a resource well below its heading, expand diagnostics and wait for
   polling/SSE; position and focused controls must remain stable. Apply filters,
   change steps, reload and use Back/Forward; compare resource context and scroll
   coordinates. Navigation must never resubmit a pending operation.

Capture reviewable screenshots of login, Home, lists/details and assistant steps
at approximately 1280px and 360px, plus 200% zoom. Exclude tokens, device codes,
authorization values, cookies and provider secrets from screenshots and traces.


## Isolated Worker detail Untitled UI evaluation

`/workers/{workerId}/poc` now owns its complete visible shell in React: the public
Untitled UI sidebar/mobile dialog, heading/status/capacity hierarchy, current work,
recent execution table, capability surfaces and secondary control rail. Ordinary
routes retain their existing implementation. Tailwind tokens and React Aria source
components are bundled into the existing two embedded assets. The shared session,
request and SSE owners remain authoritative; React receives snapshots/callbacks.

See [PoC acceptance and packaged behavior](server-worker-detail-poc.md) for source
provenance, pinned build dependencies, browser fixtures and the real artifact
regression. Build machines need Node/npm; installed machines do not. Issue titles
and stage history remain explicit API limits, and history remains a bounded latest
50-request projection. No contract, scheduling rule, credential permission, product
version or ordinary dashboard theme is changed by this evaluation.
