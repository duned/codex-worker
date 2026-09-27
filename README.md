# codex-worker

`codex-worker` is a .NET 10 polling worker for one configured GitHub repository. It claims ready Issues sequentially, asks the Codex CLI to implement each request in a dedicated checkout, runs configured validation commands, and owns the Git and GitHub lifecycle.

## V0.1.3 architecture and workflow

1. Load and validate YAML configuration with YamlDotNet.
2. Validate the dedicated checkout, its `origin`, clean state, and configured/generated Git refs. Acquire a local exclusive worker lock.
3. Run a bounded Codex availability preflight from a temporary directory, using the same authentication/environment isolation and optional configured model as task runs. Require successful execution and the exact response `OK` before querying the Issue queue.
4. Find the oldest open Issue with the configured ready label and claim it by replacing that label with the working label.
5. Update the base branch with fast-forward-only pull, create a feature branch, and run `codex exec` in `workspace-write` with structured JSON output and the configured task timeout.
6. Verify that Codex did not change the current branch or commit. Run configured validation commands sequentially, each with a timeout. If a command fails, give Codex up to `validation.maxFixAttempts` bounded repair attempts in the same working tree, rerunning authoritative validation after each repair.
7. After validation passes, commit and optionally merge/push changes. A successful no-op is completed without an empty commit, merge, or push. If repairs are exhausted or Codex reports a task failure/blocked result, clean up safely and report that Issue without stopping later work.
8. Update the Issue, comment, and close it on success. With no ready Issue, wait for `worker.pollingSeconds` and poll again. The console reports lifecycle states and elapsed times; optional Telegram messages report worker and Issue outcomes.

GitHub operations remain in the worker. Codex receives project instructions and Issue content only as task context; its final status is parsed from the required output schema, not inferred from prose.

## Prerequisites

- .NET SDK 10
- Git
- GitHub CLI (`gh`), authenticated and able to access the configured repository
- Codex CLI (`codex`), installed and authenticated
- A local clone of the configured project repository

Package restore requires NuGet access on first build.

## Configuration

Copy [`config/project.example.yml`](config/project.example.yml) to a private YAML file. Set the repository, checkout path, labels, base branch, project instructions file, and validation commands. Relative `project.directory` paths are resolved from the YAML file location; relative `codex.instructionsFile` paths are resolved from the checkout. Start with the dedicated checkout clean and already on the configured base branch; if a prior stop left it on another branch, inspect it and reconcile it manually before restart.

The checkout must be dedicated to this worker. Do not edit it concurrently by hand or point another worker at it. A local lock prevents two codex-worker processes from owning the same Git directory at once; it cannot prevent a human or unrelated process from changing files concurrently. Concurrent edits are unsupported. The worker checks for a clean starting state and verifies the branch/commit after Codex and validation.

The configuration includes project identity and paths; Git branch prefixes and integration choices; GitHub labels; Codex model (optional), reasoning effort, task timeout, and bounded preflight timeout; sequential validation commands, their timeout, and maximum repair attempts; Telegram enablement; poll interval; and Git/GitHub CLI timeouts. `validation.maxFixAttempts` defaults to 2 and is limited to 0–5; zero disables repairs. The Codex preflight timeout defaults to 60 seconds and is limited to 1–300 seconds. Defaults are shown in the example. Validation commands are generic shell commands; no language or build system is assumed. Configuration is trusted input and commands run with the worker user's permissions.

### Console output and timing

The console uses compact semantic states for startup, Codex preflight, waiting, Issue start, Codex work, validation, repair attempts, integration, success, task failure, blocked Issues, infrastructure failure, and shutdown. Interactive terminals are cleared before the header and get restrained ANSI colors plus in-place spinners with elapsed time during preflight, long operations, and idle polling. Redirected or piped stdout gets line-based status updates with no clear-screen, ANSI, or animation sequences. Repeated idle polling is represented by one waiting line until work starts. Preflight duration, Codex run and repair durations, each validation duration, integration duration, and total Issue duration are shown. Successful validation output is kept quiet; a failed command and final task failure include a concise diagnostic. Ctrl+C while safely idle displays a short stopped message; cancellation during an operation warns that Issue and checkout state need inspection.

Codex should run useful local checks while developing when possible, but those self-checks are best effort. If sandbox, network, restore, or environment restrictions prevent an optional check, Codex should report what could not run and why, while returning success if the implementation is complete. The worker's configured `validation.commands` remain the authoritative gate before commit or integration; any configured command failure prevents integration.

Unknown YAML properties and duplicate keys are rejected. Secrets do not belong in project YAML. `.gitignore` excludes common local, private, secret, environment, and log files while retaining `*.example.yml` files.

The worker expects these five distinct labels to exist in the repository (or their configured equivalents):

