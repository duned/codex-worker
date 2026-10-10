# Execution history and maintenance

`cw` is the repository developer client. The running Worker's local Management API owns history reads, resource classification and cleanup; the client never opens the execution database or implements Git cleanup rules. Server queue reconciliation remains a separate operation.

For automatic continuation of interrupted Codex implementation, see [Codex interruption recovery](codex-interruption-recovery.md).

## Inspect and select

The Worker exposes a read-only, bounded maintenance inventory at
`GET /api/executions/inventory`. It supports `project`, `executionId`, `issueNumber`,
`outcome`, `olderThanDays`, `attention`, `origin` (`local` or `managed`), `limit`
(1–200) and `offset`. Results are newest-first and include the existing execution
read model plus a derived maintenance assessment (`status`, stable `reasonCode`,
explanation, last known progress time, authority and currently available actions).
The endpoint scans at most 5,000 matching history rows per request; `hasMore` also
signals when that scan bound was reached. The last progress timestamp is the
completion time when present, otherwise the start time; the history schema does
not record intermediate progress timestamps.

Example:

```sh
curl --fail "$worker_url/api/executions/inventory?project=sample&attention=reconciliation-required&limit=25"
```

Maintenance status is operational guidance derived from local history, not a new
authoritative lifecycle state. Business `outcome` remains the execution result.
`managed` authority means history has a Server execution or assignment identity;
it does not mean the Server is currently reachable or that an old lease is valid.
An unconfirmed managed report is marked `reconciliation-required`; uncertain
recovery, missing ownership and integration ambiguity stay retained for review.
Recovery and reporting take precedence over terminal outcomes and age. The mapping is explicit:

| Persisted evidence | Maintenance status / reason |
| --- | --- |
| Reporting failure (including after GitHub reconciliation) | `reconciliation-required` / `managed-report-unconfirmed` or `github-report-unconfirmed` |
| `github-reconciliation-required`, pending completion JSON | `reconciliation-required` / `github-report-unconfirmed`, `completion-pending` |
| Integration recovery claim | `retained-review` / `recovery-claimed` |
| `recoverable`, `cleanup-pending` | `recoverable` / `workspace-recoverable`, `cleanup-pending`, regardless of expiry or age |
| `uncertain`, integration conflict/unavailable/interrupted | `retained-review` / `recovery-ambiguous` |
| `missing`, `preparation-failed`, `managed-completion-quarantined` | `retained-review` / `workspace-missing`, `preparation-resource-proof-missing`, `managed-completion-quarantined` |
| Codex interrupted/resuming/exhausted/inspection-required | `retained-review` / `codex-recovery-pending` |
| Codex recovered/recovery-finished, integration-recovered, superseded | `retained-review` / `recovery-transfer-unverified`: consumption does not prove source cleanup or successful descendant lineage |
| `github-reconciled` | `retained-review` / `github-resource-proof-missing`: verifies Issue eligibility, not completion delivery or local resources |
| operator/expired/resumed-cleaned, discarded, cleaned-no-changes, completion-reconciled | `healthy-terminal` for Completed, otherwise `terminal-clean`, with `cleanup-recorded`; business outcome is unchanged |
| Unknown recovery vocabulary | `retained-review` / `recovery-state-unknown` |
| Nonterminal or incomplete history without recovery evidence | `healthy-active`, or `stale` after seven days |
| Terminal without receipts | Recent Completed: `healthy-terminal`; older Completed or other outcomes: `retained-review` / `terminal-resource-proof-missing` |

Missing project configuration takes precedence as `orphaned`. Age never proves cleanup eligibility.
Canonical `outcome` values are `succeeded`, `blocked`, `failed`, `infrastructure-failure`,
`cancelled`, `integration-conflict`, and `active`; inventory accepts case variants such as `Failed`.
Unknown outcome and attention filters return allowed values. `terminal-clean` distinguishes resource-clean
failures from successful business results; no successful later Issue attempt alone clears an ambiguous source.
`includeArchived=true` includes archived history, with the same assessment and filters.

Safe read-only examples (paging follows attention filtering):

```sh
curl --fail "$worker_url/api/executions/inventory?attention=recoverable&limit=25&offset=0"
curl --fail "$worker_url/api/executions/inventory?attention=retained-review&olderThanDays=7"
curl --fail "$worker_url/api/executions/inventory?outcome=failed&attention=terminal-clean&includeArchived=true"
```

The inventory advertises inspection only. The
existing cleanup inspection/apply path performs fresh repository and ownership
proof under its normal repository gate and drain safeguards. A missing configured
project is `orphaned`, not disposable.

