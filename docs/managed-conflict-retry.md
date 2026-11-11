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
| `issue-parent-history-environment` | Before process launch: service HOME/managed `GH_CONFIG_DIR` setup, ownership marker, permissions, linked paths or unrelated authentication failed validation. Reconcile locally under the original service account; do not relax ownership checks. |
| `issue-parent-history-process-start` | Service PATH, executable/interpreter permissions or current working directory prevented process launch. Repair the service context. |
| `issue-parent-history-authentication` | The process ran but authentication/repository authorization failed. Verify managed service-account repository access. |
| `issue-parent-history-process-failed` | The process exited unsuccessfully without a recognized authentication or GraphQL error. Inspect local service diagnostics and provider connectivity. |
| `issue-parent-history-timeout` | Process timeout or the 20-second complete-history deadline expired. Check service connectivity/provider availability. |
| `issue-parent-history-graphql` | The provider returned GraphQL errors, including errors with a nonzero CLI exit. Verify schema/API support and repository access using the production query/context. Raw provider messages are never copied to the preview. |
| `issue-parent-history-result-invalid` | The Worker could not parse the result contract. Check Worker/API schema compatibility and bounded-output truncation; raw response content is withheld. |
| `recovery-observation-missing` | No candidate has arrived through an accepted original-Worker assignment poll (or its latest poll omitted the candidate). Check readiness, managed project synchronization, local resources, free capacity and Worker-to-Server transport. |
| `recovery-observation-stale` | The received candidate expired or belongs to a different reported Worker build. Wait for a new accepted original-Worker poll and refresh. |
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

## Refresh boundaries and verification

The dashboard **Inspect / refresh** action reads
`GET /api/v1/executions/{id}/conflict-recovery`. It clears its prior preview and
checks the Server execution, project, original Worker and local execution IDs.
This read does not call the node or launch GraphQL. The original Worker's normal
managed capacity/assignment poll runs `DiscoverManagedIntegrationRecoveriesAsync`
→ preserved workspace/process inspection → `VerifyIntentAsync` →
`GetParentProvenanceAsync`. Discovery requires execution readiness, synchronized
managed configuration, an enabled project and free capacity. Drain/readiness or
full capacity can prevent a new observation even while the Worker heartbeats.

The production GitHub client resolves `gh` using the service PATH, uses the
service process working directory and prepares the shared node-managed GitHub
environment before starting `ProcessRunner`. The managed directory is derived
from the service account home and must pass ownership/path checks. Inherited
GitHub token variables are removed. A shell query with manually assigned HOME
and GH_CONFIG_DIR does not exercise these checks or prove the running service
has the same environment. Reproduce locally as the service account without
copying private environment values or raw process output into reports.

A successful query returns serialized native provenance on the candidate in
`WorkerAssignmentRequest.IntegrationRecoveries`. The Server validates the bounded
request contract, retains observations by authenticated Worker and Server
execution ID, and rechecks original assignment ordering when constructing the
preview. Failed/lost transport does not update that observation. The inspection
reason includes the original Worker ID, its reported build version at receipt,
and Server receipt time; `workerVersion` and `observedAtUtc` also appear in the
API response. Compare this identity/version with the dashboard Worker details
and capability/readiness inventory. These are reported build metadata, not
independent verification of a deployed binary. A newer reported build cannot
reuse the prior build's observation. Check that receipt time advances after a
Worker discovery poll before interpreting a refreshed provider refusal.

`IssueParentProvenanceTests.ManagedProcessUsesOwnedEnvironmentAndReportsSafeBoundary`
uses the production environment builder and process runner with a local CLI stub.
It accepts parent #32 added at 2026-11-10 16:46:19 UTC before the
16:47:10 assignment, with one event and no next page. It also exercises managed
ownership, launch, authentication, process, GraphQL, malformed result, pagination
and changed-history failures without publishing provider output. The Server
bounded-retry test consumes this process-produced proof through candidate JSON
serialization, verifies original assignment ordering and retained evidence, and
checks missing/stale/build-mismatched observations and the existing one-attempt
request/lease safeguards. Tests use no live GitHub service or incident database.
After deployment, wait for a new receipt, review native history in the existing
dashboard evidence, and use **Request one resolution attempt**. No Worker database
edit or GitHub parent removal is needed; all existing intent/workspace/lease and
retry-budget checks still apply. This repository validation does not establish
which build/environment is running on an operator's host.
