# Explicit standalone integration conflict resolution

Normal scheduling, retry/resume and integration-recovery labels never authorize an
implementation engine to resolve merge conflicts. A local operator can separately
authorize resolution of one exact retained standalone execution:

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

The action creates a durable recovery attempt linked to the selected execution,
adopts that same preserved workspace, and rebases through the ordinary Worker Git
lifecycle. Only confirmed rebase conflicts invoke the configured implementation
engine. Its input contains the original Issue intent and the precise Git conflict
context; it edits files only and does not restart implementation. Resolution calls
are bounded by `validation.maxFixAttempts` (which must be positive) and the existing
engine timeout. `git.autoMerge` must be enabled so the full integration and
validation lifecycle runs. Existing bounded validation repairs remain in effect.
If continuation reaches another conflicting commit, it preserves the partial
resolution and remaining rebase for inspection; it never skips that commit. The Worker
owns staging, rebase continuation, authoritative configured validation, verified
integration/push, completion reporting and cleanup. Fresh Issue intent checks fence
workspace adoption, resolution, integration authority and reporting. No force push,
reset, commit skipping or automatic conflict resolution is introduced.

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
restarting the Worker; normal startup reconciliation never authorizes a resolver.
