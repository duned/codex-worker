# Resolve retained execution records

Use the installed `codex-worker executions` command in the Worker service identity
(`sudo codex-worker` for packaged Linux installations). No journal editing is needed.
Listing and detail output omit prompts, process output, private paths and credentials.

```sh
sudo codex-worker executions list
sudo codex-worker executions show 12345678-1234-1234-1234-123456789abc
```

Each record shows execution ID, project/repository, Issue, durable outcome,
recovery state, validation and integration provenance, eligibility constraints and
any previous acknowledgment. Listing does not contact GitHub or establish remote
cleanup authority.

Stop the Worker before acknowledgment. The action acquires the same exclusive
checkout lock as the Worker and refreshes history before checking eligibility.
A live Worker or another administration process holding that lock causes rejection.
Managed executions are refused: reconcile their ownership and leases on the Server.
Local cached configuration cannot grant managed completion authority.

For a manually completed Issue after a verified remote integration:

1. Verify the Issue is closed with the project's configured done label. Remove
   stale ready, working, failed, blocked, integration-conflict or recovery labels
   only after resolving their underlying work.
2. Inspect the execution. Its stored exact validated integration commit and base
   must match the configured repository and authoritative remote branch. The
   command fetches that branch and verifies the exact commit is an ancestor.
   Missing/mismatched provenance is refused; an operator-supplied SHA cannot
   substitute for durable execution evidence.
3. Run a dry-run, then repeat with explicit confirmation:

```sh
sudo codex-worker executions acknowledge 12345678-1234-1234-1234-123456789abc
sudo codex-worker executions acknowledge 12345678-1234-1234-1234-123456789abc --confirm
```

Terminal legacy Completed/Failed/Blocked records without integration evidence
can be acknowledged using authoritative closed/done Issue proof. Interrupted,
uncertain or conflicted records require exact validated integration provenance.
Modern completion provenance is also checked for state-machine consistency.
Active executions, active sibling attempts, retained integration recovery claims,
and managed ownership are rejected with an explanation.

Acknowledgment records the time and verified reason atomically, preserving the
original outcome, execution identity, lineage and anti-duplicate provenance. It
marks recovery as `operator-acknowledged`; startup skips completion reconciliation
for that record. Restart the Worker after successful acknowledgment. No Codex,
Git integration, Issue reporting or notification is replayed. Ambiguous records
remain fail-closed.

Optional `--prune --confirm` removes only the retained original Issue prompt when
there is no Codex recovery session, recovery base snapshot or recovery status. History, commit/base provenance, completion
checkpoints, audit, worktrees and branches remain. Session evidence is never
pruned. This intentionally does not delete entire rows or recovery resources.
Repeated acknowledgment is idempotent: the first audit reason/time survives;
pruning may subsequently remove the eligible prompt. Every mutation rechecks
remote proof, including on repeated calls.
