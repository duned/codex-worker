# Restarting a Worker

On an installed Linux node, run `sudo codex-worker restart` (or add `--json`
for a versioned result). `--config <path>` selects the running Worker's configuration.
The local loopback management API must be enabled. Its instance ID must match
the installed service's systemd invocation ID before any restart is dispatched. The command drains the Worker,
waits up to 24 hours for active executions to finish, restarts `codex-worker.service`
through systemd, then waits up to two minutes for a different process instance to
report normal execution readiness. Exit codes follow the other Worker commands:
0 on verified success, 2 on failure, 130 on operator cancellation. Concurrent local
requests are rejected while one command owns the restart lock. Cancellation and
drain timeout leave the drain in place; inspect status before cancelling the drain.
Once service dispatch is prepared, the local drain cannot be cancelled until the
service restarts, preventing execution admission during the service-manager call.
If that call fails, inspect the journal and restart the drained service locally.

On the Server **Worker detail** page, use **Restart worker**, then **Refresh restart
status** to follow the pending operation and its outcome. The Worker must be online
and advertise `worker-restart-v1`. Enable `worker.provisioning.enabled` locally and
add `worker:restart` to `worker.provisioning.allowedPrivilegedActions` to authorize
remote restart. A matching denied action wins. Refresh the packaged installation
locally if its sudo policy predates restart support. This permission authorizes only
`systemctl --no-block restart codex-worker.service`; callers cannot supply commands.

Dashboard restart shares the persistent self-update operation and admission fence:
requests use the authenticated, CSRF-protected `/api/v1/workers/{id}/update` path with
`{ "operationId": "<32-character GUID>", "restart": true }`. Reads return `restart`
and the existing operation states. Repeated IDs return their retained outcome;
requests while an operation is pending or failed return that operation. Restart and
update cannot run concurrently through this path. Offline Workers reject new requests.
The Server drains scheduling and waits for active assignments and leases, then the
Worker checks its local drain gate before asking for one-time start authorization.
No active execution is terminated by the restart action.

Success requires a fresh heartbeat from a different process instance, synchronized
configuration, execution readiness and the previous capabilities. The previous
Server scheduling policy is restored only if the operation still owns the drain.
An operator's policy change revokes that restoration authority. No execution history,
identity, ownership records or recovery worktrees are removed.

Pending drain expires after 24 hours; dashboard reconnection expires after 20 minutes.
Failed or uncertain requests are never automatically replayed. Scheduling remains
paused and the operation retains a diagnostic. Inspect `systemctl status codex-worker`
and `journalctl -u codex-worker` on the node. After checking that service-manager
operations are quiescent and the Worker is healthy, use the existing
[explicit operation reconciliation](worker-dashboard-updates.md#recovery) endpoint;
it acknowledges readiness without dispatching another restart. Reconciliation requires a replacement instance reporting the expected version;
the original instance cannot prove a successful restart. Use drain release below
to retain the failure and recover a failed dispatch.

### Abandon a failed restart's Server drain

Worker detail offers **Restore Server scheduling policy** for a failed or timed-out
restart. Confirm that the node is quiescent and no restart is pending. This action
requires no VM command, installation or service restart. The Server rechecks, in the
assignment admission transaction, a fresh post-operation heartbeat reporting zero
executions and zero active Server assignments and leases. The checklist identifies
each blocker. Missing/stale observations cannot establish quiescence.

The authenticated, CSRF-protected endpoint is
`POST /api/v1/workers/{id}/update/release-drain?nodeQuiescent=true`, with
`{ "operationId": "<failed operation ID>" }`. It releases only that operation's
scheduling fence and restores its prior policy only while it still owns the drain.
An operator's subsequent policy change is respected. Repeated requests for the same
released operation return its retained result. A different current operation rejects
the request. The failed state, diagnostic and release timestamp remain inspectable;
late acknowledgements cannot change it to success. No completion effects are replayed.

The Worker consumes this acknowledgement only for its own failed restart operation
and releases its local maintenance fence into not-ready. The Server does not change
the Worker observation or mark the restart successful. Only a subsequent fresh running heartbeat and normal
execution readiness checks permit assignment. Other eligible Workers remain usable.

### Failure path corrected

Code tracing found that the managed restart handler previously called `DrainWorker`
before checking the node's provisioning authorization. Disabled provisioning, a
missing `worker:restart` allow rule, or a deny rule then recorded `restart-failed`
on the live Worker despite never dispatching systemd. That local lifecycle suppresses
readiness updates while drained; Server reconciliation requires a running lifecycle,
so fresh idle heartbeats alone could not clear the failed operation. Version equality
is expected for a restart and is not evidence of service replacement or its failure.
The handler now checks authorization before mutating the local drain/lifecycle.
An actual dispatch failure still retains the local fence for inspection. Without
node diagnostics, this code defect does not establish which failure happened on a
particular deployed node; the new Server-only release works safely for either case.

### Managed handoff and recovery reliability

An acknowledged systemd restart can cancel the old process while its service-manager
command is returning. Requested shutdown now preserves the handoff instead of
reporting restart failure. Success requires a fresh idle replacement instance, the
expected version, synchronized configuration, execution readiness and retained
capabilities before restoring the operation-owned scheduling policy. A failed
dispatch stays failed after drain release; local admission opens only after the
host rechecks readiness. A new restart may then be requested with a new operation ID.

Journal reads use a fixed, installer-allowlisted privileged command scoped to
`codex-worker.service`, fetching at most 200 lines and returning the latest 100 by
default. Output is bounded and redacted; permission denial has a distinct dashboard
message. Existing installations need the updated installer privilege policy for
this journal access. No arbitrary journal unit or command is accepted.
