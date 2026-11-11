# Codex interruption recovery

A CLI error stating “Selected model is at capacity” during implementation keeps the current execution, assignment and worktree active. The Worker waits five minutes before retrying the same invocation with the same selected model, checking managed ownership before each retry. Capacity waits do not consume the infrastructure resume budget or invalidate local execution readiness. Other available execution slots remain usable; the waiting execution retains its slot. There is no configured fallback policy, so the Worker continues cooling down and retrying the selected model (including at least three retries) until a turn completes or the execution is cancelled. It never silently selects an alternative model. History and managed execution status report the capacity condition; shutdown retains the workspace through normal interruption recovery.

The Worker remains online when a Codex execution loses a recognized external prerequisite. Usage/rate/subscription limits, authentication/session failures, service/network errors and execution timeouts park the implementation rather than restarting the Worker. Unknown process failures retain their workspace and sanitized diagnostics for inspection; an exit code alone does not authorize automatic recovery. Task outcomes, explicit cancellation, uncertain Git/GitHub mutations and integration recovery retain their existing semantics.

Healthy sibling executions continue. Requested Worker shutdown preserves a verified implementation/repair workspace as an interrupted continuation. A restart reconciles incomplete implementation history against its registered worktree before scheduling. It never invents missing legacy ownership, intent, profile or base metadata.

## Readiness and budgets

Unhealthy execution preflight is automatically rechecked after five minutes, backing off to ten, twenty and forty minutes on consecutive failures. Capability observations and explicit provisioning refresh can trigger an earlier check. Status exposes a sanitized reason and the next automatic preflight time. Authentication requires the supported node authentication flow; missing or broken CLI installation requires provisioning/repair. Preflight uses one bounded ephemeral invocation and performs no project work.

Each interrupted lineage permits at most three automatic Codex resumes. The count is consumed transactionally before launch and survives deployment/restart. An interruption also records a five-minute earliest resume time, so restarting cannot immediately repeat implementation invocations. Unhealthy readiness checks consume no implementation or validation repair budget. Validation repair lineage and consumed attempts are carried into continuation.

Context-window exhaustion differs from account quota exhaustion. Successful account preflight cannot make the oversized conversation resumable. A recognized context-window failure parks the verified workspace under the same cooldown and three-resume budget, then continues the original Issue in a fresh session with its original model/effort and existing files. The exhausted conversation is never resumed. Older `uncertain` context failures with an existing ownership snapshot can be reconstructed at startup only after history, configuration, workspace/base and process verification; their consumed resume count is retained. Missing evidence remains review-only with a specific diagnostic.

## Continuation and repository safety

Recovery is considered before new standalone Issues and runs within existing global/project capacity. It keeps original Issue intent, effective model/effort, workspace identity, session UUID when supplied, starting commit and configuration fingerprint. Configuration changes require inspection instead of silently substituting different execution settings. Environment secrets are never copied into the recovery snapshot.

The CLI's local help must establish support for explicit session resume with the Worker's existing schema, JSON events and approval/sandbox contract. The Worker never uses `--last`. When that contract or session is unavailable, it starts a new invocation on the same verified worktree, explicitly instructing Codex to inspect existing changes and continue the original Issue. It does not copy partial changes into a new implementation workspace or remove the source workspace. Other resume errors return through the interruption lifecycle rather than immediately replaying the invocation. A context-window failure therefore consumes the normal bounded recovery path before fresh-session continuation.

Completion follows ordinary authoritative validation, repair, rebase/revalidation and integration. The history validation checkpoint is written before each authoritative validation and after a successful repair. If shutdown interrupts validation after implementation or repair completed, both standalone and managed continuation rerun that validation on the verified preserved worktree, retain the consumed repair count, then perform the normal rebase validation and integration. They do not rerun implementation for that checkpoint. Managed continuation uses the fresh Server recovery assignment and lease before touching the worktree and rechecks authority before integration. Advancing the configured base while waiting does not overwrite newer work. Git history mutation, unfinished Git operations, missing registration, branch/HEAD mismatches or incomplete metadata stop automatic recovery safely.