The Server execution registry remains the authority for managed queue state,
assignment, lease expiry and reconciliation. Its `/api/v1/executions` listing
continues to report those authoritative states; local Worker inventory does not
substitute for Server reconciliation when disconnected or when a lease is absent.

```sh
./cw executions list
./cw executions show --issue 210
./cw executions show <execution-id>
./cw execution inspect <execution-id> --json
./cw execution cleanup --stale --limit 1 --json
./cw maintenance completed-branches --older-than 30 --limit 1 --json
```

History comes from `ExecutionHistoryStore`, including attempts older than the recent list. Issue numbers can occur in several projects: inspect project/repository grouping before choosing an exact execution. Inspect and recovery dry-run use the same Worker service, repository gate, current history and Git classifier. Inspection is a snapshot; apply repeats the proof.

Recovery classifications are `active`, `keep`, `review` and `safe`, with reason codes. `safe/already-clean` means no managed worktree, registration or feature branch remains. `safe/integrated` means ownership, clean workspace and retained commits were verified against the freshly observed origin base. Current recovery attempts, unmerged commits, uncommitted or ignored files, claimed recovery, uncertain outcomes and incomplete ownership proof remain preserved. Issue closure does not authorize cleanup. History is retained after deletion.

Completed archives need a unique successful historical execution, exact branch identity and tip, elapsed completion retention, no worktree using the branch, and reachability from the authoritative origin base. Local-only and origin-only branches use the same proof. Preview and apply choose eligible branches in ordinal branch order, default limit 20, maximum 100. Excess eligible branches are skipped. An origin deletion uses an expected-tip lease and local deletion checks the old tip atomically. A rejected or uncertain deletion stops further deletion in that request. Preview refreshes remote-tracking refs but does not delete branches or reset the checkout.

## Apply a bounded batch

Both apply operations require the running Worker to finish draining. Stopping the service makes these API commands unavailable. Drain stops new scheduling while allowing current work to finish; do not drain the Worker from inside an execution it must finish first.

```sh
# Run outside a Worker execution, against the intended local Worker.
worker_url=${CW_WORKER_API_URL:-http://127.0.0.1:5080}
curl --fail-with-body -X POST "$worker_url/api/worker/drain"
curl --fail "$worker_url/api/worker/drain"
curl --fail "$worker_url/api/status"
```

Wait until drain reports `drainComplete: true` and status reports `state: drained`. Repeat dry-run and inspect each selected resource's reasons, then explicitly apply a small batch:

```sh
./cw execution cleanup <execution-id> --apply --json
./cw maintenance completed-branches --older-than 30 --limit 1 --apply --json
./cw executions show <execution-id>
./cw execution inspect <execution-id> --json
./cw maintenance completed-branches --older-than 30 --limit 1 --json
```

Apply holds the maintenance reservation and repository gate through mutation; configuration replacement and drain cancellation are refused during maintenance. Recovery success records `operator-cleaned` without replacing the primary execution outcome or lineage. The branch result reports local and origin deletions separately. Apply exits nonzero for refused/failed recovery resources or completed branches requiring review; other eligible resources may already have been cleaned. Reinspect partial results before another apply. After inspection, resume scheduling explicitly:

```sh
curl --fail-with-body -X POST "$worker_url/api/worker/drain/cancel"
```

Never manually force-remove a workspace or delete an archive to bypass a refusal. Reconcile missing metadata, changed tips, active recovery, unavailable origin or unmerged work at their owning lifecycle boundary first.

## Issue 210 integration validation evidence

Local shell suites cover history/detail/lineage, inspection, cleanup selection, bounds, JSON output and failure diagnostics. Local-repository regression tests cover integrated and unmerged resources, uncertain/active attempts, ownership mismatches, ignored files, repeated cleanup, local-only/origin-only archives, origin tip races, drain reservations and deterministic bounded deletion. These tests do not use the development repository as a destructive fixture.

A development Worker status read during implementation reported `running`, one active execution and no drain request. Subsequent project and bounded recovery dry-run requests could not connect to the loopback API. No real resources were classified or removed, and no manual-review inventory was established. The real dry-run/apply campaign remains deployment validation: run this procedure outside the Worker execution after deploying the changes, record one bounded cleanup's results, and retain every review/keep case. Draining or mutating the live development repository from this task would conflict with the task's Worker-owned Git lifecycle and active execution.

.NET self-validation was unavailable: parallel MSBuild could not bind its named pipe in the sandbox, single-process build lacked restored assets, and single-process restore could not reach NuGet or find the required packages locally. The Worker's separately configured validation remains authoritative.

