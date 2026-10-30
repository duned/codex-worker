# Dashboard Worker updates

For a restart without installing a release, see [Worker restart](worker-restart.md).

The Worker editor's **Update worker** action sends an authenticated, CSRF-protected
management request to the Server. It never copies or executes a browser shell
command. Update authority is separate from configuration saving and Worker reads.

Packaged Linux Workers advertise `worker-self-update-v1` when the fixed installer
helper is present. Enable `worker.provisioning.enabled` and allow
`worker:self-update` in the node-local `allowedPrivilegedActions` list to permit
updates. A matching denied action always wins. Older installations need a local
packaged upgrade to install the helper before remote updates become available.
No caller-supplied version, package, executable or URL is accepted.

The Server durably records the request, owns temporary scheduling drain, and
blocks admission until the request succeeds. Active assignments and leases are
allowed to finish; neither expiry nor a zero Worker heartbeat alone proves idle.
Duplicate operation IDs retain their outcomes; a different ID while an update is
pending or failed returns that existing operation. Operator policy changes revoke
the update's authority to restore the previous policy, even when the operator
selects the same policy again.

Once idle, the Worker discovers the latest stable release through the shared
packaged updater, obtains a one-time Server start authorization, and invokes a
fixed root-owned helper through noninteractive sudo. The helper starts the existing
`CodexWorker update --yes --json` command in an independent bounded systemd service.
The installer retains ownership of configuration, identity, credentials, history,
installation rollback and service replacement. The helper accepts no arguments and
receives no service secrets. Node-local policy and sudo authorization both apply.

The Worker checks the independent updater's terminal systemd result before
reporting installer verification. The Server also requires a fresh heartbeat at the
expected version, synchronized configuration, normal execution readiness and no
regression in previously advertised capabilities. Only then can the temporary drain
restore its prior policy. Previously disabled or draining Workers remain so.
A newer release published between discovery and installation may produce a version
mismatch; this remains blocked for inspection rather than silently returning to service.

Requests waiting for idle expire after 24 hours; started operations have a 20-minute
reconnect deadline. Failures, interrupted starts, lost responses and uncertain
outcomes never cause an automatic installation replay. Server restart retains the
operation and drain ownership. A node reboot that loses the updater service result
requires operator reconciliation.

## Recovery

Inspect `systemctl status codex-worker codex-worker-self-update` and
`journalctl -u codex-worker-self-update`. Repair the installation with the documented
local packaged update workflow if needed. Keep work and recovery resources intact.
Verify the updater is quiescent, the Worker has reconnected at the expected version
(or original version if release discovery failed), and startup checks pass.
Then an authenticated management client may POST
`/api/v1/workers/{workerId}/update/reconcile?nodeQuiescent=true`.
This explicit acknowledgement checks current Server readiness and assignment/lease
counts; it does not execute an installer. Failure diagnostics remain in the retained
operation. Concurrent policy changes still prevent automatic restoration. Merely
changing scheduling policy cannot bypass an unresolved update's admission fence.

The Worker detail page identifies this admission fence as **Paused by operation**,
separately from the configured scheduling policy and connection status. Its recovery
panel shows restart/update type, expected and original versions, the version from
the latest heartbeat (including explicit unavailable values), timeout uncertainty,
and a timestamped readiness checklist. **Refresh recovery checks** reads
`GET /api/v1/workers/{workerId}/update/recovery` without advancing the operation,
changing policy, or recording a timeout. Each unmet prerequisite includes a stable
code and a sanitized next step. Unsafe reconciliation returns HTTP 409 with
`reason` and the same current `recovery` checklist.

After inspecting the node service and journal, confirm that no restart/updater
process remains active, then select **Reconcile and restore scheduling**. The Server
rechecks readiness under the assignment admission gate. Fresh post-operation
heartbeat, running lifecycle, synchronized configuration, expected version,
required capabilities, and no active assignments, leases or executions are required.
Successful reconciliation releases the operation fence and restores only a temporary
drain still owned by the operation; intentional disabled/draining policies remain.
Queued work can then be assigned under the normal eligibility and capacity rules.

For a failed update reporting an older version than expected, **Retry managed update**
is available when all other recovery checks pass and the operator confirms quiescence.
It creates one idempotent managed update attempt linked to the failed attempt,
retaining the expected release, original capabilities and prior scheduling policy.
Late reports for the failed attempt cannot change the retry. Scheduling stays fenced
through installation and readiness verification. The existing updater installs the
latest stable release: retry start authorization rejects a different discovered
version. If the expected release is no longer latest, or the running version is newer,
use the [versioned Linux Worker installer](linux-installation.md) for the expected
release after confirming quiescence, then refresh and reconcile. A release changing
between discovery and installation still requires inspection; an unexpected version
never clears the fence automatically.

For a failed restart, explicit reconciliation may accept a different running stable
version. The fresh post-operation heartbeat must report a supported protocol,
running lifecycle, synchronized configuration, all original required capabilities
and no active executions; Server assignments and leases must also be zero. This
records the accepted running version in the reconciliation diagnostic while retaining
the original expected version. It sends no restart/update command and replays no work.
