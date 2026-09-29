# Changelog

## 0.8.0

- Add the independently runnable Codex Server with durable SQLite worker and central project registries, a versioned Worker registration/heartbeat API, availability tracking, and a management dashboard.
- Add durable random Worker identity, opt-in managed registration and heartbeat reporting, while preserving standalone operation with local project YAML as the default.
- Separate Worker registration/heartbeat credentials from the Server management token for registry reads and project writes; require HTTPS for non-loopback Server exposure and document the V0.8 trust boundary and contract compatibility.
- Keep central project definitions free of Worker checkout paths and secrets, and keep execution scheduling and ownership in the Worker.

## 0.7.5

- Preserve useful workspaces from safe failed and blocked executions while cleaning outcomes with no changes.
- Keep each attempt immutable in SQLite history and link retries to their previous execution with an attempt number and explicit resume/restart policy.
- Resume only after verifying the previous workspace's persisted branch, base commit, Git registration, and unambiguous worktree state; retries always receive a new execution identity and worktree.
- Reconcile missing and expired recovery workspaces at startup, retain them for the configured period, and clean only verified worker-owned Git resources without deleting execution history.
- Expose attempt, retry, resume, and recovery retention state in the Worker API/dashboard; document configuration and recovery behavior.

## 0.7.1

- Display Issue references as `<title> #<number>` across console output, Telegram notifications, runtime events, and GitHub execution reports.
- Name new feature and completed branches with the slugified title followed by the Issue number, preserving configured prefixes and existing historical branches.

## 0.7.0

- Add a loopback-only local Worker Control API and dashboard for worker status, capabilities, projects, executions, runtime events, project controls, and worker drain.
- Add a replaceable project configuration provider boundary with validated project CRUD, atomic local YAML writes, explicit reload, and debounced file watching.
- Add enabled, disabled, and draining project lifecycle states that stop new scheduling while existing executions finish safely.
- Expose bounded process-local runtime event history and Server-Sent Events; retain SQLite as the authoritative execution history.
- Prevent project removal while execution history or an in-flight runtime reservation indicates active work.
- Document the local security boundary, dashboard/API usage, lifecycle semantics, and future Codex Server configuration direction.

## 0.6.0

- Dispatch independent Issue executions concurrently up to a global `worker.maxParallelTasks` limit and each project's own limit; both default to one and are bounded to eight.
- Keep queue scans and Issue claims sequential, skip projects at capacity, and leave dependency-blocked Issues outside execution capacity.
- Run Codex and sequential project validation in isolated per-execution Git worktrees while serializing shared repository setup, cleanup, and integration with repository-scoped gates.
- Preserve unrelated task outcomes when an execution is blocked or safely fails. Stop scheduling and cancel active work conservatively after infrastructure failure; shutdown waits for child processes and preserves uncertain execution workspaces and history.
- Document V0.6 concurrency defaults, integration coordination, cancellation behavior, and the single-worker-per-repository limitation.

## 0.5.1

- Upgrade Microsoft.Data.Sqlite to resolve its vulnerable SQLitePCLRaw native dependency.
- Capture expected startup infrastructure diagnostics through the test console abstraction.
- Identify task lifecycle notifications by worker version, project, and event, with safe clickable Issue links and complete completion summaries.

## 0.5.0

- Separate project scheduling and GitHub lifecycle coordination from per-Issue execution, which now runs through an explicit execution context and runner.
- Keep project round-robin scheduling globally sequential; document per-repository integration serialization as a requirement for future bounded concurrency in V0.6.
- Persist execution lifecycle, summaries, validation repairs, and Git integration facts in a local SQLite database outside project checkouts.
- Keep interrupted executions identifiable and add a versioned schema initialization path for future history upgrades.

## 0.4.2

- Use the GitHub CLI 2.45-compatible `gh api --paginate` invocation for Issue dependencies and parse every returned page without requiring `--slurp`.
- Return a dedicated startup/preflight failure exit code so systemd can avoid restarting deterministic configuration and capability failures.
- Document the V0.4.1 production invocation failure and the recommended systemd `RestartPreventExitStatus=2` setting.

## 0.4.1

- Fix V0.4.0 startup and scheduling compatibility with GitHub CLI 2.45.0 by reading Issue dependencies through GitHub's paginated REST API instead of the `blockedBy` JSON field.
- Validate GitHub CLI, authentication, repository/Issue access, and the dependency API before label initialization or queue polling.
- Include the assembly-derived worker version in Telegram lifecycle and infrastructure headers.

## 0.4.0

- Respect GitHub native `blocked by` dependencies when selecting ready Issues, while continuing past dependency-waiting candidates.
- Keep dependency-waiting Issues unchanged and document the recommended parent/child and dependency workflow.

## 0.3.1

- Preserve the initial implementation summary and every validation repair summary in Issue execution reports.
- Publish structured GitHub completion, failure, and blocked reports with validation history and total duration.
- Keep Telegram completion notifications concise while retaining implementation and integration summaries.

## 0.3.0

- Add optional per-project dotenv environment files for Codex and validation child processes.
- Validate configured environment files before GitHub startup work and resolve relative paths from project YAML files.
- Update the documented application version to 0.3.0.
