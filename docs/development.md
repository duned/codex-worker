# Development

Requires the .NET 10 SDK. From the repository root run `dotnet restore CodexWorker.sln`, `dotnet build CodexWorker.sln`, and `dotnet test CodexWorker.sln`. Dashboard build instructions are in [React architecture](server-dashboard-react.md); product releases use [release packaging](release-packaging.md).

The [test-suite audit](test-suite-audit-28.2.md) records the reduction baseline, retained safety coverage and timing method. The [documentation inventory](documentation-inventory.md) records the combined review and local validation limitations.

## Local developer operations

From the repository root, `./cw --help` lists the repository and local development Worker commands. The helper is repository-local. You can optionally expose it through a PATH directory:

```bash
mkdir -p ~/.local/bin
ln -sf "$(pwd)/cw" ~/.local/bin/cw
```

Make sure `~/.local/bin` is in `PATH`. Repository operations use `CW_REPO_DIR`, which defaults to `~/projects/codex-worker`, regardless of the current directory or symlink location. Set `CW_REPO_DIR` to use another Codex Worker checkout; a leading `~/` expands against the current user's home directory.

```sh
./cw --help
./cw status       # or: ./cw s
./cw deploy       # or: ./cw d
./cw log          # or: ./cw l
./cw log -f       # follow the journal
./cw log -n 300   # show the last 300 lines
./cw projects     # or: ./cw p
./cw executions list  # or: ./cw e list
./cw executions show <execution-id>
./cw executions show --issue 211
```

`cw status` summarizes the checkout, Git worktrees, and the `codex-worker` systemd service. `cw projects` reads the running Worker Management API and lists configured projects and their YAML paths. `cw executions` uses the same API (`CW_WORKER_API_URL`, default `http://127.0.0.1:5080`) to inspect persisted execution history. The recent list defaults to 100 entries; exact execution and Issue lookups include older history independently of the recent-list limit. Issue lookup displays all known attempts grouped by project/repository, with retry/resume links, outcome, timing, branches, integration, validation and recovery metadata. Missing history and unavailable or malformed API responses return a diagnostic and a nonzero exit code. These commands require the running Worker API and never read SQLite directly. Raw task/process output is excluded; task failure details that may contain validation output are represented by state and validation summaries.

For recovery inspection, cleanup and completed-branch maintenance, follow the [execution maintenance runbook](execution-maintenance.md). The running Worker owns classification and Git mutations; apply requires a completed local drain.

`cw deploy` refreshes the development WET .NET tool at `~/.local/share/wet` (override with `CW_WET_TOOL_DIR`) and manages a persistent launcher at `~/.local/bin/wet`. It verifies the package/version and that current and Bash/Zsh login-shell PATH select the launcher before replacing Worker. Ensure `~/.local/bin` is on your login-shell PATH; shell profiles are never edited; see the [development WET workflow](work-execution-toolbox.md#standalone-planning-cli-wet). Ordinary builds/tests do not install tools. The command publishes this checkout in Release configuration, stages output before replacing `~/apps/codex-worker`, then starts and verifies the systemd service. It manages a system-level service through `sudo -n systemctl stop/start`, so the invoking user needs suitable non-interactive sudo permission for those required operations. A least-privilege policy can authorize only the applicable systemctl operations; unrestricted sudo is not required. Permission and service failures are reported directly, and a failed stop leaves the current deployment untouched. Set `CW_DEPLOY_DIR` to use another deployment directory. On successful replacement, the prior deployment is retained beside the target for inspection. This command deploys the local development Worker; it is **not** the GitHub/product release process and does not change the product version. For a host with systemd configured for this local deployment, smoke-test with `./cw deploy`, `systemctl is-active codex-worker`, `./cw status`, and `./cw log`.

`cw version` and `cw release` use the same configured source checkout. GitHub release queries are pinned to that checkout's `origin` remote.

`cw vr` shows the current product version and latest release using the existing read-only inspections. `cw vr 0.27.0` runs `cw v 0.27.0` first, then `cw r 0.27.0` only after the version commit and push succeed. `cw vr +`, `cw vr ++`, and `cw vr +++` select a patch, minor, or major increment from the current checkout version; minor resets patch, and major resets minor and patch (for example, `0.26.4` becomes `0.26.5`, `0.27.0`, or `1.0.0`). The selected version remains fixed through synchronization; existing version preflight checks can reject it.

Any version failure stops before release publication. If publication fails, the version is already committed/pushed and remains intact; publication may have an uncertain outcome. Inspect GitHub release/tag state and follow the release workflow's recovery guidance before manually using `cw r VERSION`. The combined command never rolls back, deletes remote state, or automatically retries publication. This is a repository-local developer command, separate from installed Worker and WET commands.

The production release workflow remains [`packaging/release.sh`](../packaging/release.sh).