## Interrupted integration completion

The Worker saves a validated integration handoff before updating the local base and
pushing it. The handoff includes the full commit ID, base branch, execution identity,
Issue intent and completion policy, followed by separate remote/reporting checkpoints.
A push failure preserves this handoff and pauses the affected project. It does not
start another implementation attempt or replace the working label with a task-failure
label.

At reconciliation, the Worker fetches the configured authoritative base and requires
that the exact saved commit is in its ancestry. Remote HEAD may have advanced beyond
that commit. A missing commit, changed history, missing/corrupt provenance, changed
Issue title/body, conflicting lifecycle labels, reopened Issue or another execution
requires manual inspection. An Issue becoming ineligible for replay is not proof of
success. The Worker never retries a base push automatically: verify the original
mutation and, if appropriate, retry only that exact push before restarting. Retained
managed execution metadata is not a current Server lease; completion without proof
of original managed ownership requires manual reconciliation.

Once proven, reconciliation uses the normal success reporter to finish the done
label, success comment, Issue close, notification and owned resource cleanup. It
rechecks Issue intent/state before reporting mutations and uses an execution marker
in the success comment to recognize a lost response. A confirmed report that was
removed or altered requires inspection rather than recreation. Repeated restarts
inspect the existing durable effects and skip completed steps. No Codex invocation,
new claim or new implementation attempt is involved.

Telegram does not support delivery lookup or idempotency keys. Its success attempt
is recorded **before** sending, so a restart cannot duplicate that attempt; interruption
between recording and delivery can omit the notification. Existing nonfatal Telegram
failure behavior is retained. Cleanup verifies execution/worktree/branch ownership
and the exact remote commit again. If configured, a pending completed-branch archive
may publish only the proven commit to its saved Worker-owned archive name; an archive
with another commit is a conflict. No reconciliation operation force-pushes or resets
the base. History retains the original failure diagnostics and recovery lineage while
recording completed reconciliation.

### Historical execution archival

`POST /api/executions/cleanup` also accepts `action`: `inspect`, `cleanup`
(the default), `archive`, `reconcile`, or `purge`. Selection and the 1–100 batch
limit are unchanged. Preview with `apply: false`; apply requires a completed
Worker drain and holds the existing repository gate through durable recording.
Each item is re-read and inspected separately; refusals and failures are returned
per item, so a batch may partially complete. Retry after inspection rather than
assuming all items succeeded.

Archive is available only for standalone, configured executions with a confirmed
terminal outcome at least 30 days old, a healthy-terminal or terminal-clean classification, and
fresh `safe/already-clean` resource inspection. Clean resources separately first.
Active, ambiguous, pending recovery and inconsistent historical records remain
visible. Age alone never proves eligibility. Archival never removes Git resources
or changes recovery state. It atomically records an immutable receipt, preserved
across restart and repeated apply, and hides the execution from recent and default
inventory listings. `includeArchived=true` includes archived inventory entries;
detail, Issue lineage and recovery reads retain all records. Retrieve the receipt
at `GET /api/executions/{executionId}/archive-audit`.

All outcome, Git provenance, completion, GitHub reporting and ownership metadata
is retained indefinitely. Permanent purge is refused with
`purge-proof-retention-required`, even if `confirmPurge` is true: current contracts
cannot establish safe removal of incident and lineage evidence. `reconcile`
returns `recovery-protocol-required`; request recovery using the existing owned
recovery protocol. Managed cleanup and archive return `server-authority-required`,
including historical records containing only an ownership generation. Local
admin access, an expired lease, or a disconnected Server grants no authority;
managed apply uses the scoped Server protocol below.

## Managed maintenance through Server

The authenticated Server management API coordinates Worker-local maintenance.
It never opens VM files or deletes Git refs itself. Workers advertise the
`protocol/execution-maintenance-v1` capability; unavailable, offline or older
Workers receive an explicit refusal rather than granting authority from cached history.

`POST /api/v1/maintenance/executions` accepts an operator-generated `operationId`
(a GUID in `N` format), `workerId`, `action`, `apply` (default false), and a
`timeoutSeconds` deadline (5–300, default 60). Exact actions (`inspect`, `cleanup`,
`archive`, `retry-report`) also require `serverExecutionId`, `workerExecutionId`,
`assignmentId` and `generation`. Server, assignment and Worker IDs use GUID `N`
format; the local execution ID is a GUID. A reused operation ID must have the
same complete request. Conflicting requests and concurrent outstanding operations
on one Worker return 409. Authentication uses the existing Server management
authorization; Worker dispatch, confirmation and reports use that Worker's
revocable API credential.

