# codex-worker

`codex-worker` is a .NET 10 polling daemon for multiple independently configured GitHub repositories. This release is V0.2.1. The startup header reads its version from the application assembly version, configured in the project file. It runs one Issue at a time globally, asks Codex to implement it in that project's dedicated checkout, runs that project's authoritative validation, and owns the Git and GitHub lifecycle.

## V0.2 architecture

At startup the worker loads one global YAML file, discovers every project YAML file in the configured directory in deterministic filename order, validates the complete set, and inspects every checkout without changing it. Only after all projects pass those read-only checks does it query repository labels and create missing configured labels. Existing labels are left unchanged; custom names from each project's `github` section are supported. A validation, label query, or label creation failure stops startup before Issue queue access. The worker then initializes each checkout and runs one global Codex CLI/authentication preflight.

The scheduler scans projects in round-robin order and stops at the first ready Issue. After processing an Issue, its next scan starts with the following project. Empty queues are skipped. When all queues are empty, the worker waits for the global polling interval before scanning again. There is never more than one active Issue or Codex execution across the process.

Each project retains its own repository, checkout, Git branches and lifecycle settings, GitHub labels, Codex instructions/model/reasoning/timeout, and validation commands/timeout/repair limit. There is no mutable current-project configuration. Telegram enablement and polling interval are worker-global.

A structured task failure or exhausted validation repair is safely reported and the scheduler continues. Blocked Issues are marked blocked and the scheduler continues. Infrastructure failures—including Codex service/authentication errors and uncertain Git/GitHub operations—stop the entire worker. Queue reads are safe to cancel; cancellation after a claim or during a state-changing operation is treated conservatively.

## Configuration and migration

Copy [`config/worker.example.yml`](config/worker.example.yml) to `~/.codex-worker/worker.yml`, then create its `projects/` directory and add one YAML file per project based on [`config/project.example.yml`](config/project.example.yml). Run:

```sh
CodexWorker ~/.codex-worker/worker.yml
```

Relative `projects.directory` paths resolve from the global config file. Project `directory` paths resolve from their project YAML file; relative `codex.instructionsFile` paths resolve from the checkout. Only `.yml` and `.yaml` files are discovered, sorted by filename; unrelated files are ignored. Any malformed project file fails startup. At least one project is required. Duplicate project names, GitHub repositories, or checkout paths are rejected. `worker.preflightTimeoutSeconds` applies to the one global preflight and is bounded to 1–300 seconds.

To migrate from V0.1.x, move its project YAML into the new projects directory. Remove `telegram`, `worker.pollingSeconds`, and `codex.preflightTimeoutSeconds` from that project file. Put Telegram enablement, polling interval, and the global preflight timeout in `worker.yml`. Keep per-project `worker.gitTimeoutSeconds` and `worker.githubTimeoutSeconds` if customized. There is one CLI/configuration path; `CodexWorker <project.yml>` is no longer supported.

The configured checkout must be dedicated to this worker and initially clean on its configured base branch. Startup verifies the Git origin, checkout root, branch, cleanliness, and generated branch refs for every project before queue access. The worker then acquires local checkout locks and updates configured base branches. Do not edit a checkout concurrently or configure the same repository in multiple project files.

## Telegram and console

When global `telegram.enabled` is true, set `TELEGRAM_BOT_TOKEN` and `TELEGRAM_CHAT_ID` in the worker environment. Global start/stop and infrastructure messages describe the worker; task messages include the project name, Issue, duration, and completion details when available. Telegram delivery failures are warnings and never change task outcomes. Secrets are not stored in YAML.

Interactive terminals get a spinner, elapsed idle timer, restrained color, and deduplicated global idle status. Redirected output remains line-based without animation or ANSI sequences. Ctrl+C and SIGTERM request graceful shutdown. Startup/configuration/infrastructure failures exit non-zero.

## Build and run

```sh
dotnet restore CodexWorker.sln
dotnet build CodexWorker.sln
dotnet test CodexWorker.sln
```

The application does not daemonize itself. A future systemd service can own this one process and pass its `worker.yml` path; V0.2 does not add a unit file or installer.

## Safety and limitations

The worker never force-pushes or automatically resolves merge conflicts. Task cleanup is allowed only after verifying the worker-created branch and starting commit. Infrastructure failures preserve checkout state for manual review. GitHub mutations are separate, so an error after a partial transition can leave state requiring inspection. No speculative recovery or service retry is attempted.

V0.2 assumes exactly one Codex Worker instance manages a given configured repository. The local checkout lock prevents two local processes from owning one checkout, but there are no distributed leases or cross-machine ownership guarantees. Multiple projects are supported; parallel Issue execution, multiple-worker coordination, databases, webhooks, session recovery, and service-manager setup are not.

Codex runs with the existing restricted child environment and Git state verification. Treat Codex and repository instructions as trusted, use a least-privileged worker account, and do not put credentials in repository files or Codex-visible content. Validation commands are trusted project YAML and run sequentially with bounded timeouts and repair attempts.
