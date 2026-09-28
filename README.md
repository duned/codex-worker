# codex-worker

`codex-worker` is a .NET 10 polling daemon for multiple independently configured GitHub repositories. This release is V0.5.1. The startup header and Telegram lifecycle messages read the version from the application assembly version, configured in the project file. It runs one Issue at a time globally, asks Codex to implement it in an execution-specific Git worktree, runs that project's authoritative validation there, and owns the Git and GitHub lifecycle.

## V0.5 execution architecture

The host owns project scheduling and dispatches up to the global `worker.maxParallelTasks` limit, subject to each project's own `worker.maxParallelTasks` limit. Both default to `1`; supported values are `1` through `8`, and project limits never raise the global cap. Round-robin scheduling skips projects that are at capacity, so they do not hold up projects with free slots. Only claimed executions consume capacity; Issues waiting on dependencies do not. `ExecutionRunner` owns each execution's workspace, Codex implementation, sequential authoritative validation and bounded repair, Git state checks, integration request, and task outcome. Each attempt gets its own Git repository execution object for mutable branch, worktree, and starting-commit state. GitHub claiming and result reporting remain in the worker orchestration; infrastructure failures escape and stop scheduling, while blocked and safe task-failed outcomes free capacity.

Codex and validation can run concurrently in separate execution worktrees. Shared Git setup, cleanup, and integration are serialized with a repository-scoped gate; executions for other repositories use independent gates. Integration fetches the current base before merging, so work that started against an older base either integrates safely or fails without overwriting newer work.

## Startup and project scheduling

At startup the worker loads one global YAML file, discovers every project YAML file in the configured directory in deterministic filename order, validates the complete set, and inspects every checkout without changing it. Before any label initialization, it verifies that GitHub CLI (`gh`) runs, configured authentication and repository/Issue reads work, and the dependency API can be read on an existing Issue when one is available. It then queries repository labels and creates missing configured labels. Existing labels are left unchanged; custom names from each project's `github` section are supported. A validation, capability, label query, or label creation failure stops startup before queue access. The worker then initializes each checkout and runs one global Codex CLI/authentication preflight. GitHub checks use `gh api` and standard `gh issue` operations; no `blockedBy` JSON field or newer GitHub CLI version is required.

The scheduler scans projects in round-robin order and stops at the first eligible ready Issue with project capacity available. Within each project, ready Issues are considered oldest first; Issues with any open GitHub `blocked by` dependency are skipped so a later independent Issue can run. Dependency state is read from GitHub on every queue scan. After claiming an Issue, the next scan starts with the following project. Empty, full, or dependency-waiting queues are skipped. When all queues have no eligible work and no execution is active, the worker waits for the global polling interval before scanning again.

### Issue dependencies

`codex-ready` means the worker may execute an open Issue once its dependencies allow it. GitHub's native `blocked by` relationships control execution ordering. The worker reads dependencies from GitHub's paginated Issue Dependencies REST API through `gh api --paginate`; every returned page is checked. A ready Issue remains ready while it waits; the worker does not claim it, remove its label, add `codex-blocked`, comment, or send a Telegram notification. Once every dependency is closed, it becomes eligible on the next polling cycle. A dependency lookup failure stops queue processing safely. V0.4.0 used `gh issue view --json blockedBy`, which is unavailable in deployed GitHub CLI 2.45.0. V0.4.1 moved to REST but supplied an incompatible extra CLI option; V0.4.2 uses the confirmed compatible REST invocation without that option.

Parent/sub-issue relationships are organizational only. They do not create execution dependencies, make a parent executable, or cause a parent to close. Add `codex-ready` only to Issues that should run. Use native `blocked by` relationships when one child must wait for another:

```text
Parent Issue
  tracking only

#101 Domain
  codex-ready

#102 Persistence
  codex-ready
  blocked by #101

#103 Application/API
  codex-ready
  blocked by #102

#104 Frontend
  codex-ready
  blocked by #103
```

`codex-blocked` means Codex started work and requires human intervention; it is unrelated to dependency waiting.

Each project retains its own repository, checkout, Git branches and lifecycle settings, GitHub labels, `worker.maxParallelTasks`, Codex instructions/model/reasoning/timeout, and validation commands/timeout/repair limit. Set the project limit in that project's YAML under `worker`; it defaults to `1`. A project may use a higher limit when its checkout and repository setup support concurrent executions, while the global limit remains the ceiling across all projects. There is no mutable current-project configuration. Telegram enablement, polling interval, and the global concurrency limit are worker-global.

Each project may configure an `environment.file` dotenv file. Its values are loaded and validated before startup proceeds to GitHub label or Issue queue access, then passed only to that project's Codex and validation child processes. Values are not added to the worker process environment and are not shared with other projects. Codex still filters GitHub authentication and protects its isolated Git credential configuration.

A structured task failure or exhausted validation repair is safely reported and the scheduler continues. Blocked Issues are marked blocked and the scheduler continues. Infrastructure failures—including Codex service/authentication errors and uncertain Git/GitHub operations—stop the entire worker. Queue reads are safe to cancel; cancellation after a claim or during a state-changing operation is treated conservatively.

## Configuration and migration

Copy [`config/worker.example.yml`](config/worker.example.yml) to `~/.codex-worker/worker.yml`, then create its `projects/` directory and add one YAML file per project based on [`config/project.example.yml`](config/project.example.yml). Run:

```sh
CodexWorker ~/.codex-worker/worker.yml
```

