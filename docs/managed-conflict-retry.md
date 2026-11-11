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
remain blocked as `issue-original-metadata-missing`.

Legacy parent reconciliation
----------------------------

#464 introduced parent capture during claim preparation. Schema 20 introduced a nullable `parent_issue_number`. Both immediately before
and after #496, normal Worker claim preparation queried the native parent before
creating history; the Server assignment's work reference did not carry a parent.
#496 added body fingerprints and specific diagnostics, but did not add a parent
capture marker or backfill parent values. Thus a retained non-null number is
positive recorded evidence, while an old SQL NULL cannot distinguish an absent
parent from missing capture (including rows migrated from before schema 20).
The current schema records `ParentIssueCaptured` separately, including an explicit
null parent. Migration leaves legacy evidence unchanged. The reported external
execution's stored value/cause cannot be established from repository code or a
partial external timeline alone; this workflow inspects actual retained evidence
without requiring private artifacts or node database edits.

For an eligible managed conflict with unknown legacy parent metadata:

1. Upgrade the original Worker and Server. Keep the real parent relationship,
   original title/body, worktree and commits intact. Wait for the original Worker's
   discovery poll and open the original execution's recovery inspection.
2. If native history proves the parent, the preview explains that legacy capture
   is missing. Expand **Native GitHub parent history** under preserved evidence.
   Review repository/Issue identity, original execution time, and ordered event
   IDs, timestamps, parent numbers and add/remove actions. The Server additionally
   verifies that the same parent was established before its original assignment.
3. Choose **Request one resolution attempt** and confirm the evidence and exact
   preserved commit/base. This approval reconciles provenance for that linked
   request only. It never overwrites the source history. The same operation ID is
   idempotent, including after restart. An unchanged base is supported.
4. Follow the linked attempt. The Server retains the exact reviewed evidence in
   request metadata; the Worker retains it in the new execution history. The
   Worker reloads native history and compares the proof at claim, resolver,
   integration and reporting boundaries, alongside fresh lease/intent checks.
   Evidence changes require fresh inspection, not an automatic retry.

The Worker uses GitHub's native
[ParentIssueAddedEvent and ParentIssueRemovedEvent](https://docs.github.com/en/graphql/reference/issues#parentissueaddedevent)
through cursor pagination, including transfer detection. Inspection is bounded
by a 20-second deadline, ten pages of at most 100 relevant events, and a 256 KiB
proof. All pages must agree on repository, Issue node identity, creation time,
current parent and total event count. Replay must begin at creation with no
parent and contain a valid strictly ordered add/remove sequence. Duplicate IDs,
equal timestamps or an event within the assignment/execution boundary second,
inaccessible or foreign parents, transfers, broken/repeated
cursors, count mismatches and exhausted bounds are not proof. Any relationship
change after assignment blocks recovery, even if a parent is later restored.
An add at 16:46:19 before assignment at 16:47:10 is eligible when the complete
history and current relationship corroborate it; an add after assignment is not.
No-event history can prove an absent parent only when complete and consistent
with the current absent relationship. Native API history is the supported evidence
source; screenshots, comments and partial timeline exports are insufficient.

| Diagnostic | Meaning and operator action |
| --- | --- |
| `issue-parent-metadata-missing` | Legacy capture is unknown in standalone inspection. Use managed Server inspection for eligible managed conflicts; standalone historical attestation is unsupported. |
| `issue-parent-history-unavailable` | Original Worker cannot query native history (access, API support or deadline). Restore access/support and refresh; no parent value is inferred. |
| `issue-parent-history-incomplete` | History is incomplete, ambiguous, transferred, inconsistent, outside bounds, or cannot establish the parent before original assignment. Preserve work and investigate native history access. |
| `issue-original-parent-absent` | Complete history proves no parent at execution start, but a parent exists now. This is changed intent, not missing capture. |
| `issue-parent-changed` | Recorded evidence disagrees with the current parent, or complete history contains a later relationship change. Review actual intent; reconciliation cannot override it. |
| `issue-parent-proof-changed` | Fresh proof differs from the approved request. Preserve work and refresh inspection before another explicit request. |

When authoritative proof is unavailable, manual parent attestation is deliberately
unsupported: it cannot preserve the original-intent guarantee. Do not edit a
Worker database or remove a real parent to pass inspection. Keep the original
execution and use a separately reviewed fresh Issue/request through normal
scheduling if new implementation is appropriate. That is distinct from recovery
of the preserved implementation. Refusals leave its worktree and commits intact.
