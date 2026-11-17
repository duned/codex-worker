# Parallel integration

Several Issues of one project can be implemented at once, each in its own worktree and branch and each in its own engine session. Integration into the configured base branch is serialized by the repository gate.

## What happens when two Issues finish

1. The first execution to reach integration rebases (if needed), validates and pushes to the base branch.
2. The next execution rebases its commit on the advanced base and validates the combined source again before pushing.
3. If the rebase applies cleanly, both changes are integrated. No engine is involved.
4. If the rebase conflicts, the execution's own engine is asked to resolve the conflict in its worktree (at most `validation.maxFixAttempts` times). There is no Issue intent verification for a first attempt; the managed lease is checked before and after. The resolved source then runs the authoritative post-rebase validation before it is pushed.
5. If the engine cannot resolve it (or the engine itself fails), the Worker aborts the rebase, preserves the implementation worktree and branch, records an `IntegrationConflict` outcome with integration result `rebase-conflict` and keeps scheduling other work.

## After a conflict

| Situation | Result |
| --- | --- |
| Standalone Worker, first attempt of the Issue, aborted rebase | The Issue returns to the ready label once. The next attempt implements it again on the current base; the conflicting work stays preserved for the retention period. |
| Any later conflict of the same Issue | The Issue keeps the integration-conflict label for review. |
| Post-rebase validation failure or any other integration failure | The Issue keeps the integration-conflict label; nothing is requeued. |
| Server-managed Worker, first Server attempt, aborted rebase | The Worker reports the conflict with integration result `rebase-conflict`. In the same transaction the Server marks the execution `RebaseConflictRequeued` and queues one fresh attempt (attempt 2, fresh workspace) on the current base, unless the Issue already has queued or running work. That retry does not need the ready label (the Issue carries the conflict label) and is assigned with metadata `rebaseConflictRetryOf`. The Worker's preserved-conflict recovery candidate for the original execution is then ignored without a rejection. |
| Server-managed Worker, any other conflict | The execution stays failed for review. Queue it again, or request an explicit recovery, from the Server. |

Applying the ready label to a conflicted Issue always starts a fresh attempt on the current base. Applying the integration-recovery label retries integration of the preserved commit without changing it. Engine-assisted resolution of a preserved conflict is available only on explicit request; see [standalone conflict recovery](standalone-conflict-resolution.md) and [managed conflict retry](managed-conflict-retry.md).

## Removed behaviour

- Codex session inheritance between dependent Issues (series 33) was removed. It added GitHub reads and a per-conversation gate before every implementation.
- Automatic engine conflict resolution during automatic standalone recovery was removed. First-attempt resolution was restored without live Issue intent verification: a resolution that fails only falls back to the rebase-conflict path above.

## Engine turn without a final response

When the engine process exits successfully but writes no (or an unparseable) final response, the turn ended early, typically a dropped stream while several executions run at once. Unless the process output names another cause, this is classified as `Codex incomplete turn`, which is recoverable: the preserved workspace is resumed through the bounded Codex recovery budget instead of stopping as an unknown infrastructure failure.
