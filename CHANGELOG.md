# Changelog

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