`inventory` is read-only and omits exact execution identities. It supports
`project` (Worker project name), `issueNumber`, `outcome`, `origin` (`local` or
`managed`), `limit` (1–100) and `offset` (0–10000). It includes archived rows.
Use the existing Server execution listing for canonical filtered registry records,
including records whose Worker cannot currently provide observations.

`GET /api/v1/maintenance/executions?workerId=…&limit=…&offset=…` lists bounded
command audit. `GET /api/v1/maintenance/executions/{operationId}` combines the
retained command and bounded Worker observations with current Server records.
It distinguishes an offline Worker, orphan local records, stale ownership/leases,
pending reporting and potentially recoverable executions. Observations are
snapshots, not scheduling or recovery authority. Exact inspect reports the
Worker's existing Git cleanup assessment reason without deleting resources.
`POST /api/v1/maintenance/executions/{operationId}/cancel` cancels only commands
that have not been dispatched and retains their audit; running commands use their deadline.

For apply, first set the Server Worker scheduling policy to `Draining` and wait
for zero active assignments (completion-report retry can finish its own outstanding
assignment). The Worker must also have no local active execution
or concurrent maintenance. Its local maintenance reservation prevents new local
reservations, and cleanup/archival hold the existing repository gate. Inside that
gate the Worker checks exact local identities and asks the Server for fresh scope
confirmation. Cleanup and archive require a terminal Server result and released
lease of the matching generation, then repeat all existing Git provenance and
retention checks. Standalone API calls still cannot maintain managed executions.

`retry-report` previews or retries only delivery of a locally terminal result to
the existing Server completion endpoint. It requires matching ownership plus a
current active lease or the matching canonical terminal result. It never reruns
implementation, integration, Issue notification or GitHub mutation. Stale leases
require the existing Server reconciliation protocol. 404/409 reporting results
become durable, visible reconciliation dispositions; they are not silently retried.

Commands retain authorization/request correlation, deadline, terminal timestamps
and outcomes in the Server database. Worker receipts retain started operations
and terminal reports in local execution history. A lost report acknowledgement
resends only the report; a restart never replays a started mutation. Expired
dispatches appear as uncertain and retain the concurrency slot until the
quiescent Worker reconnects and records an interrupted disposition. Partial
failure or timeout requires fresh inspection before a new operation ID is used.
An out-of-date or disconnected Worker cannot apply commands; queued audit remains
visible until it reconnects. Archive receipts, execution lineage and provenance
remain retained; permanent purge and remote Issue maintenance are not supported.

## Installed standalone batch command

Use the installed Worker tool against its running local Management API:

```sh
sudo codex-worker maintenance clean-executions --limit 20
sudo codex-worker maintenance clean-executions --limit 20 --apply --confirm --json
```

Preview is the default. `--apply --confirm` is the explicit mutation opt-in, matching the installed
maintenance confirmation policy; no manual drain is required. `--config` selects the installed Worker
configuration and API address. This command is independent of the repo-local `cw`
client and refuses managed Worker configuration. Managed rows remain refused by
the cleanup service, including rows with only an ownership generation.

A pass visits at most 100 records (default 20), oldest first with execution ID as
the tie breaker. `--offset` selects a further page (0–10000). Apply reserves
maintenance before waiting for active executions to finish; work is never
interrupted. Concurrent maintenance and drain cancellation are excluded. The
prior Worker drain state is restored on exit, including timeout or cancellation.
The request has a five-minute deadline. Each item uses existing cleanup proof,
then attempts receipt-based archive with the existing 30-day retention policy.
Recent settled records remain visible with their archive eligibility reason.
Counts and exact IDs/reasons distinguish cleaned resources, archived history,
already archived records and remaining review. A review result exits nonzero;
earlier successful mutations remain durable and can be inspected before retry.
No execution rows are purged and no Issue mutations are performed.

Startup legacy reconstruction now records a separate durable review diagnostic
and emits one aggregate warning per project instead of a warning for every old
execution. Every startup still rechecks reconstruction, so changed workspace,
configuration, session or sibling evidence can enable valid recovery. The review
is neither acknowledgment nor reconciliation and grants no deletion authority.
Read the retained reason at `GET /api/executions/<id>/legacy-review` or with
`codex-worker executions show <id>`. Original outcomes and recovery metadata remain
intact; uncertain integrations and retained recoverable sessions require their
existing recovery protocols.
