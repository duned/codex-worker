# codex-worker

`codex-worker` is a .NET 10 polling daemon for multiple independently configured GitHub repositories. This release is V0.11.0. The startup header and Telegram lifecycle messages read the version from the application assembly version, configured in the project file. It executes Issues concurrently within explicit global and per-project limits, asks Codex to implement each Issue in its own Git worktree, runs that project's authoritative validation there, and owns the Git and GitHub lifecycle.

## V0.7 local control plane

The worker serves a local dashboard and JSON management API at `http://127.0.0.1:5080/` by default. The API can be disabled with `api.enabled: false`; `api.listenUrl` accepts only a loopback IP address. Keep remote access behind an SSH tunnel or another external access mechanism. The dashboard reads and changes state only through the API. Routes provide worker status and capabilities, project and execution views, bounded recent events, a Server-Sent Events stream, project configuration CRUD/reload, project lifecycle controls, and worker drain status.

Project ownership is explicit in global Worker YAML: `projects.ownership: standalone` (the default) means the Worker polls GitHub directly; `projects.ownership: managed` requires `server.enabled: true` and makes the Server the source of execution assignments. Both modes load execution settings and secrets from local Worker project YAML. Central Server project definitions identify eligible projects and contain no Worker checkout paths, dotenv files, or credentials.

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

The standalone `CodexServer` application hosts the central control plane API and management dashboard at `/`. The dashboard summarizes Server status, registered Workers and capacity, shows Worker details and availability, and supports authenticated central project CRUD through the Server APIs. Worker status updates use an authenticated Server-Sent Events stream at `/api/v1/events/stream`. All Worker and project registry reads and writes require the Server management token; only basic Server status, version, health, and the dashboard shell are public. The dashboard asks an operator for the Server management token, uses it for registry data, and keeps it in page memory without displaying or persisting it. A central project contains a stable ID, name, repository, default branch, description, and structured requirements (`type`, `name`, optional `version`). Requirement types include `runtime`, `tool`, and `service`, and other type names are accepted for future capabilities. Versions support an exact numeric version (`10.0.2`) or a minimum (`>=10.0`); versions have one to four numeric components, and no wildcard, range, or dependency solving is performed. Requirement type and name are trimmed and lowercased; duplicate type/name pairs are rejected even if they specify different versions. Existing projects without requirements remain valid, and the previous string requirement form is read as a runtime requirement for compatibility. The project has no checkout path, dotenv contents, or secret fields. Project updates and removals require the current revision; stale writes return `409 Conflict`. Names and repositories are unique without regard to case. Invalid definitions are rejected before persistence. Registration and project metadata are stored durably in SQLite; normal project responses contain no secrets. The local Worker dashboard remains focused on Worker diagnostics and control.

Run the server independently from the worker:

```sh
dotnet run --project src/CodexServer/CodexServer.csproj
```

By default it listens on `http://127.0.0.1:5090` and stores its database at `~/.codex-server/codex-server.db`. Configure it through `appsettings.json`, environment variables, or command-line configuration keys. For example:

```sh
dotnet run --project src/CodexServer/CodexServer.csproj -- --Server:ListenUrl=http://127.0.0.1:5091 --Server:DatabasePath=/tmp/codex-server.db
```

The equivalent environment variables are `Server__ListenUrl` and `Server__DatabasePath`. The listen URL must be an absolute HTTP or HTTPS URL with a valid port. The server creates the database directory and initializes its schema before accepting requests. No service manager is required.

Workers register with `PUT /api/v1/workers/{workerId}`, send periodic heartbeats to `POST /api/v1/workers/{workerId}/heartbeat`, and can be inspected at `GET /api/v1/workers` or `GET /api/v1/workers/{workerId}`. While an assigned execution runs, its Worker renews ownership through `POST /api/v1/workers/{workerId}/executions/{executionId}/lease/renew`, using the current lease generation. This is distinct from the general Worker heartbeat. Registration, heartbeat, assignment, report, and lease renewal require `CODEX_SERVER_REGISTRATION_TOKEN` on Server and Worker. Registry reads, project management, and execution queue management require a separate `CODEX_SERVER_MANAGEMENT_TOKEN`, which should only be given to trusted operators. Neither token is accepted from YAML. Use long random values for both and HTTPS when traffic crosses a trusted host boundary. Repeat registration updates the existing Worker entry by its stable ID. The public wire contract accepts `contractVersion: 1` for older Workers and `contractVersion: 2` for structured capabilities; unknown contract versions are rejected, and the Server does not depend on Worker persistence types. V0.11 Workers send contract version 2. The registration token grants registration, heartbeat, assignment, execution-report, and lease-renewal access to any Worker identity and is shared across managed Workers; these general APIs do not yet have per-Worker roles or project access rules.

