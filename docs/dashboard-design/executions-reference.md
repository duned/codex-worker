# Executions visual references

The [Executions list](executions-list-reference.svg) and [Execution detail](execution-detail-reference.svg) images are embedded in the dashboard design guide. This note preserves API field mappings; sample values are illustrative.

## Field and state mapping

- Project: `projectId`, resolved from project summary.
- Issue: `workReference.type`, `.id`, optional `.url`; titles only when supplied by an authorized API.
- Worker: `assignedWorkerId`, resolved from Worker observation.
- State, stage and outcome: `state`, optional `currentStage`, `completionSummary`, `recoverable` and recovery fields. Preserve distinct terminal outcomes.
- Times: `createdAtUtc`, assignment/start/completion timestamps and optional duration. Show only supplied values.
- The executions API is bounded to 50 records per page and does not provide a global total.
