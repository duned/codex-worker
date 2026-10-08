# Projects visual references

Open [the standalone mockups](projects-reference.html) in a browser. The file contains the Projects list and a representative project detail screen, with the empty list state shown below them. It follows the approved dashboard references' Untitled UI visual language and is a design artifact only; production UI is unchanged. Names and operational values in the mockups are illustrative, while the field groupings and status distinctions follow the current Server contracts.

## Responsive behavior

At desktop widths, project rows prioritize name and repository, lifecycle/discovery status, discovery policy, recent activity and actions. Detail uses a main content column with a Worker/activity rail. Below tablet width the detail rail stacks; narrow layouts hide the desktop sidebar, wrap project rows into compact cards and keep labels and actions readable. Long repository names wrap instead of causing page-wide overflow.

## Empty and error states

An empty project catalog should explain how to create the first project. Loading, unavailable catalog and failed reads need distinct states with retry where the existing read supports it; they must not look like an empty catalog. Empty execution and Issue histories should say no activity or no Issues are recorded. Worker observations that are missing or stale stay explicitly unknown/stale. Do not invent discovery-cycle timestamps, candidate totals or complete queue counts.

## API fields represented

- `GET /api/v1/projects` and `/api/v1/projects/{projectId}`: `id`, `name`, `repository`, `defaultBranch`, `description`, `revision`, `enabled`, `issueReadyLabel`, `issueBlockedLabel`, `requirements`, and nullable `automaticDiscovery` (`enabled`, `intervalSeconds`, `pageSize`, `deadlineSeconds`). The definition and discovery policy are separate visual groups.
- `GET /api/v1/executions?projectId=…&limit=50&offset=0`: execution `id`, `state`, `createdAtUtc`, `completedAtUtc`, `assignedWorkerId`, and `workReference` (type, id, optional URL). Project activity is bounded to the records returned; issue title and activity timestamp must only be shown when available from the authorized Issue/execution projection. The illustrative titles in the HTML are sample content, not API-backed fixture claims.
- `GET /api/v1/projects/{projectId}/github/issues`: Issue `number`, `title`, `state`, `url`, `labels`, `isEligible`, `eligibilityReasons`, and `blockedBy` relationships support the queue context. `GET /api/v1/workers` and `/api/v1/workers/{workerId}/diagnostics` provide Worker display/availability and project readiness, revision, observation and materialization fields. These observations are not scheduling authority.

No production screen behavior or API contract is changed by this reference.