Managed recovery is offered through the existing preserved-work assignment protocol with kind `Codex`. The Server verifies the original node/execution identity, outcome and competing reservations, then supplies a fresh assignment/lease. If a pre-integration lease expired and its retry is still queued, the returning owner may attach verified work to that queued retry. It cannot take back an assigned/completed execution. Expired preserved continuations wait for their original node; cached configuration and an expired lease confer no execution authority.

## Explicit readiness after an interruption

An explicit ready label asks the standalone Worker to re-evaluate an interrupted
Issue even when an older blocked or failed label remains. Closed or done Issues
remain excluded. The Worker still checks the original intent, process scope,
registered worktree, configuration, execution profile and remote integration state.
Verified validation checkpoints continue on the preserved workspace; a safe clean
retirement can instead permit a fresh attempt while retaining feature commits and
history. Dirty or uncertain work that cannot be verified remains protected with a
queryable recovery reason. Stale failure labels are removed only after continuation
is durably claimed. A ready label never resets the Codex resume budget or grants
managed lease authority.

For historical standalone `InfrastructureFailure` / `uncertain` records, explicit
ready can authorize verified implementation recovery even when the original CLI
cause was generic. The failure category remains unchanged. History must establish
an unfinished implementation invocation, and ownership, original intent, execution
profile, configuration, process and authoritative remote checks must agree. The
Worker uses the existing preserved-work Codex continuation and durable resume
budget, rather than treating the interruption as completed implementation awaiting
validation. A clean worktree at its original base with no feature commits may be
retired for a fresh ready attempt after the same safety checks. Missing evidence,
changed intent, active processes or exhausted budget retain resources and expose
the actual blocker; a generic cleanup `review` decision supplies no recovery proof.

A successful CLI process exit without a final task response does not establish
success. Recognized CLI error events still identify usage or context limits; absent
that evidence the cause remains unknown. The unknown cause alone cannot authorize
recovery; explicit ready and independent preserved-work verification are required. An operator report of “token exhaustion” alone cannot distinguish account
quota from model context exhaustion. Use the owning node's durable execution history
and CLI evidence to establish the cause; do not infer it from the Issue label.

## Operator evidence

Execution history retains interruption reason, lineage, changed-path summary, session UUID, configuration fingerprint and resume count. GitHub and Telegram receive the specific recognized cause and shared cooldown or exhausted-budget guidance, without arbitrary raw Codex output; the Issue does not need a new ready label for continuation. Status/journal report degraded readiness and restored execution readiness. Pending or exhausted interruption resources remain protected from automatic cleanup. GitHub recovery guidance includes the earliest resume time or states that the resume budget is exhausted; reapplying ready does not reset it. Workspace/configuration rejection reasons are retained in execution history for status inspection, and parked records are not re-reported on every scheduler poll.

Legacy `uncertain` interruptions can be reconstructed before scheduling when independent history, Git and session evidence agrees. Original Issue intent and effective profile must still exist in Worker history; conversation text cannot supply ownership or missing intent. Legacy post-repair validation is eligible only for a cancelled uncertain attempt with an implementation summary, a contiguous bounded repair journal whose last structured repair completed, no post-repair validation result, and no commit or integration evidence. The Worker pins this recovered state as `legacy: pending validation`, reruns the full authoritative validation on the preserved worktree, and carries the recorded repairs forward so it consumes only the remaining repair budget. A placeholder interrupted repair, failed/blocked terminal outcome, prior validation success, or ambiguous lifecycle remains review-only. The preserved worktree must be registered in the configured repository, have the execution's exact path and Issue/attempt branch, and have no unfinished Git operation. Its HEAD must agree with any recorded starting commit and be an ancestor of the current base. Dirty tracked files, deletions and untracked additions are retained unchanged.

