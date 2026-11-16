# Parallel integration

Several Issues of one project can be implemented at once, each in its own worktree and branch and each in its own engine session. Integration into the configured base branch is serialized by the repository gate.

## What happens when two Issues finish

1. The first execution to reach integration rebases (if needed), validates and pushes to the base branch.
2. The next execution rebases its commit on the advanced base and validates the combined source again before pushing.
3. If the rebase applies cleanly, both changes are integrated. No engine is involved.
4. If the rebase conflicts, the Worker aborts it, preserves the implementation worktree and branch, records an `IntegrationConflict` outcome and keeps scheduling other work. The Worker does not stop and the engine is not asked to resolve the conflict.

## After a conflict

| Situation | Result |
| --- | --- |
| Standalone Worker, first attempt of the Issue, aborted rebase | The Issue returns to the ready label once. The next attempt implements it again on the current base; the conflicting work stays preserved for the retention period. |
| Any later conflict of the same Issue | The Issue keeps the integration-conflict label for review. |
| Post-rebase validation failure or any other integration failure | The Issue keeps the integration-conflict label; nothing is requeued. |
| Server-managed Worker | The Issue keeps the integration-conflict label and the Server execution is reported as a conflict. Queue it again, or request an explicit recovery, from the Server. |

Applying the ready label to a conflicted Issue always starts a fresh attempt on the current base. Applying the integration-recovery label retries integration of the preserved commit without changing it. Engine-assisted resolution of a preserved conflict is available only on explicit request; see [standalone conflict recovery](standalone-conflict-resolution.md) and [managed conflict retry](managed-conflict-retry.md).

## Removed behaviour

- Codex session inheritance between dependent Issues (series 33) was removed. It added GitHub reads and a per-conversation gate before every implementation.
- Automatic engine conflict resolution during first-attempt integration and during automatic standalone recovery was removed. It made an ordinary overlap between two Issues depend on live Issue verification, lease checks and a resolver budget.
