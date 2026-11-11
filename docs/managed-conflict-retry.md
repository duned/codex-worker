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

Intent inspection diagnostics
-----------------------------

Inspection returns a safe explanation and a `reasonCode` for a closed or missing
Issue, changed title/body/parent, missing original metadata, missing recovery
label, conflicting lifecycle label, or unavailable GitHub query. The dashboard
shows that explanation with the next step. These checks do not treat the expired
original lease as intent evidence: a successful request receives a fresh lease.
A provider failure is reported as `issue-query-unavailable`, without forwarding
provider output or stored Issue content.

History previously passed original Issue bodies through diagnostic redaction.
That removes carriage returns (including those in CRLF line endings) and replaces
secret-shaped text or configured secret values. Direct comparison with a fresh
GitHub body could therefore reject unchanged intent. Current capture stores a
SHA-256 body fingerprint before redaction; comparison canonicalizes CRLF to LF
but preserves other whitespace and content. Title and parent remain separate
comparisons. Execution and completion bookkeeping use the freshly verified Issue
payload, while engine prompts retain the recorded redacted body. Recovery attempts
retain the original fingerprint instead of hashing the redacted prompt as new evidence. SQLite and JSON preserve these values without truncation. The Worker
fetches parent metadata before creating initial assignment history; Server work
references do not supply original body or parent evidence.

After upgrading the original Worker, already-preserved conflicts affected only by
CRLF removal need no repair or database changes. Wait for its discovery poll,
refresh eligibility in the existing dashboard action, and request one resolution
attempt. Original history, worktree, commits and attempt lineage remain intact.
The new fingerprint is not backfilled from a fresh Issue: doing so would overwrite
the evidence needed to detect a genuine change.

Historical bodies containing redaction markers without a fingerprint cannot prove
unchanged intent, even if redacting today's body produces the same text. They
remain blocked as `issue-original-metadata-missing`. Missing historical parent
metadata likewise cannot authorize adopting a current parent; inspect the
`issue-parent-changed` diagnostic and retained history. Do not edit the database,
delete evidence, or remove a real parent merely to pass inspection. If original
intent cannot be proven, retain the original execution and use a separately
reviewed fresh Issue/request for new implementation through the normal scheduling
workflow. That is distinct from resolving this preserved conflict and does not
silently rerun implementation as recovery. The specific external incident's
mismatch requires its fresh diagnostic; the CRLF/redaction defects alone do not
establish which mismatch occurred there.