Credential provisioning keeps secret values out of credential metadata and management responses. Set `CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY` to base64 encoding of 32 random bytes, stored outside YAML and separately from the Server database. The local SQLite implementation encrypts secret values with AES-GCM; database-only copies do not reveal values, but the Server process and encryption key remain trusted. Back up the key securely or stored values cannot be decrypted after key loss. A future dedicated secret-store implementation can replace this storage boundary.

Management clients can create credentials at `POST /api/v1/credentials`, list metadata at `GET /api/v1/credentials`, assign with `PUT /api/v1/credentials/{id}/assignment`, rotate using `PUT /api/v1/credentials/{id}/secret`, and revoke with `POST /api/v1/credentials/{id}/revoke`. These routes require the management token and return metadata only. To enable delivery, provision a distinct random Worker token (at least 32 characters) using `PUT /api/v1/workers/{workerId}/credential-access` with that token as the request body, then set the same value in that Worker’s `CODEX_WORKER_CREDENTIAL_DELIVERY_TOKEN` environment. The Server stores only its SHA-256 hash. The Worker retrieves an assigned value through `GET /api/v1/workers/{workerId}/credentials/{credentialId}`; this endpoint requires the Worker-specific token and returns the secret only for that Worker. Keep this token private to its Worker and use HTTPS for remote Server connections. Worker delivery returns the value in memory; provider-specific materialization and zeroing managed strings are responsibilities of the consuming integration and are not provided by the generic foundation.

Managed mode is opt-in in the global Worker YAML:

```yaml
projects:
  ownership: managed
server:
  enabled: true
  url: https://server.example:5090
  # identityFile: /var/lib/codex-worker/worker-id
```

The Worker creates its identity automatically at `~/.codex-worker/worker-id` (mode 0600 on Unix). Set `server.identityFile` to choose another durable path. The random identity is stable across restarts; it is not derived from the hostname. Startup registers once before GitHub queue access. Registration failure stops managed startup safely; the Worker does not silently fall back to standalone operation. With no `server` configuration, standalone behavior remains unchanged. Registration and heartbeats advertise structured capability objects containing a type, name, and optional detected version. The built-in discovery checks for .NET, Node.js, Git, Docker, PostgreSQL's `psql` client, and the Codex CLI; detected versions are reported when available. It also advertises the GitHub Issues integration. It does not send project settings, credentials, environment variables, or secrets.

To enable Server-managed dispatch, set `projects.ownership: managed` along with `server.enabled: true`. Workers continue to load their checkout, GitHub, Codex, validation, and recovery settings from local project YAML. Create the matching central project in the Server dashboard, then enqueue work with `POST /api/v1/executions` using a project ID and a `workReference` (`type`, `id`, and optional `url`). `GET /api/v1/executions` exposes queued, assigned, running, completed, and failed requests. A Worker requests assignments through `POST /api/v1/workers/{workerId}/assignments/request`; the Server considers heartbeat availability and the global and per-project capacity the Worker reports. The Worker maps the Server project to its local configuration, retrieves the referenced Issue, and executes it through the normal Worker pipeline. It reports structured lifecycle and final outcome details to `POST /api/v1/workers/{workerId}/executions/{executionRequestId}/report`.

Server-managed provisioning plans are separate from execution requests. Management clients create a durable plan with `POST /api/v1/provisioning` (`workerId` and structured `actions`), inspect history through `GET /api/v1/provisioning`, and may cancel or reject a pending plan through `POST /api/v1/provisioning/{planId}/state`. Action types are `runtime`, `tool`, `service`, `authentication`, and `refresh-capabilities`; actions include an `operation` such as `ensure`, `install`, `configure`, `provision`, or `refresh`. There is no shell-script action. An idle managed Worker claims plans at `POST /api/v1/workers/{workerId}/provisioning/request` and reports action progress and results to the plan's `/report` endpoint. Authentication provisioning actions identify an assigned `credentialId` and repository `scope`; supported handlers provision GitHub API tokens through `gh auth login` or set the `gh` credential helper in the scoped Git checkout for HTTPS Git operations. The secret is sent to `gh` over standard input, and the Worker reports only a bounded safe outcome. Git SSH remains available through existing manual authentication and can be added through another Worker-side handler. Provisioning is disabled by default in global Worker YAML (`worker.provisioning.enabled`). Operators may separately enable non-privileged actions and allow selected privileged action keys in `worker.provisioning.allowedPrivilegedActions`, using `type:name:operation` keys such as `tool:git:install`; `deniedActions` takes precedence. Credential provisioning also needs `allowCredentials: true`. Policy decisions are made on the Worker, so Server management cannot override them. Existing local capabilities satisfy `ensure` and `install` actions when their exact or minimum version requirement matches. The initial installer maps Git, Node.js, Docker, and the PostgreSQL client to fixed Debian/Ubuntu apt packages and never accepts package names or commands from a Server as executable input. It does not refresh package indexes or replace installed incompatible versions. .NET, GitHub CLI, other package mappings, unsupported platforms, configuration actions, and unsupported authentication handlers return explicit unsupported failures. Successful installs are rediscovered and version-checked before the plan completes. Privileged apt operations pass through one Worker-side boundary, use fixed argument lists and a bounded timeout, and call `sudo -n` so they cannot wait for a password prompt. To allow a selected action for a non-root Worker, host administrators must configure non-interactive sudo permission for the exact apt install command and selected package mapping; the Worker itself need not run as root. A missing permission is reported as unavailable with a sanitized reason. Server history retains the Worker identity, action, plan timestamps, and sanitized outcome. Unsupported or denied actions fail the plan before that Worker claims Issue work.