The isolated `session_meta` v1 compatibility reader examines only bounded first records of `rollout-*.jsonl` in the service account's configured `CODEX_HOME/sessions/yyyy/MM/dd` directories during the recorded execution window (at most 31 days and 10,000 files). A root must have `source: exec`, `originator: codex_exec`, a UUID, exact workspace CWD, matching Git branch/commit, and a compatible timestamp. Repository URL metadata, when present, must also agree. Parent-bearing and subagent source records are excluded. Missing, ambiguous, partial, malformed, duplicate-key or unsupported metadata fails closed with fixed diagnostics; raw session contents and parser exceptions are never printed. Other Codex storage formats require an explicit adapter update.

Legacy executions lack a historical configuration fingerprint. Reconstruction verifies the persisted effective profile against original Issue metadata and current defaults, then pins the current continuation configuration as a new baseline; it does not claim to have recovered historical validation or environment settings. Current Worker validation remains mandatory. Once pinned, later configuration changes require inspection as for ordinary interruptions. Reconstruction is published once with an atomic history update that rejects competing/newer executions. The normal durable recovery claim, resume budget, readiness backoff and managed lease checks then apply, including after restart. Failed reconstruction changes neither history nor worktree resources. No incident Issue or execution ID is special-cased.

Real legacy evidence analysis is read-only. Actual resume, validation and integration are performed only by the owning Worker after it establishes fresh local or managed authority.

## Incident #432 operator inspection

Keep the incident worktree intact until its owning Worker has deployed this recovery behavior and reconciled the execution from its own durable history. Do not reset, clean, relabel, or manually run validation in that worktree. First capture a read-only snapshot of the Worker and Issue state:

```sh
./cw status
./cw recovery blocked --issue 432
./cw executions show dcbaa7e8-bc59-4dd5-ab83-8293086c8ee5
./cw log -n 300
```

Compare the execution ID, attempt, project/repository, engine/model, validation checkpoint, repair count and journal, recovery state, registered branch/base, and diagnostic with the service journal and Server execution detail when managed. On an upgraded Worker, inspect `./cw execution inspect dcbaa7e8-bc59-4dd5-ab83-8293086c8ee5 --json` and `./cw executions show dcbaa7e8-bc59-4dd5-ab83-8293086c8ee5` as read-only evidence; `review / recovery-ambiguous` is a cleanup decision and never authorizes resume. Confirm GitHub still has the original Issue open and inspect its labels without changing them. For managed mode, confirm Server's current assignment/lease and generation for the original execution; local history or a cached assignment is not authority. If history reports legacy-review, read `./cw executions show <execution-id>` for the retained diagnostic. A reason such as missing session metadata, ambiguous repair history, changed configuration, missing registered worktree, or a competing attempt means preserve the worktree and resolve that evidence with the owning Worker/Server operator. If reconstruction succeeds, the owning standalone Worker will show `legacy: pending validation` and resume the same execution through normal validation; managed continuation still requires a fresh Server assignment and current lease. Never reset, clean, relabel, or manually validate the retained worktree as part of diagnosis.

### Continuous standalone inspection

The former lifetime three-inspection budget left deferred executions untouched until another restart. Standalone recovery now runs in the normal host claim loop, admitting at most three read-only candidates per project per minute. Each inspected candidate normally has a durable one-hour cooldown, including across restarts. Historical uncertain infrastructure candidates receive one new bounded inspection per deployment; observing a newly added ready label also invalidates their local diagnostic cooldown. Issue eligibility is refreshed through a bounded five-minute cache, and the three-per-minute inspection limit remains in force. Uninspected and formerly budget-deferred candidates precede previously inspected candidates; newer attempts lead within that group. Last-inspected age then rotates blocked candidates fairly. Normal new-work claims still run after an inspection batch, using existing host slots and repository gates. Managed Workers do not enter this queue.

Authoritative Issue eligibility is read before interrupted-workspace process/Git inspection. Closed/done or otherwise ineligible Issues receive `issue-no-longer-eligible`; this prohibits continuation and preserves ambiguous local resources. It does not establish integration or deletion safety. Repeated polls during cooldown perform no per-record diagnostic writes. Infrastructure candidates may refresh the bounded Issue cache to detect explicit ready; other histories retain their durable cooldown before Issue reads. The startup aggregate is emitted once, rather than every poll.

