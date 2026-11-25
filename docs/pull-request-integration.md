# Pull request integration

Each project chooses how the Worker delivers validated work. The default is unchanged.

| Mode | What the Worker does after validation | Issue |
| --- | --- | --- |
| `direct` (default) | Rebases onto the current base, validates again, fast-forwards the base branch and pushes it. | Labelled done and closed by the Worker. |
| `pull-request` | Rebases onto the current base, validates again, pushes the result as the completed branch and opens a pull request into the base. The base branch is never pushed. | Labelled done and left open. The pull request body says `Closes #N`, so GitHub closes the Issue when the pull request is merged. |

## Configure

- **Managed project:** in the dashboard, *Edit project → Integration → Pull request for review*. The API field is `integrationMode` (`null` or `direct`, or `pull-request`) in the [project definition](../config/managed-project.example.json). The default is stored as `null`, so existing projects keep their snapshot version.
- **Standalone project YAML:** `git.integration: pull-request` (default `direct`). It requires `git.autoMerge: true`, because the rebase and post-rebase validation run in that path. Managed projects in pull-request mode enable it themselves.

## Behaviour

- The pull request head is the completed-branch name (`git.completedPrefix`, for example `done/feature/17-…`). It is pushed without force; a refused push is reported as an integration conflict and the work stays preserved, as with a refused base push.
- If an open pull request from the same head into the same base already exists, the Worker reuses it instead of opening another.
- Opening the pull request is a single GitHub write. A failed or ambiguous create is uncertain remote state and stops reporting for reconciliation; it is never replayed automatically.
- The execution report and Issue comment list the branch and the pull request link. The Worker sends the link to the Server with the final report (`metrics.pullRequestUrl`); the execution page shows *Pull request #N opened for review* instead of *Integrated into main*.
- Statistics count a pull-request execution as completed. Whether the pull request is merged afterwards is GitHub's state, not the Worker's.

## Sub-issues: one pull request per parent

Large work is usually split into sub-issues. In pull-request mode they do not open a pull request each; their parent collects them:

| Issue | Starts from | Delivers into | Issue afterwards |
| --- | --- | --- | --- |
| Sub-issue (has a parent) | `integration/<parent>` if it exists, otherwise the base | `integration/<parent>`: rebased onto its current tip, validated again and pushed without force. The first sub-issue to finish creates the branch from the base. | Labelled done and **left open**. |
| Parent (has delivered sub-issues) | its own `integration/<parent>` | A pull request into the base, as above: the branch is rebased onto the current base and validated again. A parent that needs no change of its own still opens it. | Labelled done and left open. The pull request body says `Closes #N` for the parent and for every open sub-issue (at any depth), so merging it closes them all. |
| Issue without a parent | the base | Its own pull request, as above. | Labelled done and left open. |

- Siblings that finish at the same time are handled like concurrent integrations into the base: the push names the tip the work was rebased onto, so a refused push means a sibling got there first and the work is rebased and validated again (bounded, then preserved as an integration conflict).
- Nesting is recursive: a parent that has a parent of its own delivers its integration branch into its parent's branch, and only the top-level Issue opens the pull request.
- The integration branch is not kept up to date with the base while sub-issues run. The parent's own execution rebases it onto the current base before the pull request, with the usual conflict recovery.
- A sub-issue that needs no code change is closed as usual: there is nothing of it waiting for the merge.
- The parent opens the pull request when it runs. Make the parent blocked by its sub-issues so it runs last; a parent that is only a container and never runs leaves its integration branch without a pull request.

### Dependencies

A prerequisite (*blocked by*) normally has to be closed. In pull-request mode a sub-issue stays open until the parent's pull request is merged, so:

- A prerequisite that is a **sibling** (same parent) or a **sub-issue of the Issue itself** no longer blocks once it carries the done label (`codex-done`): its code is already in the integration branch the Issue starts from. This is how siblings chain and how the parent starts after its sub-issues.
- Any **other** prerequisite still has to be closed. An Issue outside the parent that depends on a sub-issue therefore waits until the parent's pull request is merged and GitHub closes the sub-issue.

The Server and standalone Workers apply the same rule. The Server reads parents only for open prerequisites that carry the done label, so other projects and modes make no extra GitHub requests.

## Requiring checks before merge

The repository's [CI workflow](../.github/workflows/ci.yml) runs the same command the Worker runs before integrating, `tools/validate.py`, forced to the full suite, plus the dashboard model tests, on every pull request and on pushes to `main`. To make GitHub merge only validated pull requests, add the `CI / validate` check to the base branch's protection rule and enable auto-merge or a merge queue there. The Worker does not merge pull requests.

Pushing workflow files needs a GitHub credential with the `workflow` scope; without it the push is refused with a clear `workflow scope` diagnostic.
