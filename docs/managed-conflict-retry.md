# Explicit managed conflict recovery

In the Server dashboard, filter **Executions** by project and Issue. Open the
original failed `IntegrationConflict` execution that owns the preserved worktree
(use **Previous attempt** when viewing a recovery attempt). Confirm the project,
Issue and original Worker in the execution details.

Under **Retry preserved integration conflict**, choose **Inspect / refresh
eligibility**. Review the refusal reason or expand the preserved execution evidence
for the exact Worker execution, implementation commit and integration base. Then
choose **Request one resolution attempt** and confirm. This permits one bounded
attempt even when automatic recovery has exhausted the unchanged base. It does not
reset automatic retry limits or rerun implementation.

The original Worker must be online, scheduling enabled, execution ready, and able
to advertise fresh recovery evidence during its capacity/discovery poll. The
project must be enabled with `git.autoMerge` and a positive validation repair
budget. The original assignment/lease identity, Issue title/body/parent and
recovery state must remain verifiable. The registered worktree, branch and exact
commit must match, with no active processes, competing execution, unfinished Git
operation or uncertain integration/reporting outcome. A changed preview requires
another inspection. A missing preview after a Server restart requires a fresh
Worker discovery poll; a cached snapshot grants no scheduling authority.

The Server queues a linked recovery request pinned to the original Worker and
issues a fresh assignment and lease through normal admission. The Worker rechecks
intent, workspace identity and current lease before using the existing bounded
resolver, authoritative validation, integration and reporting pipeline. Neither
ready labels nor the standalone `executions resolve-conflict` command authorize
managed recovery. No label edits are needed to request this action.

Follow **Inspect recovery attempt and result** to inspect state, validation,
integration and recovery evidence. If a response is lost, inspect again to find
the same operation before retrying. The administration API also supports
`GET /api/v1/executions/{original-id}/conflict-recovery` for inspection and `POST`
to that path with `{ operationId, workerId, candidate }`, using the exact candidate
returned by inspection. `operationId` is a caller-generated GUID in `N` format
and becomes the linked execution ID. Reuse the same ID and payload after a timeout;
its result survives restart and disconnect. Different requests cannot overlap an
active or uncertain attempt. Queued requests can be cancelled through the existing
execution cancellation action; assigned/running or lease-expired work follows
normal lease reconciliation, never automatic replay of an uncertain integration.

On refusal or failure, inspect the retained workspace and attempt history. Partial
resolver changes, unfinished rebases and uncertain results require reconciliation;
they are never discarded to make a retry eligible. A new request is a new explicit
authorization, not a retry loop.
