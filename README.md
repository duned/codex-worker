# codex-worker

`codex-worker` is a .NET 10 polling daemon for multiple independently configured GitHub repositories. This release is V0.13.0. The startup header and Telegram lifecycle messages read the version from the application assembly version, set once in the shared MSBuild properties. It executes Issues concurrently within explicit global and per-project limits, asks Codex to implement each Issue in its own Git worktree, runs that project's authoritative validation there, and owns the Git and GitHub lifecycle.

Linux x64 self-contained Server and Worker release archives can be built with [`packaging/release-linux-x64.sh`](packaging/release-linux-x64.sh). See [Linux release packaging](docs/release-packaging.md) for artifact contents and GitHub Release publishing steps.

## V0.7 local control plane

The worker serves a local dashboard and JSON management API at `http://127.0.0.1:5080/` by default. The API can be disabled with `api.enabled: false`; `api.listenUrl` accepts only a loopback IP address. Keep remote access behind an SSH tunnel or another external access mechanism. The dashboard reads and changes state only through the API. Routes provide worker status and capabilities, project and execution views, bounded recent events, a Server-Sent Events stream, project configuration CRUD/reload, project lifecycle controls, and worker drain status. `POST /api/worker/drain` stops new claims while active work completes; `POST /api/worker/drain/cancel` resumes scheduling only when no execution is active.

Project ownership is explicit in global Worker YAML: `projects.ownership: standalone` (the default) means the Worker polls GitHub directly; `projects.ownership: managed` requires `server.enabled: true` and makes the Server the source of project identity, repository, default branch, requirements, project revisions, and execution assignments. The Server returns a versioned project snapshot. A Worker validates and applies the whole snapshot atomically and keeps a permission-restricted last-known-good copy beside its persistent identity. A temporary Server outage leaves that applied copy in use; an invalid update leaves the previous valid snapshot active. Worker identity and bootstrap connection, checkout and instruction paths, GitHub labels and credentials, environment files, Codex settings, validation commands, recovery settings, and local provisioning authorization remain Worker-local. Central project definitions contain no checkout paths, dotenv files, or credentials.

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

In standalone mode, project YAML owns repository, checkout, base branch, Git behavior, GitHub labels, `worker.maxParallelTasks`, `worker.retryMode`, `worker.recoveryRetentionDays`, Codex instructions/model/reasoning/timeout, and validation commands/timeout/repair limit. In managed mode, Server project definitions own project name, repository, default branch, declared requirements, and revision; the matching YAML supplies the checkout and other machine-local execution settings. Recovery retention defaults to 7 days and accepts 1–3650 days. Set the project limit in that project's YAML under `worker`; it defaults to `1`. A project may use a higher limit when its checkout and repository setup support concurrent executions, while the global limit remains the ceiling across all projects. There is no mutable current-project configuration. Telegram enablement, polling interval, the global concurrency limit, and provisioning authorization are worker-global/local policy.

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

The Server stores its Worker and project registries, centrally managed project configuration and requirements, execution metadata/history/queue/leases, provisioning plans, and credential metadata in the SQLite database configured by `Server:DatabasePath` (default: `Server:DataDirectory/codex-server.db`). On the packaged Linux deployment, create a versioned, portable backup with `/opt/codex-server/CodexServer backup export <archive-path> /var/lib/codex-server/codex-server.db`. SQLite's online backup API provides a consistent snapshot while the Server is running. Validate it with `/opt/codex-server/CodexServer backup validate <archive-path>`; validation checks the archive format and version, required tables, schema version, and SQLite integrity. Replace the executable path and database path with the deployed values when using another installation layout.

Backups deliberately exclude secret material. Credential metadata is retained with status `NeedsReprovision`, encrypted credential payloads are erased, and Worker credential delivery-token hashes are omitted. Management and registration tokens come from environment variables and are not stored in the database. Server process configuration (`appsettings.json`, environment assignments, and service-manager settings), Worker-local checkouts and identity, and external secret-provider contents are outside the database archive and must be handled separately. Store configuration backups without credentials; restore secret values from the separately managed secret system or re-enter them. This SQLite backup cannot recover credential values or Worker delivery tokens. Re-enter credentials and provision new per-Worker delivery tokens after restore.

