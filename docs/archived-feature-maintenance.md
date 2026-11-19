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

## Worker-run cleanup (legacy)

The previous flow, kept for the legacy dashboard under `/old/` and for local Worker
administration, dispatched an `archived-branches` command through
`POST /api/v1/maintenance/executions` to a Worker advertising `archived-feature-maintenance-v1`.
That Worker only deletes branches it can match to a single completed entry of its **own**
execution history with the exact commit, so branches created by other Workers, before a
reinstall, or under a renamed Issue are always kept for review. It also requires a drained
Worker. Prefer the Server cleanup above.