After deployment, use `./cw recovery blocked --issue 432 --json` over successive polling cycles. `queued-for-review` (including old budget-deferred records) means inspection has not established a safety proof; it is not a verified blocker. Actual diagnostic records include `checkedAtUtc` and `nextEligibleAtUtc`. With 150 retained candidates, the initial campaign needs at most 50 one-minute batches (plus configured host polling and available execution capacity), without restart. A newer uninspected attempt leads older uninspected histories. Check the execution history and journal for continuation of the original identity and retained Repair 1/3 before interpreting validation/integration as complete. Tests do not establish that the live incident resumed.

Process diagnostics distinguish missing historical scope/join proof (`process-scope-missing`), changed service cgroup (`process-scope-changed`), unreadable potentially owned workspace links (`relevant-process-unreadable`), active workspace processes (`workspace-process-active`), and other unavailable proc evidence. Linux can report a protected cwd link as either an access error or an I/O error; both now use the same independent scope/ancestor/join checks. An unreadable relevant live process still blocks. PID disappearance alone never supplies missing historical scope/join evidence. No elevated service permissions are required.

## Completed Issue session history

Every observed Codex `thread.started.thread_id` is also saved separately from interruption recovery, preserving the exact event value. History associates it with the execution/attempt, project, repository, Issue, selected engine, final execution state and parent Issue relationship read from GitHub. A local ownership fingerprint binds it to the node, service account and Codex home. No conversation transcript or credentials are copied; Codex's existing session store remains the conversation source.

`ExecutionHistoryStore.FindInheritedCodexSessionAsync` exposes the latest authoritative attempt for a blocker Issue. It requires a completed Codex execution, the same project/repository and parent package, matching local ownership and a successful caller-supplied availability check against the Codex store. A newer failed, blocked, superseded or unfinished attempt prevents fallback to an older success. Legacy history without the separate thread ID has no inheritable session, even when interruption recovery has a session UUID. This lookup does not schedule children or grant lease authority; same-Issue interruption recovery and its resume limit remain unchanged.

For a new Codex execution, the Worker reads the child's native blocked-by relationships and verifies each blocker's current parent. It resumes a blocker thread only when every blocker belongs to the same repository and parent package and every latest authoritative attempt supplies the same exact locally available thread ID. Missing or incompatible history, different threads, independent Issues and Claude-selected Issues keep the fresh-session path, with a bounded skip diagnostic. Retry/recovery attempts retain their existing continuation policy.

The implementation request carries the child's own current Issue prompt, execution profile and isolated worktree. The invocation is `codex --cd CHILD_WORKTREE --approve-for-me exec resume … THREAD_ID PROMPT`: `--cd` explicitly overrides the source workspace, and the global approval flag precedes the resume subcommand. The [official CLI reference](https://learn.chatgpt.com/docs/cli/reference) documents explicit session IDs, follow-up prompts and working-root overrides. Local CLI help/parser checks verify these options; deterministic stub-process tests verify the exact arguments, prompt and process working directory without live Codex access. Unsupported structured resume or a session disappearing before invocation falls back to a fresh session with a diagnostic. The Worker never forks threads, imports transcripts or merges separate conversation histories.

### Child dependency examples and operational limits

A parent/child link groups Issues; it does not establish execution order or inherit
context. Use native blocked-by dependencies to express the order. For children of
one parent, `A → B → C` (each arrow means “blocks”) lets B resume A's exact thread
and C resume B's thread after successful completion. Independent roots start fresh.
Two children blocked by A both resume A's thread. A child blocked by both of them
can resume that thread only if their latest compatible successes still name the
same exact ID. Blockers with different IDs cause a fresh session: the MVP neither
forks nor merges Codex histories. Shared siblings see a mutable conversation,
including earlier sibling turns; they do not receive isolated snapshots of A.