To recover, stop the Server, preserve a copy of its current database, then run `/opt/codex-server/CodexServer backup restore <archive-path> /var/lib/codex-server/codex-server.db` using a compatible Server version and an account that can write the data directory. Restore validates and extracts the complete backup before replacing the configured database. Do not restore while the Server is running; restart it after recovery. The artifact records format version 1 and the registry schema version, and incompatible versions are rejected without replacing the destination.

The standalone `CodexServer` application hosts the central control plane API and management dashboard at `/`. The dashboard summarizes Server status, registered Workers and capacity, shows Worker details and availability, and supports authenticated central project CRUD through the Server APIs. Worker status updates use an authenticated Server-Sent Events stream at `/api/v1/events/stream`. All Worker and project registry reads and writes require the Server management token; only basic Server status, version, health, and the dashboard shell are public. The dashboard asks an operator for the Server management token, uses it for registry data, and keeps it in page memory without displaying or persisting it. A central project contains a stable ID, name, repository, default branch, description, and structured requirements (`type`, `name`, optional `version`). Requirement types include `runtime`, `tool`, and `service`, and other type names are accepted for future capabilities. Versions support an exact numeric version (`10.0.2`) or a minimum (`>=10.0`); versions have one to four numeric components, and no wildcard, range, or dependency solving is performed. Requirement type and name are trimmed and lowercased; duplicate type/name pairs are rejected even if they specify different versions. Existing projects without requirements remain valid, and the previous string requirement form is read as a runtime requirement for compatibility. The project has no checkout path, dotenv contents, or secret fields. Project updates and removals require the current revision; stale writes return `409 Conflict`. Names and repositories are unique without regard to case. Invalid definitions are rejected before persistence. Registration and project metadata are stored durably in SQLite; normal project responses contain no secrets. The local Worker dashboard remains focused on Worker diagnostics and control.

Run the server independently from the worker:

```sh
dotnet run --project src/CodexServer/CodexServer.csproj
```

By default it listens on `http://127.0.0.1:5090` and stores durable state in `~/.local/share/codex-server/`. Configure it through `appsettings.json`, environment variables, or command-line configuration keys. `Server:ListenUrl` selects the interface and port; `Server:DataDirectory` is the persistent state directory. The optional `Server:DatabasePath` overrides the SQLite file location (relative paths are resolved inside the data directory). ASP.NET Core's `ASPNETCORE_ENVIRONMENT` selects the runtime mode. For example:

```sh
dotnet run --project src/CodexServer/CodexServer.csproj -- --Server:ListenUrl=http://127.0.0.1:5091 --Server:DataDirectory=/var/lib/codex-server
```

The equivalent environment variables are `Server__ListenUrl` and `Server__DataDirectory`. The listen URL must be an absolute HTTP or HTTPS URL with a valid port. Plain HTTP is accepted only on loopback; use HTTPS directly or terminate TLS at a trusted reverse proxy for remote traffic. Binding to a non-loopback interface exposes the Server to that network. Keep it on a trusted network and protect it with TLS and firewall policy; management and registration tokens do not replace transport security. The Server creates the state directory and initializes persistence before accepting requests. `/livez` reports process liveness, `/readyz` reports initialized persistence readiness, and `/health` retains the detailed persistence health result. SIGTERM and normal service-manager stop requests use the .NET Generic Host graceful shutdown path.

#### Linux service installation

The Linux package in `packaging/linux` creates a dedicated unprivileged service account, installs the published apphost and systemd unit, and preserves the existing environment file on repeat installs. Publish for the target host and install as root:

```sh
dotnet publish src/CodexServer/CodexServer.csproj -c Release -r linux-x64 --self-contained false -o server-publish
sudo packaging/linux/install-server.sh server-publish
```

The installer keeps binaries in `/opt/codex-server`, service configuration and secrets in `/etc/codex-server/server.env`, and SQLite state in `/var/lib/codex-server`. The environment file is root-owned and readable by the Server service account (mode `0640`); keep it out of source control. Set `ASPNETCORE_ENVIRONMENT=Production`, `Server__ListenUrl=http://127.0.0.1:5090`, and `Server__DataDirectory=/var/lib/codex-server` there, plus the registration and management tokens described below. Add `CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY` when using Server-managed credentials. The Server logs to the systemd journal; temporary files use `/run/codex-server` and are not durable state.

Start and inspect the service with:

```sh
sudo systemctl enable --now codex-server
sudo systemctl status codex-server
sudo journalctl -u codex-server
```

