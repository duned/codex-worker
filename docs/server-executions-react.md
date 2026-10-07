# React execution and recovery parity (24.12.7)

Executions now use the shared React application at `/dashboard-preview/executions`
and `/dashboard-preview/executions/{id}`. Canonical `/executions` routes retain
legacy ownership until the separate route cutover. The existing public Untitled UI
source, tokens, theme preference, session/query/live owner and managed React Aria
dialogs are reused; no backend contract or standalone Worker dashboard changes.

The default view emphasizes the reported stage or terminal outcome, friendly
project/Worker names, canonical GitHub Issue links, available timing and the next
permitted operator action. Execution failure is distinct from Worker health;
expired uncertain integration has an amber textual status. Recovery reasons remain
visible even when a completion summary is also present. Advanced native disclosures
retain assignment/lease identity and current attempt, eligibility and recovery
records, including verified integration evidence.

## Execution and recovery inventory

| Existing behavior/evidence | Shared React parity |
| --- | --- |
| Execution list | Existing `GET /api/v1/executions` with project/state/GitHub Issue filters, limit 50 and offset bounded to 10,000; previous/next controls. No total count is claimed. |
| Project, work and Worker identity | Names resolved from existing project/Worker inventories, with exact IDs as fallback; canonical safe GitHub Issue links and exact resource links. Deleted dependencies do not substitute another resource. |
| Current work and terminal results | Reported current stage, state, pending/recovery reasons and completion summary. Completed, failed, cancelled and uncertain integration remain distinct. Worker health is not inferred from an execution outcome. |
| Timing | Created/assigned/started/completed timestamps and reported duration or available timestamp-derived elapsed time, using the existing shared helpers. Missing timing stays unavailable. |
| Detail and list context | `project`, `state`, `issue` and `offset` remain in URLs across detail, reload, Back and previous-attempt links. Missing/deleted execution reads show an explicit unavailable state for that exact ID. |
| Queued cancellation | Detail confirmation explains that only queued requests can be cancelled. Fresh uncached read checks current state before `POST /cancel`; Server checks remain authoritative. Running/assigned work has no cancellation control. |
| Uncertain integration | Only Failed + LeaseExpiredUncertain + Expired lease offers reconciliation. Labeled disposition/evidence/full-commit fields preserve the existing `POST /reconcile` requirements. NotIntegrated queues a fresh-workspace attempt; the returned retry is linked with retained list context. Integrated records the verified commit without rerunning work. |
| Assignment and lease | Advanced disclosure retains assigned Worker, Server/local execution and assignment IDs plus lease execution/Worker identity, generation, acquisition, expiry, state and renewal interval. |
| Attempt lineage and recovery | Advanced disclosure retains attempt number, previous-attempt link, recoverability, workspace recovery, validation/integration result, failure classification, recovery state/reason, missing requirements and managed eligibility/reasons/check time. Matching-request browsing allows bounded inspection of subsequent attempts. |
| Pending or uncertain mutations | Shared resource fence prevents repeated submission across polling/navigation. Dialogs prevent duplicate acceptance and dismissal while submitting. Lost/rejected/malformed write responses remain locked until explicit fresh authoritative refresh; failed refresh retains the lock. No write is retried automatically. |
| Refresh, stale reads and session | Shared validated cancellable reads and private session generation. Read failures remove actionable observations; missing resources retain context. Refresh, reload, navigation and observation updates never submit mutations. Disclosures retain expansion during successful snapshot updates. |
| Appearance/accessibility | Default dark and persistent selectable light/dark theme; upstream surfaces/buttons/inputs/status tokens, named modal dialogs and native advanced disclosures. Responsive rows and wrapping evidence keep mobile content reachable. |

## API limits

The execution contract provides the current recovery record, previous-attempt ID
and latest lease, not a complete recovery-event log or stage history. These limits
are stated in the view. No sequence, completed/upcoming stage, percentage, Issue
title, subsequent-attempt ID or history total is invented. The matching-request
list remains bounded and can require pagination. A returned retry link is transient;
after reload, inspect matching requests for subsequent attempts. Recovery evidence
recorded by the Server remains accessible through the original request.

## Validation

Focused deterministic model/contract fixtures are in
`tests/dashboard/executions-react.test.cjs`; retained legacy execution and shared
mutation/session regressions remain relevant. The production-bundle browser harness
`tests/dashboard/executions-browser-review.cjs` uses Server-shaped fixtures for
queued, running/implementation, validating, integrating, completed, failed,
cancelled and uncertain requests at desktop/mobile widths. It covers pagination,
query context, reload/Back, disclosures, stale/missing reads, safe cancellation,
pending dismissal locks, changed-state rejection, evidence reconciliation in both
dispositions, response loss and explicit authoritative refresh.

```sh
npm run check --prefix src/CodexServer/worker-poc
node --test --test-isolation=none tests/dashboard/executions-react.test.cjs tests/dashboard/server-execution-admin.test.cjs tests/dashboard/react-infrastructure.test.cjs
NODE_PATH=/path/to/playwright/node_modules node tests/dashboard/executions-browser-review.cjs
```

These fixtures do not replace deployed HTTPS/session/provider acceptance or the
Worker's separately configured authoritative checks.

Local review passed typecheck, lint and production compilation; 27 focused
execution/legacy/navigation regressions and all 17 shared infrastructure tests in
an isolated run. An initial combined run hit an existing stream snapshot timing
failure; the isolated infrastructure run passed without code changes to that
suite. Chromium review passed at 1280px and 375px with the final production bundle;
desktop/mobile screenshots were inspected under `/tmp/executions-browser-review`.
Chromium required permitted process access and existing temporary browser libraries.
The optional focused .NET test command with `--no-restore` was unavailable because
this checkout lacks `tests/CodexWorker.Tests/obj/project.assets.json` (NETSDK1004).
No live provider or deployed HTTPS campaign, publication or Worker-configured
validation is claimed.
