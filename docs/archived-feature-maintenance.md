# Archived feature branch maintenance

Workers preserve integrated work on archived branches (`done/feature/`, `complete/feature/`
and `completed/`). The Server cleans them up directly on GitHub with its own GitHub login;
no Worker takes part, so Workers do not need to be drained, online or running the project.

## Server cleanup (dashboard)

The project page includes **Old branches**. Choose a minimum age, select **Find branches**,
review the list and confirm the deletion. The Server's GitHub login needs permission to
delete branches in the project repository (`contents: write`); protected branches are refused
by GitHub and reported as such.

A branch is offered for deletion only when GitHub proves its exact tip is already contained in
the project's base branch (`compare/{base}...{tip}` is `behind` or `identical`), so deleting it
cannot lose a commit. Age is the tip commit's committer date. Branches with commits that are not
in the base, newer than the chosen age, or that GitHub could not compare are kept and listed
with the reason.

API (management authorization required):

- `GET /api/v1/projects/{projectId}/archived-branches?olderThanDays=30&offset=0` returns one
  page (up to 100) of the archived branches in ordinal order with `total`, and for each one its
  `tip`, `committedAtUtc`, `decision` (`eligible`, `recent`, `not-integrated`, `unknown`) and a
  `reason` code. The dashboard pages until `offset` reaches `total`.
- `POST /api/v1/projects/{projectId}/archived-branches/delete` with
  `{ "olderThanDays": 30, "branches": [{ "branch": "done/feature/example-17", "expectedTip": "<tip>" }] }`
  (1–100 branches). Each branch is re-read and re-assessed immediately before deletion: a moved
  tip is reported as `changed`, a branch that no longer qualifies as `retained`, a missing one as
  `missing`. The first deletion GitHub refuses stops the remaining ones (`not-attempted`).

GitHub has no compare-and-delete for refs, so a tip that moves between the re-read and the
delete request is not detected. Workers never move archived branches after creating them, and
even then the commit that was checked is already in the base branch.

## Upgrading from Worker-run cleanup

Earlier builds asked a Worker to delete archived branches (`archived-branches` maintenance
commands, the `archived-feature-maintenance-v1` capability, observer branch grants and
`cw maintenance completed-branches`). That flow is removed: a Worker could only prove branches
from its own execution history, so most branches were kept for review. On start, the Server
drops retained `archived-branches` commands and observer grants, and Workers drop their receipts
for them; other maintenance history is kept. Update the Server before its Workers: a newer Worker
does not read maintenance commands from an older Server. `reporting.allowBranchMaintenance` is
still accepted in Worker configuration and ignored.