Startup journal output includes version, runtime mode, endpoint, and state directory, but no credentials. The unit is in [`packaging/linux/codex-server.service`](packaging/linux/codex-server.service). When exposing the service remotely, configure a TLS-terminating proxy and firewall rules at the network boundary.

Workers register with `PUT /api/v1/workers/{workerId}`, send periodic heartbeats to `POST /api/v1/workers/{workerId}/heartbeat`, and can be inspected at `GET /api/v1/workers` or `GET /api/v1/workers/{workerId}`. While an assigned execution runs, its Worker renews ownership through `POST /api/v1/workers/{workerId}/executions/{executionId}/lease/renew`, using the current lease generation. This is distinct from the general Worker heartbeat. Registration, heartbeat, assignment, report, and lease renewal require `CODEX_SERVER_REGISTRATION_TOKEN` on Server and Worker. Registry reads, project management, and execution queue management require a separate `CODEX_SERVER_MANAGEMENT_TOKEN`, which should only be given to trusted operators. Neither token is accepted from YAML. Use long random values for both and HTTPS when traffic crosses a trusted host boundary. Repeat registration updates the existing Worker entry by its stable ID. The public wire contract accepts `contractVersion: 1` for older Workers and `contractVersion: 2` for structured capabilities; unknown contract versions are rejected, and the Server does not depend on Worker persistence types. V0.11 Workers send contract version 2. The registration token grants registration, heartbeat, assignment, execution-report, and lease-renewal access to any Worker identity and is shared across managed Workers; these general APIs do not yet have per-Worker roles or project access rules.

Credential provisioning keeps secret values out of credential metadata and management responses. Set `CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY` to base64 encoding of 32 random bytes, stored outside YAML and separately from the Server database. The local SQLite implementation encrypts secret values with AES-GCM; database-only copies do not reveal values, but the Server process and encryption key remain trusted. Back up the key securely or stored values cannot be decrypted after key loss. A future dedicated secret-store implementation can replace this storage boundary.

Management clients can create credentials at `POST /api/v1/credentials`, list metadata at `GET /api/v1/credentials`, assign with `PUT /api/v1/credentials/{id}/assignment`, rotate using `PUT /api/v1/credentials/{id}/secret`, and revoke with `POST /api/v1/credentials/{id}/revoke`. These routes require the management token and return metadata only. To enable delivery, provision a distinct random Worker token (at least 32 characters) using `PUT /api/v1/workers/{workerId}/credential-access` with that token as the request body, then set the same value in that Worker’s `CODEX_WORKER_CREDENTIAL_DELIVERY_TOKEN` environment. The Server stores only its SHA-256 hash. The Worker retrieves an assigned value through `GET /api/v1/workers/{workerId}/credentials/{credentialId}`; this endpoint requires the Worker-specific token and returns the secret only for that Worker. Keep this token private to its Worker and use HTTPS for remote Server connections. Worker delivery returns the value in memory; provider-specific materialization and zeroing managed strings are responsibilities of the consuming integration and are not provided by the generic foundation.

### Installing and enrolling a Linux Worker

The supported installer downloads a checksum-verified, self-contained Linux x64 Worker release and installs its systemd unit. It supports Ubuntu 24.04 x86_64 and does not need a source checkout, .NET SDK, or .NET runtime. Install the latest release or pin an explicit version:

```sh
curl -fsSL https://raw.githubusercontent.com/duned/codex-worker/main/packaging/linux/install-worker.sh | sudo bash
# Or pin a release:
curl -fsSL https://raw.githubusercontent.com/duned/codex-worker/main/packaging/linux/install-worker.sh | sudo bash -s -- --version 0.13.0
```

This installs only the Worker process. Git, GitHub CLI (`gh`), Codex CLI and its service-account authentication, and outbound HTTPS access are needed for normal task execution. Tools such as Node.js, Docker, PostgreSQL, and project-specific .NET SDKs are discovered and can be handled through Server provisioning policy; they are not installer prerequisites.

The installer is repeatable and preserves existing configuration and environment files. It creates the `codex-worker` system account and uses these locations:

