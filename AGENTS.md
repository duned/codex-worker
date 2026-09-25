# Codex Worker Development Notes

This repository contains the generic .NET 10 Codex worker. Keep it project-agnostic: do not add application-specific repositories, labels, commands, or assumptions.

## Design boundaries

- GitHub queue and Issue state changes belong to `GitHubClient` and the worker orchestration. Codex must not receive GitHub lifecycle work.
- Git branch creation, commit, integration, and push belong to `GitRepository`.
- Keep task failures (`TaskFailureException` and explicit blocked outcomes) separate from infrastructure failures (`WorkerInfrastructureException`). Only a safe task failure may advance the queue; uncertain Git/GitHub state must stop it.
- Validation commands come only from project YAML and must run sequentially.
- The configured checkout is dedicated to one worker and must not be edited concurrently. Never add cleanup that discards changes without checking the worker branch and starting commit; preserve state on infrastructure failures.
- Secrets are read from environment variables. Never add credentials to YAML examples, tests, or source control.
- Keep the V0.1 single-project, single-worker polling model. Avoid adding database, webhooks, concurrency, retries, worktrees, or recovery machinery without an explicit versioned requirement.

## Implementation practices

- Use .NET 10 and nullable reference types.
- Pass process arguments as argument lists rather than building shell command strings, except for validation commands that are explicitly configured shell commands.
- Keep external-service access out of automated tests. Test parsing and deterministic decision logic locally.
- Preserve bounded process output and useful failure context.
- Keep the Codex child environment stripped of GitHub authentication and normal Git credential-helper configuration. Treat the remaining ability to use local Git as a trusted V0.1 boundary and retain Git state verification.
- Never force-push or automatically resolve merge conflicts.
- YAML configuration uses YamlDotNet; reject duplicate and unknown keys and keep example configuration checked in.