The Server execution request ID and assignment ID are stored on the Worker's normal execution history row; each Worker attempt also has its own execution ID. These identifiers allow repeated attempts for the same Issue to remain distinct. A queued request cannot duplicate an active request for the same project and work reference. Assignment creates a durable, generation-numbered execution lease. The Worker renews it over its outbound Server connection every minute; the default lease lasts 15 minutes to tolerate temporary network interruptions. Configure `Server:ExecutionLeaseDurationSeconds` (120–86400) and `Server:ExecutionLeaseRenewalIntervalSeconds` (10–3600, less than one third of the duration) in Server settings. The execution dashboard shows the current lease owner, generation, state, expiry, and recovery state alongside Worker connectivity. Renewal and execution reports are fenced by the lease generation. A Worker cancels its attempt if renewal is rejected or ownership cannot be confirmed before its last known expiry; requests from expired generations cannot change Server execution state.

Lease expiry marks the historical attempt failed and never transfers that attempt to a new owner. If expiry is known to have occurred before integration, the Server creates a separate queued attempt linked through `retryOfExecutionId`; it requires a fresh Worker workspace because the previous Worker's local state is unavailable. If integration may have started, or the reported stage is unknown or claiming, the attempt becomes `LeaseExpiredUncertain` and remains for operator reconciliation. Expiry is therefore not itself proof that retrying is safe. The Server preserves the prior attempt and its last known Worker, stage, and lease details across restart. A lost final report acknowledgement can be retried idempotently; a duplicate report cannot override a later generation. Failed execution recoverability is reported as structured state; its physical workspace remains on the Worker.

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

V0.11 supports centrally managed project requirements and capability-aware Worker selection alongside multiple registered Workers, expiring leases, and generation fencing. Projects can declare structured `runtime`, `tool`, `service`, or future capability requirements, with optional exact numeric versions or minimum versions such as `>=10.0`. The Server assigns work only when every declared requirement matches a capability in that Worker's latest heartbeat; incompatible queued work remains queued and reports missing requirements. Projects without requirements remain eligible under the existing scheduling rules. Safe pre-integration lease expiry creates a distinct retry attempt; uncertainty at or after integration requires operator reconciliation and is never blindly reassigned. Each Worker still needs its own configured local checkout and execution history; workspace recovery stays on that Worker. Standalone mode continues to poll GitHub directly.

Managed project assignments also require two Worker authentication capabilities scoped to the central project's repository: `authentication/github-api` and `authentication/git-repository`. Worker startup checks the GitHub CLI login, repository Issue permissions and Issue API, then tests the Git origin with `ls-remote` and a non-mutating `git push --dry-run`. Successful checks are reported in heartbeats with the repository scope. The Server will not assign a project to a Worker missing either capability or reporting access for a different repository. These checks accept existing manually configured credentials; provisioning credentials centrally is optional. An authentication project requirement may include `"scope":"owner/repository"` when an operator needs to state a separate repository scope explicitly.

Authentication provisioning runs once during managed startup before readiness checks, then during idle polling. It requires both `worker.provisioning.allowCredentials: true` and `worker.provisioning.allowNonPrivileged: true`. Git HTTPS provisioning changes only the scoped checkout's `.git/config`; it does not install a global Git credential helper. GitHub CLI stores the token using its own supported authentication configuration.

Codex runs with the existing restricted child environment and Git state verification. Treat Codex and repository instructions as trusted, use a least-privileged worker account, and do not put credentials in repository files or Codex-visible content. Validation commands are trusted project YAML and run sequentially with bounded timeouts and repair attempts.
