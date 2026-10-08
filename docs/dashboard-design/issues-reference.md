# Issues workflow visual reference

Open [issues-reference.html](issues-reference.html) directly in a browser. It is a
standalone, static mockup in the dashboard's React + Untitled UI visual direction;
the sample records are illustrative and the artifact does not change production UI.

## Screen and behavior

The Issues screen remains inside a selected Project. Project identity and discovery
configuration provide context, while the list is a bounded GitHub Issue query. The
primary scan order is title/number and project, GitHub open/closed state, queue
presence, then authoritative Server eligibility and its actionable reasons. On
small screens each row stacks these same fields into a card. State is always named
in text as well as color.

The three concepts stay separate:

- **Issue state** is `ManagedGitHubIssue.State` from GitHub.
- **Eligibility** is `IsEligible` and `EligibilityReasons` returned by the Server.
  The UI presents those fields and never recalculates eligibility.
- **Queue** is a matching record in the bounded executions projection, identified
  by `projectId`, `workReference.type`, and `workReference.id`; where present,
  `id` and `state` can label it. A missing record in that bounded response is not
  proof that no request exists globally, so the UI should say “Not shown” when the
  query is incomplete or unavailable.

Blocked-by relationships are secondary detail under the Issue, with blocker number,
title and state from the Issue response. Keep long relationship lists collapsed in
the production screen. Existing actions remain explicit: view details, add/remove
configured eligibility labels, enqueue only when the Server says eligible, and
refresh queued eligibility. Repository access and create Issue remain page-level
actions. Mutations retain their existing preview/confirmation and reconciliation
flows.

The mockup includes loading, no-match, failed-read, and mobile treatments. A failed
read must not masquerade as an empty list. An unavailable queue read must not be
rendered as “not queued.” Discovery enabled/disabled is configuration only: there
is no last-cycle timestamp, complete candidate count, or total Issues field in the
contracts shown here.

## Source API fields

| Display | Source |
| --- | --- |
| Project name, repository, enabled, ready label, automatic discovery policy | `GET /api/v1/projects` or `/api/v1/projects/{projectId}` → project definition |
| Issue number, title, state, labels, blocked-by, eligibility and reasons | `GET /api/v1/projects/{projectId}/github/issues?state={open\|closed\|all}&limit=1..100&label=...` → `ManagedGitHubIssue[]` |
| Issue detail and relationships | `GET /api/v1/projects/{projectId}/github/issues/{issueNumber}` → `ManagedGitHubIssue`; `blockedBy` includes number/title/state/url |
| Repository access state / checked time | `GET /api/v1/projects/{projectId}/github/access` → `GitHubRepositoryAccess` |
| Queue request presence and execution state | `GET /api/v1/executions?projectId=...&workType=github-issue&workId=...&limit=...&offset=...` → execution projections; bounded results only |
| Discovery policy | Project `automaticDiscovery` (`enabled`, `intervalSeconds`, `pageSize`, `deadlineSeconds`) |

The Issues endpoint defaults to a limit of 50 and supports at most 100; it returns
a list, not a total or pagination cursor. The discovery endpoint is a separate
bounded candidate inspection and is not evidence of a completed cycle. Enqueue and
queued eligibility refresh endpoints remain Server-authoritative; this visual
reference adds no client-side eligibility or discovery rules.