Relative `projects.directory` paths resolve from the global config file. Project `directory` paths resolve from their project YAML file; relative `codex.instructionsFile` paths resolve from the checkout. Only `.yml` and `.yaml` files are discovered, sorted by filename; unrelated files are ignored. Any malformed project file fails startup. At least one project is required. Duplicate project names, GitHub repositories, or checkout paths are rejected. `worker.preflightTimeoutSeconds` applies to the one global preflight and is bounded to 1–300 seconds.

An optional project environment section accepts only a file path:

```yaml
project:
  name: Finance
  repository: duned/finance
  directory: /home/worker/projects/finance

environment:
  file: /home/worker/.config/finance/test.env
```

For example, that file can contain `FINANCE_POSTGRES_TEST_CONNECTION_STRING=Host=localhost;Port=5432;Database=finance_test;Username=finance;Password=placeholder`. Store actual credentials only in the protected environment file.

Absolute paths are supported. Relative `environment.file` paths resolve from the project YAML file. Files use one `KEY=VALUE` entry per line; blank lines and lines beginning with `#` are ignored, and text after the first `=` is preserved as the value. The file is parsed as data and is never sourced by a shell. Keep it outside the repository and restrict access, for example with `chmod 600 /home/worker/.config/finance/test.env`. Do not place real credentials in project YAML or checked-in files. Projects without this section behave as before.

To migrate from V0.1.x, move its project YAML into the new projects directory. Remove `telegram`, `worker.pollingSeconds`, and `codex.preflightTimeoutSeconds` from that project file. Put Telegram enablement, polling interval, and the global preflight timeout in `worker.yml`. Keep per-project `worker.gitTimeoutSeconds` and `worker.githubTimeoutSeconds` if customized. There is one CLI/configuration path; `CodexWorker <project.yml>` is no longer supported.

The configured checkout must be dedicated to this worker and initially clean on its configured base branch. Startup verifies the Git origin, checkout root, branch, cleanliness, and generated branch refs for every project before queue access. The worker then acquires local checkout locks and updates configured base branches. Do not edit a checkout concurrently or configure the same repository in multiple project files.

Codex implementation and validation run in a managed worktree outside the source checkout at `~/.codex-worker/worktrees/<repository-owner>/<repository-name>/<execution-id>`. Repository scoping keeps projects with similarly named checkout directories isolated. The execution ID in the directory name identifies its owner if an infrastructure failure leaves the worktree for inspection. Successful integration and safe task failure remove the worktree; uncertain Git state is preserved. The canonical checkout coordinates base branch updates and integration and does not receive task file edits.

Execution history is stored locally in SQLite at `~/.codex-worker/codex-worker.db`, outside project checkouts. It records concise execution and validation summaries without raw process logs or environment contents. An execution left without a terminal state by a worker restart remains marked as incomplete; the worker does not resume it automatically.

## Telegram and console

When global `telegram.enabled` is true, set `TELEGRAM_BOT_TOKEN` and `TELEGRAM_CHAT_ID` in the worker environment. Global start/stop and infrastructure messages describe the worker and include its assembly-derived version (for example, `CW 0.5.1 · INFRAESTRUCTURA`). Task headers include the version, project, and event; the complete Issue title appears below as a link to that Issue in its configured repository. Completion messages retain duration, commit, integration and preserved branch details, and the complete Codex implementation summary when available. Telegram delivery failures are warnings and never change task outcomes. Secrets are not stored in YAML.

Interactive terminals get a spinner, elapsed idle timer, restrained color, and deduplicated global idle status. Redirected output remains line-based without animation or ANSI sequences. Ctrl+C and SIGTERM request graceful shutdown. Startup/configuration/infrastructure failures exit non-zero.

## Build and run

```sh
dotnet restore CodexWorker.sln
dotnet build CodexWorker.sln
dotnet test CodexWorker.sln
```

The application does not daemonize itself. It exits with status `0` for graceful shutdown, `2` for configuration/startup/preflight failures, and `1` for unexpected runtime failures. For a systemd service, use `Restart=on-failure` with `RestartPreventExitStatus=2` so deterministic startup failures are not restarted while runtime failures remain eligible for restart:

```ini
[Service]
Restart=on-failure
RestartPreventExitStatus=2
```

V0.4.1 production startup exposed an invalid `gh api` argument construction despite the REST endpoint and authentication being available. V0.4.2 fixes the invocation and provides the startup exit status needed to prevent a deterministic startup failure from causing a service restart loop. Codex Worker does not install or modify systemd configuration.

## Safety and limitations

The worker never force-pushes or automatically resolves merge conflicts. Task cleanup is allowed only after verifying the worker-created branch, registered worktree, and starting commit. Infrastructure failures preserve execution worktree state for manual review. GitHub mutations are separate, so an error after a partial transition can leave state requiring inspection. No speculative recovery or service retry is attempted.

V0.6 assumes exactly one Codex Worker instance manages a given configured repository. The local checkout lock prevents two local processes from owning one checkout, but there are no distributed leases or cross-machine ownership guarantees. Multiple projects and bounded parallel Issue execution are supported within one worker process; execution history is local to one worker installation. Multiple-worker coordination, shared databases, webhooks, session recovery, and service-manager setup are not supported.

Codex runs with the existing restricted child environment and Git state verification. Treat Codex and repository instructions as trusted, use a least-privileged worker account, and do not put credentials in repository files or Codex-visible content. Validation commands are trusted project YAML and run sequentially with bounded timeouts and repair attempts.
