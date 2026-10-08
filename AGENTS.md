# Codex Worker Engineering Guidelines

This document is the authoritative repository engineering guidance for Codex executions. Codex Worker is a generic .NET Server/Worker system; keep it project-agnostic. Do not hard-code application-specific repositories, labels, commands, or assumptions.

## Architecture and ownership

Before implementing, inspect the existing architecture, relevant contracts, representative code and tests. Extend the existing architecture rather than creating a parallel solution.

- **Server is the control plane** (`src/CodexServer`): it owns the persistent registry, managed project definitions/revisions, assignments and leases, provisioning coordination, and credential metadata/delivery authorization.
- **Worker is an execution agent** (`src/CodexWorker`): it applies managed configuration and assignments, executes work, and reports results. Keep only necessary operational/local state here: identity, machine-specific configuration, validated snapshot cache, execution history and recovery resources. A cached snapshot does not authorize standalone scheduling during a Server outage. Preserve explicit standalone ownership mode where supported.
- Reuse existing domain concepts and contracts before adding types or stores. Do not duplicate equivalent Server/Worker concepts; node capability and provisioning protocol concepts shared by both belong in the existing `src/Shared` project (`CodexProvisioning`). Share actual common behaviour without merging distinct ownership responsibilities.
- Keep scheduling, execution, Git/GitHub integration, recovery, provisioning and presentation boundaries explicit. `WorkerHost` coordinates project scheduling/capacity; `ExecutionRunner` owns the per-execution lifecycle; `GitRepository` owns repository mutations; `GitHubClient` and worker orchestration own Issue claiming/reporting. APIs and dashboards present these states rather than inventing independent lifecycle rules.
- Prefer narrow, cohesive responsibilities over growing god services/classes. Avoid speculative abstractions, new persistence/ownership mechanisms or alternative workflows for hypothetical future needs. Preserve established naming, namespace, layout and dependency conventions.
- The host supports bounded concurrency under global and per-project limits. Implementation/validation run in independent execution worktrees; shared Git mutation is serialized by repository-scoped gates. Do not replace this with global mutable current-project state or bypass capacity/ownership checks.
- Codex implements task files only. Do not delegate branch creation, commits, integration, push, or GitHub labels/comments/Issue state to Codex; those remain worker-owned lifecycle operations.

## Dashboard UI

- When changing administration-dashboard UI, follow the [dashboard design guide](docs/dashboard-design-guide.md). Use the established React + Untitled UI foundation and shared components; the guide defines screen composition, interaction, responsive and accessibility expectations.
- Treat the approved [Worker detail](docs/mockups/worker-detail-approved.jpg), [Home](docs/mockups/home-summary.svg), and [Workers list](docs/mockups/workers-list-dark-reference.svg) files as visual targets. Draft mockups in `docs/mockups` are for owner review; do not treat them as approved implementation requirements until approved. Compare rendered screens with their approved references; prose requirements alone do not replace visual comparison.

## .NET and code quality

- Target the repository's current .NET version (currently .NET 10 / `net10.0`) and retain nullable reference types. Model optional values explicitly, validate boundary inputs and avoid null-forgiving operators that hide missing invariants.
- Use async for I/O and propagate the appropriate `CancellationToken` through HTTP, database, process, delay and gate operations. Avoid blocking async calls, fire-and-forget lifecycle work, and unbounded waits. Deliberately uncancellable durable recording/cleanup must be narrow and justified by recovery semantics.
- Use modern C# when it improves clarity; prefer clear, explicit code over cleverness. Keep methods/classes cohesive, remove duplicated flows rather than copying near-identical logic, and avoid unnecessary global/static mutable state. Follow existing synchronization and dependency seams.
- Do not add compatibility hacks unless an explicit requirement or supported persisted/protocol format needs them. Keep compatibility checks at the relevant boundary and verify their behaviour.
- Authoritative build/test validation must pass without compiler or analyzer warnings introduced by the change, including production/test compiler, .NET and xUnit analyzers. `Directory.Build.props` treats these warnings as errors. Fix causes rather than suppressing diagnostics unless the repository explicitly documents an accepted warning.
- Pass process arguments as argument lists, except for explicitly configured validation shell commands. Preserve bounded output, timeouts and useful failure context.
- YAML uses YamlDotNet: reject duplicate and unknown keys, validate configuration before applying it, and update checked-in examples when the configuration contract changes. Apply managed snapshots atomically and retain the last valid snapshot on invalid updates.

## Errors, lifecycle and diagnostics

- Expected operational states (unavailable capability, pending work, drain, blocked task, rejected request) are not inherently exceptions. Use the existing state/result contracts; reserve exceptions for violated invariants or failures that callers must handle.
- Cancellation is not automatically infrastructure failure, requested Worker shutdown is not a crash, and execution failure is not Worker failure. Preserve distinct cancellation, timeout, shutdown, task and infrastructure semantics through reporting and exit behaviour.
- Keep structured Codex outcomes and exhausted authoritative validation repairs separate from `WorkerInfrastructureException`. Safe task failures release capacity without cancelling unrelated work. Codex execution/authentication failures and uncertain Git/GitHub state stop new scheduling and preserve state; do not relabel them as ordinary task failures to advance the queue.
- Project-specific startup failures can make that project unavailable while healthy projects initialize. Preserve global Codex availability/authentication preflight before Issue queue access; registration, capability discovery and process startup alone do not establish execution readiness. Do not add Codex service retries.
- Do not swallow exceptions or lose actionable diagnostics. Handle known failures at their owning boundary, retain useful context and distinguish nonfatal notification errors from execution failures.
- Keep diagnostics bounded, concise, redacted and correlated with execution IDs. GitHub failure comments need separated Markdown headings, paragraphs and recovery lists; do not repeat explanations or include raw Codex output. Persist useful outcomes/history without raw process logs or environment contents.
- Stop scheduling on shutdown/drain according to the existing lifecycle, cancel/join child work where appropriate, and preserve interrupted or uncertain resources for reconciliation. Do not perform destructive cleanup while useful state may require recovery.

