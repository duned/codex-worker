# codex-worker

`codex-worker` is a small .NET 10 service that polls one configured GitHub repository for ready Issues, asks the Codex CLI to implement them in a Git checkout, runs configured validation commands, and manages the Git and Issue lifecycle. It processes Issues sequentially and can optionally send Telegram notifications.

## V0.1 workflow

1. Read a project YAML file and validate its settings.
2. Ask `gh` for open Issues carrying the configured ready label, then claim the oldest by replacing that label with the working label.
3. Confirm the project checkout exists and is clean; check out and fast-forward the base branch; create a feature branch.
4. Run `codex exec` in the project checkout using `workspace-write`, automatic approval review, a JSON output schema, and a timeout.
5. For a blocked or failed result, update the Issue label and add a comment, then move on to the next Issue.
6. For success, run configured validation commands sequentially, commit the changes, and optionally merge and push the base branch. Optionally preserve the feature on origin using the completed branch prefix.
7. Mark the Issue done, comment with a summary, and close it after successful integration.
8. Continue immediately when another ready Issue exists; otherwise wait for the polling interval.

GitHub operations and Git lifecycle stay in the worker. Codex receives the Issue and project instructions, and is told not to manage Git or GitHub.

## Prerequisites

- .NET SDK 10
- Git
- GitHub CLI (`gh`), authenticated and able to access the configured repository
- Codex CLI (`codex`), installed and authenticated
- A local clone of the configured project repository

## Configuration

Start from [`config/project.example.yml`](config/project.example.yml). Copy it to your own YAML file and set the repository, local checkout directory, labels, base branch, Codex instructions file, and validation commands. Relative project directories are resolved from the YAML file location; a relative `codex.instructionsFile` is resolved from the project directory.

The loader supports the block mappings, scalar values, and block lists used by this configuration schema. It rejects unsupported YAML features such as anchors, flow collections, and block scalar values instead of silently interpreting them incorrectly.

All five GitHub labels must be distinct. Validation commands are generic shell commands and run sequentially in the project directory. No .NET-specific command is assumed.

The example uses `codex-ready`, `codex-working`, `codex-blocked`, `codex-failed`, and `codex-done`; create these labels in your repository or choose labels that already exist. The worker does not create labels.

Telegram credentials are never read from YAML. When `telegram.enabled` is true, set these environment variables:

- `TELEGRAM_BOT_TOKEN`
- `TELEGRAM_CHAT_ID`

The worker sends success, blocked, failed, and starting-Issue notifications. A missing credential disables Telegram delivery with a warning. Notification errors do not change task outcomes.

## Build and run

From this repository:

```sh
dotnet build CodexWorker.sln
dotnet test CodexWorker.sln
dotnet run --project src/CodexWorker -- /path/to/project.yml
```

Use Ctrl+C for graceful shutdown. If shutdown arrives while an Issue is claimed, the Issue stays labeled working so a human can inspect it before re-queueing it.

## Safety model

- The worker checks that the checkout is clean before starting each Issue and refuses unexpected Git history or branch changes.
- Git pull uses `--ff-only`; merge conflicts and other Git errors fail the Issue. The worker never force-pushes or attempts automatic conflict resolution.
- Validation failure prevents commit integration. Process output is bounded and failure comments are kept concise.
- Codex gets workspace-write access to the configured checkout and automatic approval review. It does not own issue labels, comments, closing, branches, commits, merges, or pushes.
- Validation commands are trusted project configuration and run as the worker's OS user from the project directory.
- GitHub and Codex are invoked as child processes. Issue selection and the worker loop are sequential.
- Project YAML is configuration, not a secrets store. Keep credentials in environment variables and local config out of Git.

## V0.1 limitations

One worker handles one project at a time. There is no distributed locking, concurrent processing, prioritization, dependency handling, retry policy, session recovery, managed worktrees, crash recovery, web UI, webhook, service manager setup, or auto-scaling. If a process stops after claiming an Issue, it remains in the working state for manual review. A Git failure can leave the checkout requiring manual cleanup before later Issues can be processed. A remote push can succeed before a later GitHub update fails, so inspect the branch and Issue together after partial failures.

## Development

See [`AGENTS.md`](AGENTS.md) for project-specific development boundaries. Automated tests cover configuration validation, branch slug generation, and Codex structured-result parsing without calling real GitHub or Codex services.
