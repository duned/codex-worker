# Server-authoritative managed project lifecycle integration

This review joins the managed configuration, cold-start, materialization,
eligibility, diagnostics and regression work in Issues #218–#223. The roadmap
series does not change the product version. The deployment acceptance procedure
remains the [managed Worker campaign](managed-worker-e2e.md); this review does not
claim a completed live VM campaign.

## Lifecycle and ownership

| Boundary | Integrated behavior | Regression coverage |
| --- | --- | --- |
| Server project catalog | Registry projects own identity, repository, default branch, requirements, enabled state and revision. Server returns the versioned catalog; Worker validates it before deriving runtime configuration. | `ManagedConfigurationTests`, `CodexServerTests` |
| Clean Worker startup | Managed discovery ignores local project YAML. Registration, capability inventory, heartbeat and management remain available without checkouts; execution readiness still requires authenticated agent preflight and local execution dependencies. | `ManagedWorkerReadinessTests`, `MultiProjectConfigurationTests` |
| Eligibility and assignment | Server evaluates project requirements against Worker capabilities and inventory. Worker advertises bounded project capacity and checks assignment eligibility again. Repository access probes are read-only and do not clone. | `ExecutionEligibilityTests`, `ManagedCheckoutTests`, `ManagedWorkerReadinessTests` |
| Materialization | Assignment lease renewal starts before preparation. Under the existing repository gate, `GitRepository` publishes a validated staged clone at a stable path derived from Server project ID. Existing checkouts are verified and reused, never replaced to hide conflicts or dirty state. | `ManagedCheckoutTests` |
| Revision fencing | Assignment must match the synchronized definition. Fresh Server catalogs are fully validated before and after preparation; removed, disabled or changed definitions cannot start execution. Invalid catalogs cannot authorize a clone. | `WorkerRegistrationTests`, `ManagedWorkerReadinessTests` |
| Execution and recovery | Preparation reuses Worker initialization and recovery reconciliation. `ExecutionRunner` retains isolated execution worktrees, authoritative validation, repository gates and lease cancellation before integration. History and uncertain recovery resources remain Worker-owned. | `GitWorktreeTests`, `WorkerV011Tests`, `WorkerLifecycleTests` |
| Failure and diagnostics | Expected preparation rejection reports the affected assignment failed and makes that project unavailable while the host stays alive. Retrieval, contract validation and synchronization have separate diagnostics; project capability blocking and preparation observations carry project ID and revision. | `ManagedConfigurationTests`, `ManagedWorkerReadinessTests`, `WorkerStatusTests` |
| Restart and standalone | Cached snapshots are validated derived state and never grant offline scheduling authority. Restart reuses verified checkouts. Standalone keeps local YAML discovery and preparation; managed cache does not populate its catalog. | `ManagedCheckoutTests`, `ManagedWorkerReadinessTests`, `MultiProjectConfigurationTests` |

Machine paths, authentication, execution defaults, validation commands and local
provisioning authorization remain node-local. Synchronization produces in-memory
runtime configurations and a protected catalog cache, without generating project
YAML or creating checkout directories. Runtime replacement waits for idle
executions; repository/branch changes reuse the existing ownership checks and
can require operator reconciliation rather than discarding retained resources.

The integration review closed an assignment verification gap: checking just the
selected project's hash did not validate the fresh catalog as a whole or check
its enabled state. Verification now reuses the synchronization contract validator
without applying that catalog beneath active executions. Regressions cover an
invalid snapshot version, duplicate project IDs and a disabled project after
assignment, asserting failure before materialization and a surviving host.

## Validation

Run the repository solution tests and the deterministic checks in
[the campaign runbook](managed-worker-e2e.md#deterministic-local-regression-checks).
They use stubbed service/process probes, temporary SQLite databases and local Git
repositories. The VM campaign separately verifies installed authentication,
successful execution and restart/revision behavior; it must never require manual
project YAML creation or repository cloning.