| Purpose | Location | Owner |
| --- | --- | --- |
| Executable and published binaries | `/opt/codex-worker` | root |
| Global Worker configuration | `/etc/codex-worker/worker.yml` | root, group-readable by Worker |
| Secret environment variables | `/etc/codex-worker/worker.env` | root, group-readable by Worker |
| Persistent identity, SQLite execution history, and runtime state | `/var/lib/codex-worker/.codex-worker` | codex-worker |
| Project configuration and configured checkouts | `/var/lib/codex-worker/projects` or operator-selected paths | codex-worker |
| Execution worktrees | `/var/lib/codex-worker/.codex-worker/worktrees` | codex-worker |

Edit `/etc/codex-worker/worker.yml`: set `server.url`, retain the persistent `server.identityFile`, and add local project YAML files under `projects.directory`. Each project configuration must identify its checkout and credentials through the supported local authentication setup. Managed enrollment requires `projects.ownership: managed` and `server.enabled: true`; the installed starter configuration selects those values. Put `CODEX_SERVER_REGISTRATION_TOKEN` in `/etc/codex-worker/worker.env` as a systemd environment assignment, for example `CODEX_SERVER_REGISTRATION_TOKEN=<secret>`, and keep the file root-owned with mode `0640`. Do not put credentials in YAML or command-line arguments.

Start and enable the service after configuration:

```sh
sudo systemctl enable --now codex-worker
sudo systemctl status codex-worker
sudo journalctl -u codex-worker
```

On startup, the Worker loads or atomically creates its random identity at the configured persistent path, then sends an idempotent registration keyed by that ID. Repeated restarts or rerunning registration update that Server Worker record instead of creating another identity. It then fetches the versioned managed project snapshot and applies it before startup readiness checks. If registration or snapshot retrieval fails, a previously validated snapshot remains available and is selected for local configuration; without one, managed startup stops. The Worker never falls back to standalone mode, and Server-dependent assignment and provisioning remain unavailable during an outage. The Worker refreshes the snapshot while idle. The local API exposes applied and desired versions, synchronization state, last successful update time, and a bounded error through `/api/configuration-sync`. After configuration, the Worker reports discovered capabilities in heartbeats, then reports project-scoped GitHub/Git readiness and the authenticated Codex provider after startup preflight. The Server dashboard shows the Worker and current capabilities; provisioning can fill supported gaps according to Server plans and local Worker policy.

The unit runs as the unprivileged `codex-worker` account, starts after network availability, starts on reboot when enabled, restarts after runtime failures, and maps SIGTERM to graceful shutdown. Exit status 2 prevents a deterministic configuration or startup failure from entering a restart loop. Back up `/var/lib/codex-worker` to preserve identity, history, and recoverable execution state across host replacement; restoring the identity reconnects the replacement to the same Server Worker record.

For a controlled Linux Worker update, use the installer with `--version VERSION`; it verifies the new release before switching binaries and preserves the previous release under `/opt/codex-worker.previous.*`. The local [`packaging/linux/update-worker.sh`](packaging/linux/update-worker.sh) remains available for operators publishing from a local build; it stages and checks the new apphost, requests the loopback API to stop new claims, waits up to five minutes for active executions to finish, and preserves the old binaries if readiness fails.

```sh
dotnet publish src/CodexWorker/CodexWorker.csproj -c Release -r linux-x64 --self-contained true -o publish
sudo packaging/linux/update-worker.sh publish
```

To uninstall, stop and disable the service, then remove `/etc/systemd/system/codex-worker.service` and run `systemctl daemon-reload`. Remove `/opt/codex-worker` and `/etc/codex-worker` only when the binaries and configuration are no longer needed. `/var/lib/codex-worker` contains the persistent Worker identity, execution history, project checkouts, and recovery state; back it up or remove it explicitly according to your retention needs. The installer does not delete backups or state.

For troubleshooting, inspect `systemctl status codex-worker` and `journalctl -u codex-worker`. Installer failures identify the unsupported OS/architecture, missing utility, download, checksum, or extraction step. A failed update preserves the previous binaries and attempts to restore the prior service. Check release availability and outbound HTTPS access if downloads fail.

Managed mode is opt-in in the global Worker YAML:

```yaml
projects:
  ownership: managed
server:
  enabled: true
  url: https://server.example:5090
  # identityFile: /var/lib/codex-worker/worker-id
```

