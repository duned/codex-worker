# Archived feature branch maintenance

Use the authenticated Server `POST /api/v1/maintenance/executions` command endpoint
with action `archived-branches`. The existing command polling, cancellation, deadline,
Worker authentication and audit lifecycle applies. Workers must advertise
`archived-feature-maintenance-v1`.

Supply a fresh `operationId`, the configured `workerId`, exact configured project
name in `project`, exact configured `repository`, `olderThanDays` (1–36500), and
`limit` (1–100). Execution/assignment identity fields are null. A preview has
`apply: false` and no `branches`. Poll the operation to read `report.branches`.
Each item includes its observed tip, correlated completion timestamp, decision and
retention reason. Only `eligible` items are age-based deletion candidates. Unknown,
ambiguous, changed or unintegrated history is retained. Inventory is bounded to the
`limit` archive refs in ordinal order starting at `offset` (0–10000). Preview subsequent
pages to obtain further eligible refs; inventory is not a repository count. Ref changes
between pages can change page membership, and every deletion rechecks eligibility.

To delete one or several previewed refs, submit a new command with `apply: true`
and `branches: [{ "branch": "done/feature/example-17", "expectedTip": "<preview tip>" }]`.
The selection must fit `limit`. Only `done/feature/` and `complete/feature/` refs
are accepted. The same age threshold and safety assessment runs again. The Worker
must be drained with no active executions; Server authority is checked inside the
repository gate. The operation never accepts executable names, shell text or Git
arguments. Per-ref results include retained/missing/changed refs and partial
remote/local deletion outcomes. An uncertain deletion stops further mutations.
