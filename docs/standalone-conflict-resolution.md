# Standalone integration conflict recovery

The Worker can automatically resolve a rebase conflict while recovering a verified
standalone integration conflict. It uses the original execution's registered
workspace, branch and Issue intent, then continues through the existing validation,
integration, reporting and cleanup lifecycle. It never reruns the original
implementation. This path does not require Server enrollment.

Automatic resolution is eligible only for completed standalone conflicts with exact
preserved-workspace metadata, the original open Issue intent, no competing execution
or workspace process, an available original implementation engine, `git.autoMerge`
enabled and a positive `validation.maxFixAttempts`. The Worker verifies these
conditions before claiming recovery and rechecks Issue intent and integration
authority around resolution and integration. Managed recovery never uses this
resolver; it remains fenced by Server lease ownership.

Each durable recovery claim consumes the current integration-base fingerprint and
allows at most `validation.maxFixAttempts` resolver calls. A restart cannot repeat an
interrupted claim on the same base. The Worker may re-arm automatically only after
the authoritative integration base changes. An operator can also re-arm an eligible
conflict by applying the configured integration-recovery label. Unresolved or
structured incomplete results, failed validation, cancellation, changed intent and
uncertain Git state retain the workspace and execution lineage for inspection. The
Worker reports the reason and does not close the Issue. Use the local command below
only when automatic recovery is exhausted or an operator must explicitly authorize
another attempt:

```sh
codex-worker executions list --config /path/to/worker.yml
codex-worker executions show <execution-id> --config /path/to/worker.yml
codex-worker executions resolve-conflict <execution-id> --config /path/to/worker.yml
codex-worker executions resolve-conflict <execution-id> --confirm --config /path/to/worker.yml
```

Run as the Worker service identity, with its existing history database, GitHub and
implementation-engine authentication and original project configuration. No Server
enrollment or Management API is required. Quiesce the Worker that owns this checkout
before inspection or confirmation: the command takes the same exclusive repository
lock and refuses while that Worker owns it. It does not drain or acquire locks for
other repositories; separate Workers can continue unrelated work. Do not run two
Workers against the same checkout. Stop the owning service gracefully; do not kill
active executions to free its lock.

The command without `--confirm` verifies eligibility and reports inspection only.
It runs execution readiness and remote read/authentication checks but creates no
execution claim, invokes no conflict resolver, and changes no Issue labels or
workspace files. Inspection is not transferable authorization: confirmation repeats
the checks under the checkout lock. Use the original workspace-owning execution ID,
not a retry alias. Configuration paths select projects, never arbitrary workspaces
or shell commands.

Confirmation requires a completed `IntegrationConflict` with explicit
`integration-conflict` recovery state, an exact preserved commit, original Issue
body/title, branch and registered worktree belonging to this repository. Managed
identities, missing/legacy or stale metadata, changed branch/HEAD/base configuration,
dirty or linked workspaces, unfinished Git operations, surviving workspace
processes, pending reporting/completion, existing recovery claims, competing work,
and later or ambiguous attempts are refused. Settled failed integration retries
linked directly to this original workspace are permitted; they do not select a
new implementation or grant conflict-resolution permission. Restore/reconcile evidence with the
owning operator; the command never guesses ownership or discards work to proceed.
Linux process inspection must be available. The Issue must still be open with its
original intent and parent, without ready/done intent and with a configured
integration-conflict, integration-recovery or working label. Unknown execution IDs
are refused; the command never selects the latest attempt on the operator's behalf.

The explicit action creates a durable recovery attempt linked to the selected
execution and adopts that same preserved workspace. Only confirmed rebase conflicts
invoke the configured implementation engine. Its input contains the original Issue
intent and precise Git conflict context; it edits files only and does not restart
implementation. Calls use the same `validation.maxFixAttempts` bound and engine
timeout as automatic recovery. If continuation reaches another conflicting commit,
partial resolution and the remaining rebase stay available for inspection; the Worker
never skips that commit. The Worker owns staging, rebase continuation, authoritative
configured validation, verified integration/push, completion reporting and cleanup.
No force push, reset or commit skipping is performed.

A failed, cancelled or changed-intent resolution preserves the original commits,
workspace and attempt diagnostics. Cancellation or uncertain state also retains the
recovery claim for reconciliation. A structured incomplete resolution records an
integration-conflict outcome without discarding partial edits. The rebase may remain in progress
so partial resolution edits survive; another invocation refuses that uncertain state.
Do not reset, clean, skip commits or delete the workspace to silence the refusal.
Inspect and reconcile its ownership and Git state first. Validation failures retain
the existing integration-conflict snapshot and validation diagnostics; uncertain
integration or reporting remains in the existing completion/reconciliation protocol.
No failure marks the Issue done.

After the command, inspect both the original execution and its linked attempt:

```sh
codex-worker executions list --config /path/to/worker.yml
codex-worker executions show <original-execution-id> --config /path/to/worker.yml
codex-worker executions show <recovery-attempt-id> --config /path/to/worker.yml
```

List output includes execution IDs and outcomes; show output includes recovery,
validation and commit state. On success the original is `integration-recovered`,
the linked attempt is `Completed`, and ordinary completion reporting has run.
On interruption, inspect the service-account history and preserved worktree before
restarting the Worker. Startup reconciliation releases a claim only after it verifies
the interrupted attempt and workspace; the next scheduler claim must pass the full
standalone eligibility checks and the unchanged-base fingerprint still prevents
repeating that attempt.