- `codex-ready`
- `codex-working`
- `codex-blocked`
- `codex-failed`
- `codex-done`

When `telegram.enabled` is true, set both credentials in the worker environment before startup:

- `TELEGRAM_BOT_TOKEN`
- `TELEGRAM_CHAT_ID`

Telegram sends notifications when the worker starts, an Issue starts, an Issue completes, an Issue is blocked, an Issue fails after a task-level failure or exhausted repairs, and the worker stops through cancellation or an infrastructure failure. Messages use a concise semantic icon and event headline, followed by the project or Issue details. Successful completion messages retain the concise Codex summary with duration, commit, and integration branch when available. Telegram does not send polling, validation, or repair-attempt messages. Credentials are never shown in logs or notifications. Missing credentials cause a clear startup/configuration failure before the checkout is initialized or any Issue is queried. Telegram API/network delivery errors produce a warning and do not prevent startup or change an Issue outcome.

## Build and run

```sh
dotnet restore CodexWorker.sln
dotnet build CodexWorker.sln
dotnet test CodexWorker.sln
dotnet run --project src/CodexWorker -- /path/to/project.yml
```

Use Ctrl+C for graceful cancellation. An Issue claimed at cancellation stays working for manual review. The worker does not automatically requeue it.

## Failure handling and safety

The worker distinguishes three outcomes. A structured Codex `failed` means the service evaluated the task but implementation could not be completed; a structured `blocked` means human input is required. Both are task-level outcomes and the Issue is safely cleaned up and reported before the queue continues. A configured validation failure is also task-level: the worker passes the failed command, exit status, and bounded stdout/stderr diagnostics to Codex for up to the configured number of repair attempts. Each repair acts on the existing working tree, and every `success` repair is followed by the authoritative configured validation again. Only a passing authoritative validation permits commit/integration. If validation still fails after the attempts are exhausted, the Issue is marked failed and later Issues continue.

Codex process/authentication/service/transport failures, timeouts, invalid CLI execution, or missing/invalid structured results are infrastructure failures, not task failures. Startup preflight catches these before the first Issue is queried or claimed. If Codex becomes unavailable after preflight, the worker stops immediately and leaves the Issue in its current working state for manual review; it does not blame the Issue or try service retries. Infrastructure failures in Git or GitHub likewise stop queue processing.

Dirty checkout, wrong origin, invalid refs, unsafe base-branch preparation, any Git/merge/push failure or timeout, and any GitHub query/claim/transition failure stop the worker immediately. GitHub calls are separate operations, so a failure after an earlier label/comment/close succeeded can leave partial state. The worker does not issue a contradictory follow-up transition; inspect the Issue and checkout and reconcile them manually. Integration timeouts are treated as uncertain even if a remote push may have completed. No speculative recovery is attempted.

The worker never force-pushes and does not attempt automatic conflict resolution. Merge conflicts and failed integration preserve the checkout for diagnosis. Cleanup uses `reset --hard` and `clean -fd` only for a task-level blocked/failed result after verifying the worker-created branch and original commit; this relies on the dedicated-checkout/no-concurrent-edit rule. Infrastructure failures and cancellation do not trigger destructive cleanup. Keep any uncommitted human work out of the worker checkout.

### Codex process boundary

Codex runs with the CLI's `workspace-write` sandbox and automatic approval support (`--approve-for-me`); it does not use `danger-full-access`. The worker starts Codex with a temporary home and empty `GH_CONFIG_DIR`, removes inherited `GH_*`, `GITHUB_*`, and `ACTIONS_ID_TOKEN_*` environment variables, disables system/global Git configuration, overrides Git's credential helper to empty, disables terminal credential prompts, and removes askpass variables. This prevents ordinary `gh` authentication and inherited Git credential-helper use inside the Codex child.

Codex still needs the existing Codex authentication location through `CODEX_HOME`. The CLI does not provide a simple V0.1 flag that makes arbitrary Git commands impossible. Codex may be able to run local Git commands in its writable checkout, and this implementation verifies its branch and commit before continuing. It cannot prove that Codex made no remote side effects through some other tool or credential path. Treat Codex and project instructions as trusted; use a least-privileged worker account and do not place GitHub credentials in project files, remotes, or Codex-visible files. The worker's normal Git process retains the credentials required for integration.

## V0.1 limitations

One worker handles one project sequentially. There is no distributed lock, worktree isolation, concurrent edit protection, Codex service retry policy, session recovery, sophisticated crash recovery, webhook, database, web UI, service manager setup, or auto-scaling. Validation repair attempts are bounded and are not service retries. A crash or partial GitHub transition can leave an Issue working/done while checkout or remote state needs manual inspection. Review the Issue, local branch/status, base branch, and origin refs before re-queueing or starting after an infrastructure stop.

See [`AGENTS.md`](AGENTS.md) for development boundaries.
