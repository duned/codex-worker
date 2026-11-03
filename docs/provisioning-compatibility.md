# Provisioning compatibility and restore

Both provisioning protocols remain supported. No released-client inventory or migration proof establishes that either can be removed. Product version alone is not protocol negotiation; Worker registration contract versions 1/2 do not establish typed provisioning support. Do not redirect legacy endpoints to typed commands or convert persisted rows implicitly.

## Callers and durable formats

| Boundary | Current callers | Persisted format |
| --- | --- | --- |
| Legacy `/api/v1/provisioning` create/list/get/state and `/api/v1/workers/{workerId}/provisioning/request`, `/{planId}/report` | `WorkerHost` startup and managed polling through `WorkerRegistrationClient`, `ProvisioningPlanExecutor`; management API clients; dashboard `WorkerAdvanced` reads history | `provisioning_plans`: string state, JSON action list, current action, timestamps, result/failure; Server `ProvisioningPlan` and Worker `ProvisioningPlanContract` remain distinct wire counterparts |
| Typed `/api/v1/provisioning/commands` and Worker `/provisioning/commands/request`, `/{id}/report` | Managed Worker command polling/executor, dashboard node and Server preparation controls, MCP provisioning tools, Server local background service and provisioning administration CLI | Optional `provisioning_commands`: identity/node/capability/active columns and JSON `ProvisioningCommand` from Shared; partial unique active node/capability index |

Local standalone provisioning CLI execution is node-owned, not a third Server dispatch queue. Both managed protocols still require Worker authentication and node-local action/elevation permission. Server-local typed commands use node `server`. Neither protocol grants arbitrary shell execution.

Backup archive format 1 uses the current registry schema, retains legacy plans and optionally typed history, and rejects unsupported schema/typed lifecycle data. Archives without the typed table remain supported. No schema or protocol version changes are required by this policy.

## Restart and restore transitions

Ordinary restart uses the current durable database. Offline restore uses an older snapshot that cannot prove whether a pending operation was dispatched after export. Restore therefore cancels all snapshot-pending provisioning work in the extracted database before atomic replacement, including Server-local commands. Export and read-only archive validation do not cancel live work. Older valid archives receive the same restore policy.

| Snapshot state | Ordinary restart | Offline restore |
| --- | --- | --- |
| Legacy Pending / typed Pending | Eligible for first dispatch; typed claim records Running before delivery | Cancelled with completion timestamp; never dispatch; operator must inspect actual node state before requesting new work |
| Legacy Accepted or Running | Retained; never reissued | Retained; never reissued |
| Typed Running, deadline not passed | Retained and capability remains locked; accept owned reports, never replay | Same, with login progress removed by backup scrubbing |
| Typed Running, deadline passed (uncertain) | Retain until reconciliation | Same; restore itself is not evidence of node quiescence |
| Legacy Completed / typed Succeeded | Retained history, no dispatch | Same |
| Failed, typed TimedOut, Cancelled, legacy Rejected | Retained terminal history, no dispatch | Same |

Legacy has no distinct TimedOut state or deadline/reconcile endpoint. An accepted plan may have reached the Worker even if no Running report arrived. Such a plan remains inspectable and can receive an owned final Worker report, but is never automatically retried. If the original executor is lost, inspect the node and establish quiescence before explicitly creating replacement work; the old plan remains historical uncertainty. This intentionally replaces the former legacy assumption that every action was safe to replay. Older clients continue using the same contracts, but polling no longer redelivers accepted/running plans.

Typed uncertainty is represented by Running, not a separate enum value. Existing explicit reconciliation requires a past deadline and administrator confirmation that the node is quiescent; it records Failed/Interrupted and releases the capability. A node requesting its next command after deadline also closes one expired operation as Failed/Interrupted, relying on the executor contract that it has returned and terminated its child tree before polling again. A reported TimedOut is terminal, distinct from an unacknowledged expired Running operation. Never turn either into an automatic replay. Typed terminal reports remain idempotent only for matching outcomes; contradictory reports are rejected.

At-most-once refers to dispatch from the current durable database, not a guarantee that node effects did or did not occur. Lost responses preserve uncertainty. Restoring a snapshot cannot roll back node effects. Restore also revokes Worker API authentication and scrubs credential delivery and device-login material; re-enrollment is not permission to replay old operations.

## Retirement gate and focused regression checks

Before retirement, enumerate deployed supported clients (including external management clients), prove endpoint/action/report equivalence and authority semantics, and test migration of every persisted state, optional-table archives, lost dispatch/report responses and mixed-client restarts. Until then preserve both contracts and stores rather than sharing superficially similar lifecycle code.

Focused local command:

```sh
dotnet test tests/CodexWorker.Tests/CodexWorker.Tests.csproj --filter 'FullyQualifiedName~ProvisioningPlansPersistLifecycle|FullyQualifiedName~RestoreRetainsDispatchedProvisioning|FullyQualifiedName~Backup|FullyQualifiedName~ProvisioningCommandTests'
```

Coverage includes retained accepted/running legacy plans, typed running/succeeded/failed/timed-out restore, cancelled restored pending plans/commands, durable typed claims, deadline reconciliation and capability locks. Worker-configured validation remains authoritative.

Local validation for this change: `git diff --check` passed. The focused test command with `--no-restore` could not run because `project.assets.json` was absent (NETSDK1004). Retrying with restore, then `dotnet restore tests/CodexWorker.Tests/CodexWorker.Tests.csproj --disable-parallel --verbosity normal`, exited 1 during project discovery with zero reported errors/warnings and no actionable diagnostic. Tests remain unexecuted locally; no authoritative Worker validation result is claimed.
