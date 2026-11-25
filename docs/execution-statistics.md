# Execution statistics

The Server computes weekly history per project from every finished execution request, archived ones included. It no longer depends on which executions a browser happens to have listed.

- **Dashboard:** project page → *Weekly history*, the last 8 weeks, newest first.
- **API:** `GET /api/v1/statistics?weeks=12&projectId=<id>` (management authorization). `weeks` is 1 to 52, default 12; without `projectId` every project is returned.

## Definitions

Weeks run Monday to Sunday in UTC. An execution belongs to the week it finished (a cancelled queued request finishes when it is cancelled). Per project and week:

| Field | Meaning |
| --- | --- |
| `finished` | Completed plus failed executions (cancelled ones are counted separately in `cancelled`). |
| `integrated` | Completed executions: merged into the base, or in [pull-request mode](pull-request-integration.md) with a pull request opened for review. |
| `firstAttempts`, `firstAttemptsIntegrated` | Executions that were an Issue's first attempt (not a retry), and how many of those completed: success without a retry. |
| `failureCauses` | The three most frequent causes among failed executions: `RebaseConflict` when the Worker reported a conflict with the base, otherwise the classification it reported (`Failed`, `Blocked`, `InfrastructureFailure`, `CodexInterruption`, `PreparationFailed`, …). |
| `queueMedianSeconds`, `queueP90Seconds` | Time from queued to assigned to a Worker (median and 90th percentile, nearest rank). |
| `durationMedianSeconds` | Median reported execution duration. |
| `costUsd` | Sum of the engine cost the Worker reported (Claude Code reports it; Codex reports tokens only). |

Weeks without finished executions are omitted from the API response; `weekStartsUtc` lists every week in the window so clients can show the gaps.
