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

## Requiring checks before merge

The repository's [CI workflow](../.github/workflows/ci.yml) runs the same command the Worker runs before integrating, `tools/validate.py`, forced to the full suite, plus the dashboard model tests, on every pull request and on pushes to `main`. To make GitHub merge only validated pull requests, add the `CI / validate` check to the base branch's protection rule and enable auto-merge or a merge queue there. The Worker does not merge pull requests.

Pushing workflow files needs a GitHub credential with the `workflow` scope; without it the push is refused with a clear `workflow scope` diagnostic.