The Worker creates its identity automatically at `~/.codex-worker/worker-id` (mode 0600 on Unix). Set `server.identityFile` to choose another durable path. The random identity is stable across restarts; it is not derived from the hostname. Startup registers once before GitHub queue access. Managed mode requires a valid Server snapshot on first startup; after that, connectivity loss can use the cached last valid snapshot without falling back to standalone operation. With no `server` configuration, standalone behavior remains unchanged. Registration and heartbeats advertise structured capability objects containing a type, name, and optional detected version. The built-in discovery checks for .NET, Node.js, Git, Docker, PostgreSQL's `psql` client, and the Codex CLI; detected versions are reported when available. It also advertises the GitHub Issues integration. Registration and heartbeat do not send project settings, credentials, environment variables, or secrets; managed project metadata is fetched separately.

To enable Server-managed dispatch, set `projects.ownership: managed` along with `server.enabled: true`. Workers continue to load their checkout, GitHub, Codex, validation, and recovery settings from local project YAML. Create matching central projects in the Server dashboard. Workers fetch their versioned snapshot from `GET /api/v1/workers/{workerId}/configuration`; the registration token is required. Then enqueue work with `POST /api/v1/executions` using a project ID and a `workReference` (`type`, `id`, and optional `url`). `GET /api/v1/executions` exposes queued, assigned, running, completed, and failed requests. A Worker requests assignments through `POST /api/v1/workers/{workerId}/assignments/request`; the Server considers heartbeat availability and the global and per-project capacity the Worker reports. The Worker maps the Server project to its local checkout configuration, retrieves the referenced Issue, and executes it through the normal Worker pipeline. It reports structured lifecycle and final outcome details to `POST /api/v1/workers/{workerId}/executions/{executionRequestId}/report`.

Server-managed provisioning plans are separate from execution requests. Management clients create a durable plan with `POST /api/v1/provisioning` (`workerId` and structured `actions`), inspect history through `GET /api/v1/provisioning`, and may cancel or reject a pending plan through `POST /api/v1/provisioning/{planId}/state`. Action types are `runtime`, `tool`, `service`, `authentication`, and `refresh-capabilities`; actions include an `operation` such as `ensure`, `install`, `configure`, `provision`, or `refresh`. There is no shell-script action. An idle managed Worker claims plans at `POST /api/v1/workers/{workerId}/provisioning/request` and reports action progress and results to the plan's `/report` endpoint. Authentication provisioning actions identify an assigned `credentialId` and repository `scope`; supported handlers provision GitHub API tokens through `gh auth login` or set the `gh` credential helper in the scoped Git checkout for HTTPS Git operations. The secret is sent to `gh` over standard input, and the Worker reports only a bounded safe outcome. Git SSH remains available through existing manual authentication and can be added through another Worker-side handler. Provisioning is disabled by default in global Worker YAML (`worker.provisioning.enabled`). Operators may separately enable non-privileged actions and allow selected privileged action keys in `worker.provisioning.allowedPrivilegedActions`, using `type:name:operation` keys such as `tool:git:install`; `deniedActions` takes precedence. Credential provisioning also needs `allowCredentials: true`. Policy decisions are made on the Worker, so Server management cannot override them. Existing local capabilities satisfy `ensure` and `install` actions when their exact or minimum version requirement matches. The initial installer maps Git, Node.js, Docker, and the PostgreSQL client to fixed Debian/Ubuntu apt packages and never accepts package names or commands from a Server as executable input. It does not refresh package indexes or replace installed incompatible versions. .NET, GitHub CLI, other package mappings, unsupported platforms, configuration actions, and unsupported authentication handlers return explicit unsupported failures. Successful installs are rediscovered and version-checked before the plan completes. Privileged apt operations pass through one Worker-side boundary, use fixed argument lists and a bounded timeout, and call `sudo -n` so they cannot wait for a password prompt. To allow a selected action for a non-root Worker, host administrators must configure non-interactive sudo permission for the exact apt install command and selected package mapping; the Worker itself need not run as root. A missing permission is reported as unavailable with a sanitized reason. Server history retains the Worker identity, action, plan timestamps, and sanitized outcome. Unsupported or denied actions fail the plan before that Worker claims Issue work.

The Server execution request ID and assignment ID are stored on the Worker's normal execution history row; each Worker attempt also has its own execution ID. These identifiers allow repeated attempts for the same Issue to remain distinct. A queued request cannot duplicate an active request for the same project and work reference. Assignment creates a durable, generation-numbered execution lease. The Worker renews it over its outbound Server connection every minute; the default lease lasts 15 minutes to tolerate temporary network interruptions. Configure `Server:ExecutionLeaseDurationSeconds` (120–86400) and `Server:ExecutionLeaseRenewalIntervalSeconds` (10–3600, less than one third of the duration) in Server settings. The execution dashboard shows the current lease owner, generation, state, expiry, and recovery state alongside Worker connectivity. Renewal and execution reports are fenced by the lease generation. A Worker cancels its attempt if renewal is rejected or ownership cannot be confirmed before its last known expiry; requests from expired generations cannot change Server execution state.

