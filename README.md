# codex-worker

`codex-worker` is a .NET 10 polling daemon for multiple independently configured GitHub repositories. This release is V0.8.0. The startup header and Telegram lifecycle messages read the version from the application assembly version, configured in the project file. It executes Issues concurrently within explicit global and per-project limits, asks Codex to implement each Issue in its own Git worktree, runs that project's authoritative validation there, and owns the Git and GitHub lifecycle.

## V0.7 local control plane

The worker serves a local dashboard and JSON management API at `http://127.0.0.1:5080/` by default. The API can be disabled with `api.enabled: false`; `api.listenUrl` accepts only a loopback IP address. Keep remote access behind an SSH tunnel or another external access mechanism. The dashboard reads and changes state only through the API. Routes provide worker status and capabilities, project and execution views, bounded recent events, a Server-Sent Events stream, project configuration CRUD/reload, project lifecycle controls, and worker drain status.

Project ownership is explicit in global Worker YAML: `projects.ownership: standalone` (the default) means local project YAML is authoritative; `projects.ownership: managed` records managed-mode intent and requires `server.enabled: true`. In V0.8, both modes still load Worker execution settings from local project YAML. Central Server project definitions are separately managed inventory and are not synchronized to Workers or used for execution dispatch yet. Worker-local checkout paths, dotenv files, and credentials are not part of central project definitions. Do not treat central definitions as applied Worker configuration until synchronization is introduced.

Projects start enabled. **Enabled** projects may be scheduled within their configured capacity. **Disabled** projects receive no new executions, while existing executions finish. **Draining** projects receive no new executions and report completion after their active count reaches zero. Worker drain similarly stops all new claims while allowing active work to finish. Project removal is rejected while a claim is reserved or execution history is active. Local runtime events are process-local and bounded; SQLite execution history remains the authoritative execution record.

The API has no authentication in V0.7, so its loopback-only default is a security boundary. It does not expose process environment values, dotenv contents, GitHub tokens, Telegram credentials, or Codex authentication. Do not bind it to a non-loopback interface.

## Execution architecture

The host owns project scheduling and dispatches up to the global `worker.maxParallelTasks` limit, subject to each project's own `worker.maxParallelTasks` limit. Both default to `1`; supported values are `1` through `8`, and project limits never raise the global cap. Round-robin scheduling skips projects that are at capacity, so they do not hold up projects with free slots. Only claimed executions consume capacity; Issues waiting on dependencies do not. `ExecutionRunner` owns each execution's workspace, Codex implementation, sequential authoritative validation and bounded repair, Git state checks, integration request, and task outcome. Each attempt gets its own Git repository execution object for mutable branch, worktree, and starting-commit state. GitHub claiming and result reporting remain in the worker orchestration; infrastructure failures stop new scheduling and trigger conservative worker-wide cancellation, while blocked and safe task-failed outcomes free capacity without cancelling unrelated executions.

Codex and validation can run concurrently in separate execution worktrees. Shared Git setup, cleanup, and integration are serialized with a repository-scoped gate; executions for other repositories use independent gates. Integration fetches the current base before merging, so work that started against an older base either integrates safely or fails without overwriting newer work. Shutdown stops scheduling, cancels active executions, and waits for child processes to exit. An interrupted or uncertain execution is retained in history and its worktree is preserved for inspection.

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

Each project retains its own repository, checkout, Git branches and lifecycle settings, GitHub labels, `worker.maxParallelTasks`, `worker.retryMode`, `worker.recoveryRetentionDays`, Codex instructions/model/reasoning/timeout, and validation commands/timeout/repair limit. Recovery retention defaults to 7 days and accepts 1–3650 days. Set the project limit in that project's YAML under `worker`; it defaults to `1`. A project may use a higher limit when its checkout and repository setup support concurrent executions, while the global limit remains the ceiling across all projects. There is no mutable current-project configuration. Telegram enablement, polling interval, and the global concurrency limit are worker-global.

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

New execution branches use `{featurePrefix}{slugified-title}-{issueNumber}`; completed branches use `{completedPrefix}{slugified-title}-{issueNumber}`. Existing branches created with the previous number-first naming convention remain untouched and are not used to determine Issue identity.

Codex implementation and validation run in a managed worktree outside the source checkout at `~/.codex-worker/worktrees/<repository-owner>/<repository-name>/<execution-id>`. Repository scoping keeps projects with similarly named checkout directories isolated. Every attempt has a new execution ID and durable history record; retry history records the prior execution ID and attempt number, and never reopens or rewrites the earlier result. The execution ID in the directory name identifies its owner if an infrastructure failure leaves the worktree for inspection.

Successful integration and failed or blocked outcomes with no useful file changes clean their worktree. Useful changes from a safe failed or blocked execution are retained as recoverable Git state; infrastructure failures and uncertain Git state remain preserved conservatively. A later attempt always gets a new worktree. `worker.retryMode` chooses whether the new attempt starts from the current authoritative base (`restart`, the default) or copies verified file state from the prior recoverable attempt (`resume`). Resume verifies the persisted execution identity, branch, base commit, registered worktree and Git state before copying. Set a failed or blocked Issue back to the ready state to retry it; a configured resume stops safely if there is no valid recoverable workspace. A successful resume or clean restart removes the previous recovery resources after the new worktree is created.

