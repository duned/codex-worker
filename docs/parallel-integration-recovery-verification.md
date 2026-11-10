# Parallel integration and recovery verification

`GitWorktreeTests.ParallelOverlappingImplementationsPreserveBothChangesThroughConflictRetry` is a deterministic reproduction using the test suite's temporary local bare remote and independent Git worktrees. It runs two implementation turns both with a shared inherited thread UUID and with fresh sessions. The tasks begin from the same base in different worktrees and update the same file. Task A integrates first; Task B's initial rebase conflicts and is safely aborted. Its preserved worktree is then recovered by the existing agent-resolution seam, validated against the combined result, and integrated. Assertions verify distinct worktree paths, a common starting commit, both task edits in the final base, one validation of the resolved work, and exactly three commits (initial base plus one commit per task).

Related coverage provides the surrounding lifecycle checks:

- `CodexThreadCoordinatorTests` covers same-thread serialization and completion capture, fresh/different-thread concurrency, failed turns, cancellation, and eligibility revalidation.
- `GitWorktreeTests.ExplicitResolutionEditsThePreservedWorktreeAndPreservesFailures` covers managed and standalone resolution, resolver failure, cancellation, and standalone intent changes.
- `GitWorktreeTests.ConcurrentExecutionsRetryRebasedValidationAndReleaseCapacity` exercises parallel scheduling and bounded post-rebase validation retry.
- `WorkerV011Tests` covers recovery claim/restart reconciliation and avoids automatic reattempts against an unchanged base.

These tests use fake implementation/validation seams and local Git only. They verify Worker isolation, gates, ownership and recovery behavior; they do not test live Codex model decisions, live GitHub assignment delivery, or external incident artifacts. A real overlapping edit is expected to conflict on first integration. Recovery proceeds only after the base has advanced and a recovery claim is authorized; failed resolution retains the worktree for inspection rather than looping on the same base.
