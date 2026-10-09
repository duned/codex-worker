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

## Installed HTTP maintenance commands

`codex-worker maintenance --help` and `codex-server maintenance --help` describe
service-owned maintenance. These are separate from repository-local developer
`cw` and the offline `executions acknowledge` reconciliation command. Clients
never open execution history databases.

Set `CODEX_ADMIN_URL` to the owning service origin (HTTPS, or HTTP loopback).
For Server requests supply its management bearer credential using
`CODEX_ADMIN_TOKEN` in the process environment, never as an argument. Installed
Server helper execution reads these settings from its systemd environment file.
Protect that file as other service credentials.

Standalone examples:

```sh
codex-worker maintenance inventory my-project --limit 50 --json
codex-worker maintenance show <exact-execution-guid> --json
codex-worker maintenance inspect <exact-execution-guid> --json
codex-worker maintenance cleanup <exact-execution-guid> --json
codex-worker maintenance cleanup <exact-execution-guid> --apply --confirm --json
```

Drain the Worker and wait for drain completion before applying. Archive uses the
same exact-target syntax. There is no implicit purge, force, or bulk apply.

Managed commands use a JSON file containing the existing
`ExecutionMaintenanceRequest`: operationId, workerId, serverExecutionId,
workerExecutionId, assignmentId and generation. Inventory needs only operationId
and workerId; project, issueNumber, outcome, origin, limit and offset filter it.
IDs for Server operations are full 32-character GUIDs. The command selects action
and defaults apply to false regardless of file contents:

```sh
codex-server maintenance inventory inventory.json --limit 50 --json
codex-server maintenance inspect target.json --json
codex-server maintenance cleanup target.json --apply --confirm --json
codex-server maintenance show <operation-id> --json
```

Server acceptance queues dispatch; it does not establish completion. Inspect the
operation with `show` for reports, per-execution outcomes and canonical ownership.
Reuse the operation ID and identical request for safe repeat requests; use a new
operation ID when changing preview to apply. An offline Worker requires waiting
and re-inspection. Stale leases require Server reconciliation; authority refusals
must never be bypassed with local cleanup. Drain/reservation and Git provenance
checks remain authoritative in the owning services. `retry-report` is managed-only.

Output is JSON, including HTTP status and service response. Exit codes are 0 for
accepted/read success, 2 for invalid arguments, 3 for API/authority rejection,
4 for unavailable service or timeout, and 5 for refused/failed report results.
Always inspect asynchronous Server completion and individual results for partial
success before retrying.