Recoverable workspaces expire after 7 days by default (`worker.recoveryRetentionDays` in project YAML; allowed range 1–3650). Startup reconciles missing and expired workspaces and deletes only a registered worktree and generated branch whose execution ID, branch, and base commit match persisted metadata. Cleanup never removes execution history, which keeps summaries, outcomes, and retry relationships available after workspace deletion. The Worker API and dashboard show attempt number, previous execution, resume/restart choice, recovery state, and expiry. The canonical checkout coordinates base branch updates and integration and does not receive task file edits.

Execution history is stored locally in SQLite at `~/.codex-worker/codex-worker.db`, outside project checkouts. It records concise execution and validation summaries without raw process logs or environment contents. An execution left without a terminal state by a worker restart remains marked as incomplete; the worker does not resume it automatically.

## Telegram and console

When global `telegram.enabled` is true, set `TELEGRAM_BOT_TOKEN` and `TELEGRAM_CHAT_ID` in the worker environment. Global start/stop and infrastructure messages describe the worker and include its assembly-derived version (for example, `CW 0.6.0 · INFRAESTRUCTURA`). Task headers include the version, project, and event; the complete Issue title appears below as a link to that Issue in its configured repository. Completion messages retain duration, commit, integration and preserved branch details, and the complete Codex implementation summary when available. Telegram delivery failures are warnings and never change task outcomes. Secrets are not stored in YAML.

Interactive terminals get a spinner, elapsed idle timer, restrained color, and deduplicated global idle status. Redirected output remains line-based without animation or ANSI sequences. Ctrl+C and SIGTERM request graceful shutdown. Startup/configuration/infrastructure failures exit non-zero.

## Build and run

### Codex Server

The standalone `CodexServer` application hosts the central control plane API and management dashboard at `/`. The dashboard summarizes Server status, registered Workers and capacity, shows Worker details and availability, and supports authenticated central project CRUD through the Server APIs. Worker status updates use an authenticated Server-Sent Events stream at `/api/v1/events/stream`. All Worker and project registry reads and writes require the Server management token; only basic Server status, version, health, and the dashboard shell are public. The dashboard asks an operator for the Server management token, uses it for registry data, and keeps it in page memory without displaying or persisting it. A central project contains a stable ID, name, repository, default branch, description, and extensible runtime requirement names. It has no checkout path, dotenv contents, or secret fields. Project updates and removals require the current revision; stale writes return `409 Conflict`. Names and repositories are unique without regard to case. Invalid definitions are rejected before persistence. Registration and project metadata are stored durably in SQLite; normal project responses contain no secrets. The local Worker dashboard remains focused on Worker diagnostics and control.

Run the server independently from the worker:

```sh
dotnet run --project src/CodexServer/CodexServer.csproj
```

By default it listens on `http://127.0.0.1:5090` and stores its database at `~/.codex-server/codex-server.db`. Configure it through `appsettings.json`, environment variables, or command-line configuration keys. For example:

```sh
dotnet run --project src/CodexServer/CodexServer.csproj -- --Server:ListenUrl=http://127.0.0.1:5091 --Server:DatabasePath=/tmp/codex-server.db
```

The equivalent environment variables are `Server__ListenUrl` and `Server__DatabasePath`. The listen URL must be an absolute HTTP or HTTPS URL with a valid port. The server creates the database directory and initializes its schema before accepting requests. No service manager is required.

Workers register with `PUT /api/v1/workers/{workerId}`, send periodic heartbeats to `POST /api/v1/workers/{workerId}/heartbeat`, and can be inspected at `GET /api/v1/workers` or `GET /api/v1/workers/{workerId}`. Registration and heartbeat require `CODEX_SERVER_REGISTRATION_TOKEN` on Server and Worker. Registry reads and project management require a separate `CODEX_SERVER_MANAGEMENT_TOKEN`, which should only be given to trusted operators. Neither token is accepted from YAML. Use long random values for both and HTTPS when traffic crosses a trusted host boundary. Without the corresponding Server token, each API is denied. Repeat registration updates the existing Worker entry by its stable ID. The public wire contract is versioned with `contractVersion: 1`; unknown contract versions are rejected, and the Server does not depend on Worker persistence types. V0.8 supports this contract version only; negotiate compatibility before upgrading either side to a future contract version. The registration token grants registration and heartbeat access to any Worker identity and is shared across managed Workers; V0.8 does not provide per-Worker credentials or role-level project access.

Managed mode is opt-in in the global Worker YAML:

```yaml
server:
  enabled: true
  url: https://server.example:5090
  # identityFile: /var/lib/codex-worker/worker-id
```

The Worker creates its identity automatically at `~/.codex-worker/worker-id` (mode 0600 on Unix). Set `server.identityFile` to choose another durable path. The random identity is stable across restarts; it is not derived from the hostname. Startup makes one registration request before GitHub queue access. Registration failure stops managed startup safely with no claim attempted; the Worker does not silently execute in standalone mode. With no `server` configuration, the default standalone behavior is unchanged. Managed Workers still load their execution projects from local YAML, execute their existing polling workloads, and report heartbeat state; the Server does not assign executions or project configuration in V0.8. Registration sends only the Worker ID, display name, Worker version, platform, configured global capacity, and capability names. It does not send project settings, credentials, environment variables, or secrets.

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