Inheritance is limited to the owning Worker node, service account and configured
Codex home. The matching local session must still exist. Server history, a cached
assignment, repository files and another Worker's success cannot transfer a session
or authorize execution. History and session availability are checked again after
waiting for a thread. Restart uses durable history and local session metadata;
it does not reconstruct conversation text. Failed, blocked, cancelled or uncertain
turns on a shared thread prevent further inheritance, including after restart.
Every recorded turn must also belong to the same project, repository and parent
package and use Codex. An unfinished record without captured implementation
success cannot certify a shared thread, even when an older blocker completed.
An interrupted child follows its own verified same-Issue recovery policy and
resume budget, rather than selecting a blocker again.

The host serializes implementation turns that use the same thread, through durable
completion capture. A waiting child retains its execution slot, and waiting respects
cancellation and shutdown. Cancellation or timeout during a turn leaves that thread
ineligible; cancelling only a waiter does not invalidate the running turn. Different
threads and fresh sessions can execute concurrently within existing global/project
capacity and repository gates. Thread coordination does not serialize all Worker work.

A top-level parent final execution starts fresh even after every child completes:
it has no same-repository parent and there is no implicit “last child” selection.
Review/integration must use the parent's own prompt, repository state and ordinary
Worker validation. A nested parent is subject to the same explicit blocker rule as
any other child; its sub-Issues alone do not supply context.

Journal warnings identify the execution and a bounded reason: `Codex thread
inheritance skipped` for ineligible dependencies, `inherited Codex context is no
longer valid` after waiting, or `starting a fresh session` when structured resume
is unsupported or the CLI explicitly reports a missing session. Other resume
failures retain the normal infrastructure/recovery classification. A successful
fresh task is not evidence of inheritance: compare its observed
`thread.started.thread_id` in execution history with the blocker's exact ID and
check fallback diagnostics. Each invocation receives its own Issue prompt,
model/effort and current worktree, including when falling back. Only session IDs
and ownership metadata are recorded for inheritance; raw transcripts and credentials
are not copied into task prompts, execution history or reports. Child credential
filtering remains in force for resumed and fresh invocations.

### Deterministic regression coverage

These tests use local history/session fixtures, fake GitHub dependencies, explicit
coordination signals and stub Codex processes; no model access or Claude CLI is needed.

| Boundary | Coverage |
| --- | --- |
| Dependency graph and restart | `BlockerCodexSessionTests.DependencyGraphPreservesExactThreadAcrossChainSiblingsAndRestart`: chain, independent roots, siblings, same/different-thread joins and top-level parent final. |
| Compatibility and recovery selection | `OnlyCompatibleAuthoritativeBlockersSupplyAThread`: missing/failed/newer failed blocker, parent/repository/owner mismatch, unavailable local session, retry and interrupted recovery. |
| Child invocation and fallback identity | `InheritedThreadUsesChildPromptProfileAndExplicitWorktree`: two child prompts, explicit CWD, exact observed thread, unsupported/missing-session fallback and GitHub credential filtering. |
| Worker lifecycle | `WorkerV011Tests.WorkerFallbackRecordsOnlyTheFreshObservedSession`: ineligible blockers do not become inherited IDs in successful child history. |
| Shared-thread scheduling | `CodexThreadCoordinatorTests`: serialization through completion capture, unrelated sessions in parallel, revalidation, cancellation, shutdown, timeout and failed turns. |
| Durable uncertainty | `ExecutionHistoryStoreTests.SuccessfulThreadSurvivesRestartAndLatestAttemptIsAuthoritative`, `IneligibleLatestAttemptCannotSupplyInheritedSession` and `LaterTurnsOnBlockerThreadMustHaveCapturedSuccessfulCompletion`. |

Same-Issue interruption lifecycle and preserved-worktree validation remain covered
by `WorkerV011Tests.InterruptedCodexRecoveryRestoresStartVisibilityAndPreservesLineage`
and `StandaloneInterruptedExecutionTests`; child inheritance does not replace those paths.
