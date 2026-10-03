# codex-worker

`codex-worker` is a .NET 10 polling daemon for multiple independently configured GitHub repositories. The source version is defined in `Directory.Build.props`; published releases embed the version selected by the release command. The startup header and Telegram lifecycle messages read the version from the application assembly version, set once in the shared MSBuild properties. It executes Issues concurrently within explicit global and per-project limits, asks Codex to implement each Issue in its own Git worktree, runs that project's authoritative validation there, and owns the Git and GitHub lifecycle.

Linux x64 self-contained Server and Worker release archives can be built with [`packaging/release-linux-x64.sh`](packaging/release-linux-x64.sh). Run `packaging/release.sh X.Y.Z` from a clean checkout to build and publish a versioned GitHub Release. See [Linux release packaging](docs/release-packaging.md) for prerequisites, artifact contents, and recovery steps.

Installed hosts share `sudo codex-server update` and `sudo codex-worker update`, with `--check`, `--yes` and `--json`. See [self-update behavior and scripting contract](docs/self-update.md).

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
```

`cw status` summarizes the checkout, Git worktrees, and the `codex-worker` systemd service. `cw projects` reads `projects.directory` from `/etc/codex-worker/worker.yml` and lists the project YAML paths without displaying configuration contents. Set `CW_WORKER_CONFIG` when the local Worker uses a different global configuration.

`cw deploy` publishes this checkout in Release configuration, stages output before replacing `~/apps/codex-worker`, then starts and verifies the systemd service. It manages a system-level service through `sudo -n systemctl stop/start`, so the invoking user needs suitable non-interactive sudo permission for those required operations. A least-privilege policy can authorize only the applicable systemctl operations; unrestricted sudo is not required. Permission and service failures are reported directly, and a failed stop leaves the current deployment untouched. Set `CW_DEPLOY_DIR` to use another deployment directory. On successful replacement, the prior deployment is retained beside the target for inspection. This command deploys the local development Worker; it is **not** the GitHub/product release process and does not change the product version. For a host with systemd configured for this local deployment, smoke-test with `./cw deploy`, `systemctl is-active codex-worker`, `./cw status`, and `./cw log`.

`cw version` and `cw release` use the same configured source checkout. GitHub release queries are pinned to that checkout's `origin` remote.

The production release workflow remains [`packaging/release.sh`](packaging/release.sh).

## Install Server + Worker

Use Ubuntu 24.04 x86_64 with sudo, systemd, `curl`, `tar`, and `sha256sum` (plus OpenSSL on the Server). Release archives include .NET; no checkout or .NET SDK/runtime is needed. Run A and B on the Server host and C on the Worker host.

### A. Install Server

Choose a **published release**, then use the same version on both hosts. These examples use `0.15.0`; replace it with your selected release:

```sh
RELEASE_VERSION=0.15.0
curl -fsSL "https://raw.githubusercontent.com/duned/codex-worker/v${RELEASE_VERSION}/packaging/linux/install-server.sh" | sudo bash -s -- --version "$RELEASE_VERSION"
```

The installer verifies the archive checksum, enables and starts the service, and generates a management token. The default endpoint is `http://127.0.0.1:5090`. For a separate Worker host, first provide a reachable HTTPS endpoint through a TLS proxy and firewall; see [Server configuration](#codex-server).

### B. Create a Worker bootstrap token

On the Server, create a single-use token (valid for 15 minutes):

```sh
sudo codex-server worker-token create
```

Keep the token private for the prompt in C. To open the Server dashboard, retrieve its management token with `sudo sed -n 's/^[[:space:]]*CODEX_SERVER_MANAGEMENT_TOKEN[[:space:]]*=[[:space:]]*//p' /etc/codex-server/server.env` and enter it in the dashboard token prompt.

Use `sudo codex-server worker show <worker-id>` to inspect an enrolled Worker. Operators can run `worker enable`, `worker drain`, and `worker disable` with the same Worker ID. **Enabled** allows new assignments. **Draining** and **Disabled** stop new assignments; already assigned or running work keeps its current lease and may report completion. Drain remains visible as draining until the active assignment count reaches zero, then the dashboard and CLI show it as drained. Re-enable scheduling explicitly afterward. These controls do not change Worker heartbeat, readiness, capabilities, or local lifecycle.

`worker revoke-token <worker-id>` revokes that Worker's generated API token. Existing leases are not deleted, but that Worker can no longer renew or report them; lease expiry and the existing recovery path apply. `worker revoke-delivery-token <worker-id>` separately revokes the Worker credential-delivery token. `worker show` and the dashboard show each state independently. The legacy `CODEX_SERVER_REGISTRATION_TOKEN` remains a server-wide fallback for Worker API calls, so revoking one Worker token does not deny a caller that still has the shared token; remove or rotate that fallback to remove its access. The one-use bootstrap authorization and the credential-delivery token are separate credentials. These commands update the configured local database while the Server service is running; use `codex-server --help` for syntax.

