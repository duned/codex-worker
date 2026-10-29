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