## Git, validation and recovery

- Keep the canonical checkout dedicated to one Worker and initially verified/clean on its configured base branch. Task edits belong in the isolated feature branch/worktree owned by each execution. Never let independent executions edit the same workspace or bypass repository gates.
- Extend `GitRepository`, execution history and existing recovery contracts; do not invent an alternative Git workflow inside a feature. Verify execution identity, registered worktree, branch and starting/base commit before resume or destructive cleanup. Preserve useful commits/worktrees and uncertain state; never discard work merely to make the next run start clean.
- Validation commands come only from project YAML and run sequentially within each execution. Worker-run validation is authoritative before integration; optional Codex self-validation does not replace it. Repair attempts are configuration-bounded.
- Integration must respect cancellation and, in managed mode, current lease ownership/generation before mutation. Cached configuration or a lost Server response must not grant authority. Rebase against the current authoritative base and validate rebased work before integration.
- Retain the established bounded post-rebase validation retry against unchanged source. Do not turn conflicts, uncertain push/integration results or authentication errors into generic retry loops. Use verified integration recovery and preserve attempt lineage/history; do not automatically rerun implementation or resolve merge conflicts. Never force-push.
- Recovery retention/cleanup must verify ownership metadata and preserve execution history. Retry/resume, reconciliation and capacity release must follow the Worker's lifecycle, including distinguishing safe failures from uncertain integration outcomes.

## Security and provisioning

- Never log credentials, tokens, private keys or authentication material, including in exceptions, process output, task prompts, reports or dashboards. Keep secrets out of YAML, examples, tests and source control. Read deployment secrets from environment variables or the existing authorized credential-storage/delivery boundary; expose metadata separately from secret payloads.
- Use least privilege for service accounts, filesystem permissions, API authorization and elevation. Preserve per-Worker credential assignment/delivery checks and node-local authorization policy; Server requests do not override local permission to provision.
- Validate untrusted/external inputs at boundaries: API/protocol payloads, repository identifiers, paths, configuration, persisted recovery metadata and process results. Retain existing bounds and rejection of unsupported/unknown inputs.
- Provisioning/control-plane features must not become arbitrary remote shell execution. Reuse allowlisted capability/action contracts and product-owned executable/argument lists; do not accept free-form shell text, package names or executable paths from remote callers. Preserve deadlines, cancellation and uncertain-operation reconciliation rather than blindly replaying commands.
- Keep private node credentials on the node where the architecture requires it (for example node-local private keys and CLI authentication state). Do not copy them into the registry, reports or backups; centrally managed credentials must use the existing protected storage and authorized delivery flow.
- Keep Codex's child environment stripped of GitHub authentication and normal Git credential-helper configuration. Local Git remains a trusted execution boundary; retain Git state verification and do not expose credentials through Codex-visible content.

## Testing and validation

- Test observable behaviour and contracts rather than private implementation details. Regression fixes require focused regression coverage; exercise failure, cancellation, shutdown, ownership and recovery paths when they are part of the feature.
- Keep automated tests deterministic and independent of external services. Use existing fakes/dependency seams, temporary local repositories/databases and stubbed process/service commands; reserve live GitHub/Codex, publishing and real-node campaigns for explicitly scoped integration validation.
- Avoid unnecessary real wall-clock dependencies. Use existing `TimeProvider`/clock seams and explicit coordination for timing/concurrency. Do not fix flaky tests by simply increasing sleeps/timeouts; fix the synchronization or timing assumption.
- Run useful local validation appropriate to the change and the required build/test checks when available. Record environment restrictions accurately; never claim the worker's separately configured authoritative checks passed based on Codex self-validation.
- Passing tests is necessary but does not excuse poor architectural fit, duplicated abstractions, weakened security or incorrect lifecycle semantics. Documentation-only changes need consistency/diff review, not artificial tests that mirror document wording.

## Product versions and releases

- Roadmap/Issue series are not product versions. Working on 16.x does **not** make the product version `0.16.0`; that becomes the product version only through the explicit release/version process. Until the v0.16 release is prepared, the current intended version remains `0.15.0`.
- Never infer or bump a product version from an Issue number, branch name or milestone. Unrelated feature/fix Issues must not silently change versions, assembly metadata, banners or release configuration.
- `Directory.Build.props` defines the shared source `Version`; Server and Worker derive product metadata from generated assembly metadata. Respect [release tooling and policy](docs/release-packaging.md#product-version-policy): explicit release preparation changes the source version, while `packaging/release.sh VERSION` and `packaging/release-linux-x64.sh --set-version VERSION` override the build version without editing it. Do not introduce independent version constants.

## Final self-review

Before declaring implementation complete, review your own diff concisely:

1. Verify requested behaviour and correctness, including lifecycle/failure semantics.
2. Verify required tests/validation and state any unavailable checks accurately.
3. Check architectural fit and ownership boundaries against the existing code.
4. Remove unnecessary complexity, duplication and parallel abstractions.
5. Verify compliance with this `AGENTS.md`, including security and recovery rules.
6. Check that unrelated behaviour, versioning and configuration were not changed accidentally.

This is a review step, not a reason to rerun the entire validation pipeline repeatedly. Keep the change focused; do not refactor unrelated production code while documenting or implementing a feature.
