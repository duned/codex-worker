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
it acknowledges readiness without dispatching another restart. If restart itself
failed, reconcile the healthy original instance after inspection before retrying.

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

This is separate from reconciling a healthy Worker or recovering its service.
The Worker observation is unchanged: an unavailable/draining lifecycle remains
unavailable for assignment. Only a subsequent fresh running heartbeat and normal
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