Lease expiry marks the historical attempt failed and never transfers that attempt to a new owner. If expiry is known to have occurred before integration, the Server creates a separate queued attempt linked through `retryOfExecutionId`; it requires a fresh Worker workspace because the previous Worker's local state is unavailable. If integration may have started, or the reported stage is unknown or claiming, the attempt becomes `LeaseExpiredUncertain` and remains for operator reconciliation. Expiry is therefore not itself proof that retrying is safe. The Server preserves the prior attempt and its last known Worker, stage, and lease details across restart. A lost final report acknowledgement can be retried idempotently; a duplicate report cannot override a later generation. Failed execution recoverability is reported as structured state; its physical workspace remains on the Worker.

```sh
dotnet restore CodexWorker.sln
dotnet build CodexWorker.sln
dotnet test CodexWorker.sln
```

The application does not daemonize itself. It exits with status `0` for graceful shutdown, `2` for configuration/startup/preflight failures, and `1` for unexpected runtime failures. The Linux package includes the systemd unit with `Restart=on-failure` and `RestartPreventExitStatus=2`, so deterministic startup failures are not restarted while runtime failures remain eligible for restart:

```ini
[Service]
Restart=on-failure
RestartPreventExitStatus=2
```

V0.4.1 production startup exposed an invalid `gh api` argument construction despite the REST endpoint and authentication being available. V0.4.2 fixes the invocation and provides the startup exit status needed to prevent a deterministic startup failure from causing a service restart loop. The packaged unit can serve as a starting point for other supported systemd hosts.

## Safety and limitations

The worker never force-pushes or automatically resolves merge conflicts. Task cleanup is allowed only after verifying the worker-created branch, registered worktree, and starting commit. Infrastructure failures preserve execution worktree state for manual review. GitHub mutations are separate, so an error after a partial transition can leave state requiring inspection. No speculative recovery or service retry is attempted.

V0.12 adds Server-managed provisioning plans, Worker-local policy enforcement, bounded dependency installers, credential delivery, and capability refresh to V0.11 capability-aware scheduling. Projects can declare structured `runtime`, `tool`, `service`, or future capability requirements, with optional exact numeric versions or minimum versions such as `>=10.0`. The Server assigns work only when every declared requirement matches a capability in that Worker's latest heartbeat; incompatible queued work remains queued and reports missing requirements. Managed projects also require the configured agent provider to pass its Worker preflight. Safe pre-integration lease expiry creates a distinct retry attempt; uncertainty at or after integration requires operator reconciliation and is never blindly reassigned. Each Worker still needs its own configured local checkout and execution history; workspace recovery stays on that Worker. Standalone mode continues to poll GitHub directly.

Managed project assignments require three readiness capabilities: `authentication/github-api` and `authentication/git-repository` scoped to the central project's repository, and `agent-provider/codex` after Codex CLI/authentication preflight succeeds. Worker startup checks the GitHub CLI login, repository Issue permissions and Issue API, then tests the Git origin with `ls-remote` and a non-mutating `git push --dry-run`. Successful checks are reported in heartbeats with the repository scope. The Server will not assign a project to a Worker missing any readiness capability or reporting repository access for a different repository. These checks accept existing manually configured credentials; provisioning credentials centrally is optional. An authentication project requirement may include `"scope":"owner/repository"` when an operator needs to state a separate repository scope explicitly.

Authentication provisioning runs once during managed startup before readiness checks, then during idle polling. It requires both `worker.provisioning.allowCredentials: true` and `worker.provisioning.allowNonPrivileged: true`. Git HTTPS provisioning changes only the scoped checkout's `.git/config`; it does not install a global Git credential helper. GitHub CLI stores the token using its own supported authentication configuration.

Codex runs with the existing restricted child environment and Git state verification. Treat Codex and repository instructions as trusted, use a least-privileged worker account, and do not put credentials in repository files or Codex-visible content. Validation commands are trusted project YAML and run sequentially with bounded timeouts and repair attempts.
