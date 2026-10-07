# Codex interruption recovery

The Worker remains online when a Codex execution loses a recognized external prerequisite. Usage/rate/subscription limits, authentication/session failures, service/network errors and execution timeouts park the implementation rather than restarting the Worker. Unknown process failures retain their workspace and sanitized diagnostics for inspection; an exit code alone does not authorize automatic recovery. Task outcomes, explicit cancellation, uncertain Git/GitHub mutations and integration recovery retain their existing semantics.

Healthy sibling executions continue. Requested Worker shutdown preserves a verified implementation/repair workspace as an interrupted continuation. A restart reconciles incomplete implementation history against its registered worktree before scheduling. It never invents missing legacy ownership, intent, profile or base metadata.

## Readiness and budgets

Unhealthy execution preflight is automatically rechecked after five minutes, backing off to ten, twenty and forty minutes on consecutive failures. Capability observations and explicit provisioning refresh can trigger an earlier check. Status exposes a sanitized reason and the next automatic preflight time. Authentication requires the supported node authentication flow; missing or broken CLI installation requires provisioning/repair. Preflight uses one bounded ephemeral invocation and performs no project work.

Each interrupted lineage permits at most three automatic Codex resumes. The count is consumed transactionally before launch and survives deployment/restart. An interruption also records a five-minute earliest resume time, so restarting cannot immediately repeat implementation invocations. Unhealthy readiness checks consume no implementation or validation repair budget. Validation repair lineage and consumed attempts are carried into continuation.

## Continuation and repository safety

Recovery is considered before new standalone Issues and runs within existing global/project capacity. It keeps original Issue intent, effective model/effort, workspace identity, session UUID when supplied, starting commit and configuration fingerprint. Configuration changes require inspection instead of silently substituting different execution settings. Environment secrets are never copied into the recovery snapshot.

The CLI's local help must establish support for explicit session resume with the Worker's existing schema, JSON events and approval/sandbox contract. The Worker never uses `--last`. When that contract or session is unavailable, it starts a new invocation on the same verified worktree, explicitly instructing Codex to inspect existing changes and continue the original Issue. It does not copy partial changes into a new implementation workspace or remove the source workspace. A resume error other than an explicit missing-session response remains a failure, not an automatic fallback loop.

Completion follows ordinary authoritative validation, repair, rebase/revalidation and integration. Advancing the configured base while waiting does not overwrite newer work. Git history mutation, unfinished Git operations, missing registration, branch/HEAD mismatches or incomplete metadata stop automatic recovery safely.

Managed recovery is offered through the existing preserved-work assignment protocol with kind `Codex`. The Server verifies the original node/execution identity, outcome and competing reservations, then supplies a fresh assignment/lease. If a pre-integration lease expired and its retry is still queued, the returning owner may attach verified work to that queued retry. It cannot take back an assigned/completed execution. Expired preserved continuations wait for their original node; cached configuration and an expired lease confer no execution authority.

## Operator evidence

Execution history retains interruption reason, lineage, changed-path summary, session UUID, configuration fingerprint and resume count. GitHub receives a concise interruption/recovery comment, without arbitrary raw Codex output; the Issue does not need a new ready label for continuation. Status/journal report degraded readiness and restored execution readiness. Pending or exhausted interruption resources remain protected from automatic cleanup.

Legacy incident worktrees can only resume automatically when their persisted identity, original intent, profile/configuration and base are sufficient. Older `uncertain` rows without those snapshots remain preserved and report that inspection is required. No incident Issue or execution ID is special-cased. Real legacy E2E recovery is deployment validation against the owning Worker's durable history and worktrees, outside an implementation task.