### C. Install and enroll Worker

On the Worker host, set the same release version and your reachable Server URL:

```sh
RELEASE_VERSION=0.15.0
SERVER_URL=https://server.example # replace with your Server endpoint
curl -fsSL "https://raw.githubusercontent.com/duned/codex-worker/v${RELEASE_VERSION}/packaging/linux/install-worker.sh" | sudo bash -s -- \
  --version "$RELEASE_VERSION" --server "$SERVER_URL" --capacity 1 --register --start
sudo systemctl status codex-worker
```

Run this from an interactive terminal: the installer asks for the bootstrap token without echoing it, registers the Worker, and enables and starts its service. Confirm the Worker appears in the Server dashboard. Installation and registration do not imply execution readiness. Managed Workers remain online and heartbeating when execution tools or authentication are unavailable. Missing capabilities and `not-ready` state are visible on the Server; compatible work is accepted only after project readiness and authenticated Codex execution preflight pass. Use supported provisioning under local Worker policy to supply tools/authentication, or configure the service account locally; readiness refreshes without reinstalling or re-registering. See [configuration](#configuration-and-migration) and the [Worker reference](docs/linux-installation.md#worker-installation-and-operations).

**Latest instead of a pinned release:** use the following commands in A and C, keeping B unchanged. `main` supplies the current installer and an omitted `--version` selects the latest GitHub Release; this choice can change between runs or hosts.

```sh
# Server host
curl -fsSL https://raw.githubusercontent.com/duned/codex-worker/main/packaging/linux/install-server.sh | sudo bash
# Worker host, with SERVER_URL set as in C
curl -fsSL https://raw.githubusercontent.com/duned/codex-worker/main/packaging/linux/install-worker.sh | sudo bash -s -- \
  --server "$SERVER_URL" --capacity 1 --register --start
```

For unattended installs, manual enrollment, upgrades, removal, and troubleshooting, use the [Linux installation reference](docs/linux-installation.md). Building and publishing releases is covered separately in [Linux release packaging](docs/release-packaging.md). See the [v0.15 hardening checkpoint](docs/v0.15-release-readiness.md) for review coverage and remaining deployment evidence.

## V0.7 local control plane

The worker serves a local dashboard and JSON management API at `http://127.0.0.1:5080/` by default. The API can be disabled with `api.enabled: false`; `api.listenUrl` accepts only a loopback IP address. Keep remote access behind an SSH tunnel or another external access mechanism. The dashboard reads and changes state only through the API. Routes provide worker status and capabilities, project and execution views, bounded recent events, a Server-Sent Events stream, project configuration CRUD/reload, project lifecycle controls, and worker drain status. `POST /api/worker/drain` stops new claims while active work completes; `POST /api/worker/drain/cancel` resumes scheduling only when no execution is active.

Project ownership is explicit in global Worker YAML: `projects.ownership: standalone` (the default) means the Worker polls GitHub directly; `projects.ownership: managed` requires `server.enabled: true` and makes the Server the source of project identity, repository, default branch, requirements, enabled state, project revisions, and execution assignments. The Server returns a versioned project snapshot. A Worker validates and applies the whole snapshot atomically and keeps a permission-restricted last-known-good copy beside its persistent identity. A temporary Server outage leaves that applied copy in use; an invalid update leaves the previous valid snapshot active. Worker identity and bootstrap connection, checkout and instruction paths, GitHub labels and credentials, environment files, Codex settings, validation commands, recovery settings, and local provisioning authorization remain Worker-local. Central project definitions contain no checkout paths, dotenv files, or credentials.

Worker-local projects start enabled. In managed mode, the central project lifecycle is authoritative for dispatch: projects start enabled; disabling prevents new enqueue requests and assignments, leaves already queued requests pending with a disabled reason, and allows assigned or running executions to finish under their existing lease. Re-enabling resumes those queued requests. Central lifecycle changes increment the project revision, so stale enable/disable requests conflict. Central deletion is rejected while any request for that project is queued, assigned, or running; completed and failed request history does not prevent deletion. Worker-local drain remains a separate runtime control and reports completion after its active count reaches zero. Local runtime events are process-local and bounded; SQLite execution history remains the authoritative execution record.

The API has no authentication in V0.7, so its loopback-only default is a security boundary. It does not expose process environment values, dotenv contents, GitHub tokens, Telegram credentials, or Codex authentication. Do not bind it to a non-loopback interface.

## Execution architecture

The host owns project scheduling and dispatches up to the global `worker.maxParallelTasks` limit, subject to each project's own `worker.maxParallelTasks` limit. Both default to `1`; supported values are `1` through `8`, and project limits never raise the global cap. Round-robin scheduling skips projects that are at capacity, so they do not hold up projects with free slots. Only claimed executions consume capacity; Issues waiting on dependencies do not. `ExecutionRunner` owns each execution's workspace, Codex implementation, sequential authoritative validation and bounded repair, Git state checks, integration request, and task outcome. Each attempt gets its own Git repository execution object for mutable branch, worktree, and starting-commit state. GitHub claiming and result reporting remain in the worker orchestration. Git/Codex infrastructure failures stop new scheduling and trigger conservative worker-wide cancellation; a GitHub CLI failure pauses the affected project while unrelated projects and active executions continue.

Codex and validation can run concurrently in separate execution worktrees. Shared Git setup, cleanup, and integration are serialized with a repository-scoped gate; executions for other repositories use independent gates. Integration fetches the current base before merging, so work that started against an older base either integrates safely or fails without overwriting newer work. Shutdown stops scheduling, cancels active executions, and waits for child processes to exit. An interrupted or uncertain execution is retained in history and its worktree is preserved for inspection.

## Startup and project scheduling

At startup the worker loads one global YAML file, discovers every project YAML file in the configured directory in deterministic filename order, validates the complete set, and inspects every checkout without changing it. Before any label initialization, it verifies that GitHub CLI (`gh`) runs, configured authentication and repository/Issue reads work, and the dependency API can be read on an existing Issue when one is available. It then queries repository labels and creates missing configured labels. Existing labels are left unchanged; custom names from each project's `github` section are supported. A project-specific checkout, capability, label query, or label creation failure marks that project unavailable and leaves healthy projects eligible. In managed mode, unavailable projects and failed Codex preflight leave the control loop online for heartbeats, configuration synchronization and provisioning, with execution scheduling disabled where readiness is missing. Standalone startup still stops if every project is unavailable or global Codex preflight fails. The worker then initializes each checkout and runs one global Codex CLI/authentication preflight. GitHub checks use `gh api` and standard `gh issue` operations; no `blockedBy` JSON field or newer GitHub CLI version is required.

The scheduler scans projects in round-robin order and stops at the first eligible ready Issue with project capacity available. Within each project, ready Issues are considered oldest first; Issues with any open GitHub `blocked by` dependency are skipped so a later independent Issue can run. Dependency state is read from GitHub on every queue scan. After claiming an Issue, the next scan starts with the following project. Empty, full, or dependency-waiting queues are skipped. When all queues have no eligible work and no execution is active, the worker waits for the global polling interval before scanning again. Operational logs report global and per-project capacity only when a slot is claimed, released, or configuration changes; an idle Worker emits a concise liveness message every 15 minutes. Retry lifecycle lines identify the attempt and prior execution once, and completion lines include lineage only for attempts after the first.

### Issue dependencies

`codex-ready` means the worker may execute an open Issue once its dependencies allow it. GitHub's native `blocked by` relationships control execution ordering. The worker reads dependencies from GitHub's paginated Issue Dependencies REST API through `gh api --paginate`; every returned page is checked. A ready Issue remains ready while it waits; the worker does not claim it, remove its label, add `codex-blocked`, comment, or send a Telegram notification. Once every dependency is closed, it becomes eligible on the next polling cycle. A dependency lookup failure pauses that project's queue without changing eligibility; healthy projects continue. V0.4.0 used `gh issue view --json blockedBy`, which is unavailable in deployed GitHub CLI 2.45.0. V0.4.1 moved to REST but supplied an incompatible extra CLI option; V0.4.2 uses the confirmed compatible REST invocation without that option.

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

`codex-blocked` means the task requires human intervention (including invalid execution metadata); it is unrelated to dependency waiting.

In standalone mode, project YAML owns repository, checkout, base branch, Git behavior, GitHub labels, `worker.maxParallelTasks`, `worker.retryMode`, `worker.recoveryRetentionDays`, Codex instructions/model/reasoning/timeout, and validation commands/timeout/repair limit. In managed mode, Server project definitions own project name, repository, default branch, declared requirements, and revision; the matching YAML supplies the checkout and other machine-local execution settings. Recovery retention defaults to 7 days and accepts 1–3650 days. Set the project limit in that project's YAML under `worker`; it defaults to `1`. A project may use a higher limit when its checkout and repository setup support concurrent executions, while the global limit remains the ceiling across all projects. There is no mutable current-project configuration. Telegram enablement, polling interval, the global concurrency limit, and provisioning authorization are worker-global/local policy.

An Issue can override the execution model and/or effort with a single explicit section:

```md
## Codex

model: <model-id>
effort: high
```

Both keys are optional and resolve independently: Issue override, then project `codex.model` / `codex.reasoningEffort`, then existing defaults (CLI-selected model and `medium` effort). Use plain unquoted key/value lines; effort accepts `low`, `medium`, `high`, or `xhigh` (case-insensitive). Model IDs accept up to 128 letters, digits, dots, underscores, and hyphens, starting with a letter or digit. The CLI remains authoritative for model availability and authentication; the worker does not maintain a model catalog. Unknown or duplicate keys, duplicate sections, invalid values, and other content in the section block the task with an actionable diagnostic before workspace preparation. End the section with a new level 1 or 2 heading before adding prose. Fenced examples outside the section and unrelated prose/headings do not select settings. Issues without the section retain existing behavior.

The resolved profile is recorded in execution history and shown in the execution API/dashboard, console, Issue report, and Telegram start message. Implementation, validation repairs, and integration recovery use the same snapshot. Restart/resume retries inherit the recorded profile even if the Issue or project settings change; a fresh execution after completion or explicit ready after integration conflict resolves settings again. History created before profile snapshots uses project/default settings once on recovery, without applying edited Issue metadata. An execution blocked for invalid metadata has no snapshot, so correcting its section applies on the next attempt.

Each project may configure an `environment.file` dotenv file. Its values are loaded and validated before startup proceeds to GitHub label or Issue queue access, then passed only to that project's Codex and validation child processes. Values are not added to the worker process environment and are not shared with other projects. Codex still filters GitHub authentication and protects its isolated Git credential configuration.

A structured task failure or exhausted validation repair is safely reported and the scheduler continues. Blocked Issues are marked blocked and the scheduler continues. Codex failures and uncertain Git integration stop the entire worker because continued repository execution may be unsafe. A GitHub CLI/provider failure pauses only the affected project, records the operation and whether Issue state is known or uncertain, and lets other projects and already active executions finish. Classified transient failures on read-only GitHub queries receive one bounded retry; GitHub mutations are never blindly retried. A failed mutation is persisted in execution history as requiring GitHub reconciliation; after restart the worker performs a read-only Issue-state check. If the Issue remains open with its ready label, the project stays paused until an operator inspects the Issue and explicitly enables the project after reconciliation. If it is no longer eligible, the worker records that state was verified and does not replay the mutation. Secondary GitHub reporting failures are recorded separately from the primary execution failure or outcome. Queue reads are safe to cancel; cancellation after a claim or during a state-changing operation is treated conservatively.

## Configuration and migration

Copy [`config/worker.example.yml`](config/worker.example.yml) to `~/.codex-worker/worker.yml` (installed Linux workers default to `/etc/codex-worker/worker.yml`), then create its `projects/` directory and add one YAML file per project based on [`config/project.example.yml`](config/project.example.yml). Run:

```sh
CodexWorker run --config ~/.codex-worker/worker.yml
```

The installed Linux service runs `/opt/codex-worker/CodexWorker run --config /etc/codex-worker/worker.yml`; the default configuration path can also be used with `/opt/codex-worker/CodexWorker run`. Use `CodexWorker --help` to list commands; `status`, `config`, and `capabilities` provide local administration views, while `provision` and `register` retain their typed local provisioning and enrollment flows. Configuration-taking commands accept `--config <path>`.

The [19.1 Worker administration and runtime audit](docs/worker-administration-runtime-audit-19.1.md) maps the current CLI, provisioning, authentication, registration and recovery surfaces to actionable follow-ups and regression/E2E scenarios. It distinguishes installed tools and local observations from authenticated execution readiness.

Use `CodexWorker config show [--json]` to inspect global Worker settings and `CodexWorker config validate [--json]` to validate the global file and configured project files. Both use the installed default path unless `--config <path>` is supplied. The JSON validation result includes a `valid` flag and diagnostics and returns a failing exit code for invalid configuration. `CodexWorker config set <setting> <value>` safely updates a bounded set of Worker settings: polling interval, preflight timeout, global parallelism, and the provisioning authorization switches and action lists. Action lists use comma-separated `type:name:operation` keys; an empty value clears a list. Unknown settings and invalid values are rejected before the file changes. Updates are written to a temporary file beside the configuration, validated, and atomically replaced. The YAML document is emitted in a normalized layout, so comments and original formatting may be removed. Restart the Worker service after a successful update for the new setting to take effect. Inspection redacts the Worker identity path and strips credentials, query strings, and fragments from a configured Server URL.

Relative `projects.directory` paths resolve from the global config file. Project `directory` paths resolve from their project YAML file; relative `codex.instructionsFile` paths resolve from the checkout. Only `.yml` and `.yaml` files are discovered, sorted by filename; unrelated files are ignored. Any malformed project file fails startup. Standalone mode requires at least one project. Managed mode may start with an empty project directory: it registers, reports zero active projects, and remains healthy and idle while it synchronizes Server configuration. When Server projects are added later, add their matching machine-local project YAML to the configured directory so the Worker has checkout and execution settings; the running Worker discovers it during its next managed configuration sync. Duplicate project names, GitHub repositories, or checkout paths are rejected. `worker.preflightTimeoutSeconds` applies to global execution-readiness preflight and is bounded to 1–300 seconds.

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

An Issue with the configured `github.integrationConflictLabel` (default `codex-integration-conflict`) can be explicitly resumed by applying the configured `github.integrationRecoveryLabel` (default `codex-integration-recovery`). The Worker verifies and reuses the preserved implementation worktree, rebases it against the current configured base branch, runs post-rebase validation, then integrates it. This creates a separately identified recovery execution linked to the original implementation execution and does not rerun the implementation prompt. Legacy integration-conflict history without recovery-state metadata is accepted only when its persisted implementation commit, branch and registered worktree can be verified. Rejected requests are written to the scheduler journal and reported through Telegram when enabled; unverifiable work is preserved for inspection.

Validation after rebase runs at most twice: the initial attempt and one retry on validation failure, against the same rebased commit and clean source state. Console logs identify each attempt and retain its useful failure diagnostics. If both attempts fail, only that execution stops: the implementation workspace is preserved with integration-conflict recovery metadata and the configured integration-conflict label, capacity is released, and scheduler polling continues. Apply the integration-recovery label after addressing the validation failure to attempt integration again. Git conflicts and uncertain repository or service state are not retried by this validation policy.

Execution history is stored locally in SQLite at `~/.codex-worker/codex-worker.db`, outside project checkouts. It records concise execution and validation summaries without raw process logs or environment contents. An execution left without a terminal state by a worker restart remains marked as incomplete; the worker does not resume it automatically.

## Telegram and console

When global `telegram.enabled` is true, set `TELEGRAM_BOT_TOKEN` and `TELEGRAM_CHAT_ID` in the worker environment. Global start/stop and infrastructure messages describe the worker and include its assembly-derived version (for example, `CW 0.6.0 · INFRAESTRUCTURA`). Task headers include the version, project, and event; the complete Issue title appears below as a link to that Issue in its configured repository. Completion messages retain duration, commit, integration and preserved branch details, and the complete Codex implementation summary when available. Telegram delivery failures are warnings and never change task outcomes. Secrets are not stored in YAML.

Interactive terminals get a spinner, elapsed idle timer, restrained color, and deduplicated global idle status. Redirected output remains line-based without animation or ANSI sequences. Ctrl+C and SIGTERM request graceful shutdown. Startup/configuration/infrastructure failures exit non-zero.

## Build and run

### Codex Server

The Server stores its Worker and project registries, centrally managed project configuration and requirements, execution metadata/history/queue/leases, provisioning plans, and credential metadata in the SQLite database configured by `Server:DatabasePath` (default: `Server:DataDirectory/codex-server.db`). On a fresh packaged Linux deployment, create a versioned, portable backup with `/opt/codex-server/current/CodexServer backup export <archive-path> /var/lib/codex-server/codex-server.db` as the Server service account. For retained installations, verify the service's configured database path first; older installations may use the service-account default described in the [installation reference](docs/linux-installation.md#server-installation-and-operations). SQLite's online backup API provides a consistent snapshot while the Server is running. Validate it with `/opt/codex-server/current/CodexServer backup validate <archive-path>`; validation checks the archive format and version, required tables, schema version, and SQLite integrity. Replace the executable path and database path with the deployed values when using another installation layout.

The same database also persists typed provisioning command history. Online backups retain both legacy plans and typed commands; archives created before typed command history existed remain restorable, with their plan history preserved. The manifest identifies the Server metadata included, and validation checks the current Worker-token and credential metadata columns plus typed-command records when that optional table is present.

Backups deliberately exclude secret material. Credential metadata is retained with status `NeedsReprovision`, assignments and encrypted credential payloads are cleared, Worker credential-delivery authorization rows are removed, Worker API-token hashes are replaced and marked revoked, and unused bootstrap authorizations are removed. Worker scheduling policy and API-token revocation metadata are retained. Management and registration tokens come from environment variables and are not stored in the database. Server process configuration (`appsettings.json`, environment assignments, and service-manager settings), node-local Codex/GitHub authentication and SSH private keys, Worker identity, local checkouts/workspaces, and external secret-provider contents are outside the database archive. Back up configuration separately without credentials; restore it from the deployment's protected configuration source. After restore, provision new Worker API and credential-delivery tokens, re-enter or retrieve each credential secret, assign it to its Worker, and re-establish any missing node-local authentication or SSH setup.

To recover, stop the Server, preserve a copy of its current database, then run `/opt/codex-server/CodexServer backup restore <archive-path> /var/lib/codex-server/codex-server.db` using a compatible Server version and an account that can write the data directory. The Server holds an exclusive `<database-path>.access-lock` for its lifetime; restore must acquire that same lock and fails before replacing the database if the Server or another restore owns it. The lock file remains in the data directory for stable cross-process coordination. Restore validates and extracts the complete backup before replacing the configured database. Restart the Server after recovery, verify `status` and `diagnostics`, then rehydrate configuration, credentials and node-local resources. The artifact records format version 1 and the registry schema version, and incompatible versions are rejected without replacing the destination.

The standalone `CodexServer` application hosts the central control plane API and management dashboard at `/`. The dashboard summarizes Server status, registered Workers and capacity, shows Worker details and availability, and supports authenticated central project CRUD and enable/disable through the Server APIs. Worker status updates use an authenticated Server-Sent Events stream at `/api/v1/events/stream`. All Worker and project registry reads and writes require the Server management token; only basic Server status, version, health, and the dashboard shell are public. The dashboard asks an operator for the Server management token, uses it for registry data, and keeps it in page memory without displaying or persisting it. A central project contains a stable ID, name, repository, default branch, description, enabled state, and structured requirements (`type`, `name`, optional `version`). Requirement types include `runtime`, `tool`, and `service`, and other type names are accepted for future capabilities. Versions support an exact numeric version (`10.0.2`) or a minimum (`>=10.0`); versions have one to four numeric components, and no wildcard, range, or dependency solving is performed. Requirement type and name are trimmed and lowercased; duplicate type/name pairs are rejected even if they specify different versions. Existing projects without requirements remain valid, and the previous string requirement form is read as a runtime requirement for compatibility. The project has no checkout path, dotenv contents, or secret fields. Project updates, lifecycle transitions, and removals require the current revision; stale writes return `409 Conflict`. Names and repositories are unique without regard to case. Invalid definitions are rejected before persistence. Registration and project metadata are stored durably in SQLite; normal project responses contain no secrets. The local Worker dashboard remains focused on Worker diagnostics and control.

The local Server CLI provides the same central project operations against the configured SQLite registry: `codex-server projects list`, `show <id>`, `create <definition.json>`, `update <id> <expectedRevision> <definition.json>`, `enable <id> <expectedRevision>`, `disable <id> <expectedRevision>`, and `delete <id> <expectedRevision>`. Create/update files use the `CentralProjectDefinition` JSON shape (`name`, `repository`, `defaultBranch`, `description`, and optional `requirements`). Project changes do not require a running Server process. CLI JSON output is available with `--json`; all mutation commands use optimistic revision checks. Delete reports the counts of queued, assigned, and running requests that prevent removal. `codex-server executions list|show|cancel|reconcile` provides bounded execution administration from the same database.

For an evidence-based capability map and proposed administration follow-ups, see the [18.1 Server control-plane audit](docs/server-control-plane-audit-18.1.md). The reproducible local acceptance campaign and its scope limits are recorded in the [18.10 Server local administration readiness report](docs/server-local-administration-18.10.md).

Local Server operators can use `codex-server status`, `codex-server diagnostics`, `codex-server config show|validate`, `codex-server projects ...`, and `codex-server executions ...`; read-only/administration output supports `--json`. These commands inspect or update the configured local registry and do not contact the running Server. On packaged Linux installations, use `sudo codex-server ...` so the helper loads `/etc/codex-server/server.env` and selects the service database. See the [Server local administration guide](docs/server-local-administration.md) for project and execution command syntax, configuration resolution, readiness semantics, redaction, and exit codes.

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

Use the [clean-machine quick path](#install-server--worker). See the [Server installation reference](docs/linux-installation.md#server-installation-and-operations) for configuration, token management, service paths, removal, and troubleshooting.

Workers register with `PUT /api/v1/workers/{workerId}`, send periodic heartbeats to `POST /api/v1/workers/{workerId}/heartbeat`, and can be inspected at `GET /api/v1/workers` or `GET /api/v1/workers/{workerId}`. While an assigned execution runs, its Worker renews ownership through `POST /api/v1/workers/{workerId}/executions/{executionId}/lease/renew`, using the current lease generation. This is distinct from the general Worker heartbeat. The one-use bootstrap token created by `worker-token create` authorizes initial registration and is consumed during enrollment. The generated per-Worker API token authorizes that Worker for registration updates, heartbeat, assignment, execution reports, leases, and configuration reads. `CODEX_SERVER_REGISTRATION_TOKEN` is also accepted as a shared server-wide fallback for those Worker APIs to support existing deployments; it is not per-Worker and cannot be revoked with a per-Worker token. Registry reads, project management, Worker administration, and execution queue management require a separate `CODEX_SERVER_MANAGEMENT_TOKEN`, which should only be given to trusted operators. Neither server token is accepted from YAML. Use long random values and HTTPS when traffic crosses a trusted host boundary. Repeat registration updates the existing Worker entry by its stable ID without changing its Server scheduling policy. The public wire contract accepts `contractVersion: 1` for older Workers and `contractVersion: 2` for structured capabilities; unknown contract versions are rejected, and the Server does not depend on Worker persistence types. V0.11 Workers send contract version 2.

Management clients can set the persistent Worker scheduling policy with `PUT /api/v1/workers/{workerId}/scheduling-policy` and `{ "policy": "Enabled|Draining|Disabled" }`. Worker registry projections keep observed availability, lifecycle, and capacity separate and include `schedulingPolicy`, the Server's `activeAssignments` count, and per-Worker API-token status (`not-configured`, `active`, or `revoked`) with its revocation time. Only Enabled Workers receive new assignments. Draining and Disabled leave active assignments and lease generations intact so the Worker can continue renewal and completion reporting. `POST /api/v1/workers/{workerId}/authentication/revoke` revokes the generated Worker API token; it does not revoke the shared registration-token fallback or credential-delivery authorization. The delivery token can be inspected with `GET /api/v1/workers/{workerId}/credential-access` and revoked with `POST /api/v1/workers/{workerId}/credential-access/revoke`; its status is `not-configured`, `active`, or `revoked`, with a revocation time when applicable. Revoking delivery authorization blocks future secret retrieval but does not affect Worker API authentication or erase credentials already delivered to a Worker. These administration routes require the management token.

Credential provisioning keeps secret values out of credential metadata and management responses. Set `CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY` to base64 encoding of 32 random bytes, stored outside YAML and separately from the Server database. The local SQLite implementation encrypts secret values with AES-GCM; database-only copies do not reveal values, but the Server process and encryption key remain trusted. Back up the key securely or stored values cannot be decrypted after key loss. A future dedicated secret-store implementation can replace this storage boundary.

Management clients can create credentials at `POST /api/v1/credentials`, list metadata at `GET /api/v1/credentials`, assign with `PUT /api/v1/credentials/{id}/assignment`, rotate using `PUT /api/v1/credentials/{id}/secret`, and revoke with `POST /api/v1/credentials/{id}/revoke`. These routes require the management token and return metadata only. To enable delivery, provision a distinct random Worker token (at least 32 characters) using `PUT /api/v1/workers/{workerId}/credential-access` with that token as the request body, then set the same value in that Worker’s `CODEX_WORKER_CREDENTIAL_DELIVERY_TOKEN` environment. The Server stores only its SHA-256 hash. The Worker retrieves an assigned value through `GET /api/v1/workers/{workerId}/credentials/{credentialId}`; this endpoint requires the Worker-specific token and returns the secret only for that Worker. Keep this token private to its Worker and use HTTPS for remote Server connections. Worker delivery returns the value in memory; provider-specific materialization and zeroing managed strings are responsibilities of the consuming integration and are not provided by the generic foundation.

### Installing and enrolling a Linux Worker

Use the [clean-machine quick path](#install-server--worker). See the [Worker installation reference](docs/linux-installation.md#worker-installation-and-operations) for unattended installation, manual enrollment, service paths, updates, removal, and troubleshooting.

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

To enable Server-managed dispatch, set `projects.ownership: managed` along with `server.enabled: true`. Workers continue to load their checkout, GitHub, Codex, validation, and recovery settings from local project YAML. Create matching central projects in the Server dashboard. Workers fetch their versioned snapshot from `GET /api/v1/workers/{workerId}/configuration`; the durable per-Worker credential is required after bootstrap. Then enqueue work with `POST /api/v1/executions` using a project ID and a positive numbered GitHub Issue reference (`issue` or `github-issue`, decimal `id`, and optional HTTPS `github.com/{owner}/{repository}/issues/{number}` URL matching the project's repository). The Server stores canonical `github-issue` references and decimal Issue numbers so aliases and leading zeroes share one active reservation. `GET /api/v1/executions` supports project/state/Issue filters and returns at most 100 results; `GET /api/v1/executions/{id}` returns one detail record. The dashboard and local `codex-server executions` CLI expose bounded list/detail, queued cancellation, and operator reconciliation. Management state transitions that assign, start, complete, or fail requests return `410 Gone`; those changes remain part of normal Worker assignment and generation-fenced report contracts. Operators may cancel only queued work. After lease expiry leaves integration uncertain, an operator must inspect authoritative repository evidence and resolve it as `NotIntegrated` (which queues a linked attempt) or `Integrated` (which records the verified commit without claiming Worker completion). Uncertain work is never automatically rerun, and physical workspace recovery remains Worker-owned. A Worker requests assignments through `POST /api/v1/workers/{workerId}/assignments/request`; the Server considers heartbeat availability and the global and per-project capacity the Worker reports. The Worker maps the Server project to its local checkout configuration, retrieves the referenced Issue, and executes it through the normal Worker pipeline. It reports structured lifecycle and final outcome details to `POST /api/v1/workers/{workerId}/executions/{executionRequestId}/report`.

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

Typed node provisioning uses `POST /api/v1/provisioning/commands` with management authentication and a body such as `{"nodeId":"server","capabilityId":"git","action":"Detect","timeoutSeconds":120,"allowElevation":false}`. A registered online Worker's ID can replace `server`. `GET /api/v1/provisioning/commands` and `GET /api/v1/provisioning/commands/{id}` report operation identity, node, capability, action, timestamps, deadline, status and a diagnostic code. The node inventory reflects queued/running operations. There is no command, argument, script, path, package or secret field; unknown input fields are rejected. Progress is currently dispatch (`Running`/`Executing`) followed by a terminal result. Process output and exception messages never enter history.

The initial product handlers support `Detect` for Git, GitHub CLI and Codex CLI; `CheckAuthentication` and `Logout` for GitHub CLI and Codex CLI; `CheckConfiguration` for Git; and `Install`, `Update`, `Uninstall` for Git on Debian-family hosts. Git operations use only the fixed `git` apt package (`Update` uses `--only-upgrade`); they do not refresh package indexes. Authentication checks and logout use the node service account's local configuration; GitHub CLI logout targets github.com. Configuration checks inspect Git user name/email without returning their values. Other combinations are rejected. Interactive login, arbitrary configuration values and secret delivery are outside this protocol; existing credential provisioning remains separate.

Read-only actions are permitted by default; Worker `deniedActions` still takes precedence. Mutations require the existing Worker-local provisioning policy (`enabled`, `allowNonPrivileged`, `allowCredentials` for logout). Package operations require both `allowElevation: true` in the request and the exact `tool:git:install`, `tool:git:update` or `tool:git:uninstall` key in `allowedPrivilegedActions`. Authentication keys use `authentication:github-cli:logout` or `authentication:codex-cli:logout`. Local Server mutations require `Server:EnableLocalProvisioning: true`; package operations additionally require `Server:AllowLocalProvisioningElevation: true` and request elevation. These settings default to false. Non-root package operations use `sudo -n` for the fixed apt command and require host-admin permission; there is no password prompt or general elevated shell.

Commands persist in the Server SQLite database. A unique active node/capability constraint rejects conflicts, while independent capabilities/nodes can have queued operations. Server execution and idle managed Worker polling remain sequential; Worker actions run only when no Issue execution is active. Workers poll the authenticated managed command endpoint and report through a node-owned report endpoint. Queued commands survive restarts and can be cancelled with the management API or `codex-server provision cancel`. Running commands use a fixed 5–600 second deadline, kill their process tree on timeout/cancellation, and cannot be remotely cancelled. They are never replayed after reconnect or Server restart. Terminal reports are idempotent. If a Worker stops or a final report is lost, the operation remains running and retains its capability lock: after its deadline, verify on the node that the process has stopped, then use management-authenticated `POST /api/v1/provisioning/commands/{id}/reconcile?nodeQuiescent=true` or `codex-server provision reconcile <id> --node-quiescent` to mark it failed/interrupted and release the lock. Reconciliation does not undo changes or retry execution. `codex-server provision list|show|create|cancel|reconcile` administers typed commands from local Server SQLite state without contacting loopback.

Legacy Worker provisioning plans remain available through `/api/v1/provisioning` for existing clients and history. New operator integrations should use typed commands. No plan migration or store merge is performed: legacy plans can replay accepted/running work under their existing idempotent-action assumption, while typed commands retain uncertain mutations and require explicit quiescence reconciliation. Plan creation is deprecated for new clients; removal requires a separately versioned migration after supported clients move.
