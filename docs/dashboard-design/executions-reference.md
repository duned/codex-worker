# Executions visual references

Open [executions-reference.html](executions-reference.html) directly in a browser. It is a self-contained, responsive mockup in the established React + Untitled UI dashboard direction. The page includes the list, detail, loading, empty, read-error, recoverable-failure and expired-uncertain examples. These are design artifacts; production screens are unchanged.

## Field and state mapping

| Reference content | API field / contract | Display rule |
| --- | --- | --- |
| Project | `projectId`, resolved from project summary | Show project name when available; otherwise the ID. |
| Issue | `workReference.type`, `.id`, optional `.url` | Use the authorized reference and link only when supported. The illustrated title is sample data. |
| Worker | `assignedWorkerId`, resolved from worker observation | Show the display name when available; otherwise the ID or “Worker unassigned.” |
| Execution identity | `id` | Shown on detail; compact in the list. |
| State and current stage | `state`, optional `currentStage` | Active state and reported current stage are paired. Stage history is not available. |
| Final result | `state`, optional `completionSummary` | Completed and Failed remain separate. `recoverable` and failure classification can distinguish a recoverable failure when present. |
| Uncertain integration | `state=Failed`, `recoveryState=LeaseExpiredUncertain`, lease `state=Expired` | Warning treatment; reconciliation action only under `canReconcile`'s exact contract. |
| Time | `createdAtUtc`, `assignedAtUtc`, `startedAtUtc`, `completedAtUtc`, optional `durationMilliseconds` | Show only timestamps that exist; active duration is elapsed time, not progress. |
| Summary / next action | `pendingReason`, `recoveryReason`, `completionSummary`; current state | No operator action by default. Cancel only while Queued. Reconcile only for an expired uncertain integration. |
| Secondary evidence | assignment, lease, attempt, recovery, validation, integration and eligibility fields | Disclosure sections; render only values supplied by the API. |

The examples use plausible sample values to make the composition reviewable. Their names and timestamps are illustrative, not API observations. The actual list API is bounded to 50 results per page; it does not provide a total history count. Loading, no matching requests, empty history and read failure are separate states.
