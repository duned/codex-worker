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
Age can mark active work `stale` or retained terminal resources `recoverable`, but
never proves cleanup eligibility. The inventory advertises inspection only. The
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
