# Managed historical completion reconciliation

A retained managed execution does not grant a Server lease. After restart the
Worker never uses it to replay Codex, integration, GitHub reporting, notifications
or cleanup. Completion reporting during the original leased execution retains
its existing ownership checks and durable checkpoints.

For a pending historical completion, the Worker validates its completion payload,
execution/commit identity, validation outcome and configured completion policy.
It then verifies the exact validated integration commit on the authoritative
remote base under the repository gate. If that proof succeeds, it records
`managed-completion-quarantined` in execution history, with a manual reconciliation
reason in `reporting_failure`. The original Server execution, assignment, lease
generation, commit, completion checkpoints and recovery resources remain intact.
Legacy records without completion checkpoints require a passed validation outcome and
compatible exact remote commit/base provenance, and also cannot authorize replay.
Independent managed assignments may proceed; assigning the quarantined Issue again
is rejected before claiming or implementation. Subsequent startup and assignment
preparation skip this quarantined completion. No GitHub effect is performed.
Missing, corrupt or incompatible provenance and an unverified integration still
pause the project. Repository gates and execution worktree ownership checks remain
in force.

Operators should inspect the original execution history and correlated managed
operational log, verify the original Issue and integration on the remote, and
reconcile the original execution through the Server's existing lifecycle and
supported administration tools. Quarantine is not a successful completion or
permission to replay it. There is no automatic lease reacquisition for historical
completion. Do not delete history or worktrees to bypass reconciliation, or retry
implementation that is already integrated. The existing verified manual completion
resolution requires authoritative terminal Issue state and validated remote
integration proof; it does not replay completion effects.

## Server result delivery

The authenticated execution report API returns 404 for an unknown execution and
409 for an ownership or lifecycle rejection. Neither response authorizes remote
mutation or proves the local outcome was accepted. Startup result delivery records
a durable manual reconciliation disposition for either response, preserving the
execution row. Successful delivery records `acknowledged`. These dispositions live
in the local SQLite `execution_server_report_dispositions` table, keyed by the
original local execution ID, and prevent repeated result delivery on restart.
Duplicate recording preserves the first disposition. Operational warnings include
the original Issue, local and Server execution, assignment and generation.
Transport errors, timeouts and 5xx responses leave delivery pending for a later
restart; they do not create a terminal disposition. Other rejected responses also
remain pending for investigation. Disposition of Server result delivery is separate
from completion quarantine and never grants a lease or GitHub authority.
