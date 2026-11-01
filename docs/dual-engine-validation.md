# Dual-engine regression evidence

This is the validation map for the dual-engine operator runbook. Tests are
deterministic and offline; they do not use a real Claude/Codex service or GitHub.
The Worker's project-configured checks remain the authoritative gate.

| Boundary | Automated coverage |
| --- | --- |
| Native CLI process | `FakeClaudeCli` launches a temporary shell script through `ProcessRunner`; version/help, auth/readiness, stdin, arguments, file edits and JSON envelope cross a real process boundary. Linux-only cases return early on other platforms. A coordinated running-process cancellation case verifies edited files survive child termination. The fake never calls a provider. |
| Standalone CCC lifecycle | `WorkerV011Tests.ClaudeProcessUsesAuthoritativeValidationAndBoundedRepairs`: parsing, CLI execution, validation failure followed by success or repair exhaustion, Worker-owned integration, no Codex fallback. Git, GitHub and validation use existing fakes. |
| Managed assignment and reporting | `QueuedServerWorkRunsThroughWorkerPipelineAndReportsTerminalState`: both engines, real local Server registry and Worker history, compatible assignment/lease, validation repair, integration and terminal Server report. |
| Missing/unready/stale CCC | `AutomaticIssueDiscoveryTests.CccSelectionPersistsAndRequiresMatchingProviderBeforeAssignment`: persisted selection, absent provider, unready observations, observation without provider, stale heartbeat and refresh before assignment. `ClaudeReadinessTests` verifies missing/incompatible/auth/service states and cancellation; `WorkerStatusTests` verifies optional missing Claude leaves Codex status available. |
| Provisioning and local policy | `ClaudeProvisioningTests`, `ProvisioningCommandTests`, `WorkerCredentialCliTests`: native stable install/update with no elevation, allowlist/policy denial, separate installation/PATH/auth observations, cancellation/deadlines, refresh and durable command reporting. CLI credentials remain node-local. |
| Credential authorization | `CredentialProvisioningTests` checks unassigned/unknown Workers, rotation/revocation and persistence; `WorkerCredentialCliTests` covers local Codex policy denial and unsupported Claude login rejection without launching a credential provider. |
| Infrastructure uncertainty | `ClaudeCliFailurePreservesWorkspaceAndNeverFallsBack`: standalone and managed auth rejection, permission-denied envelope and malformed response; retained edited files/history, no validation/integration/cleanup, no raw private diagnostic in exception/history/console/GitHub comments. |
| Cancellation and recovery | `AssignmentCancellationBeforeIntegrationStopsAtTheSharedRepositoryBoundary` and `RestartReconcilesCleanClaimButPreservesUncertainWorkspace`: both engines, cancellation before mutation, clean/uncertain recovery claims and retained engine identity. Existing Git/recovery suites verify actual local Git identity and destructive-operation guards. |
| Metadata, persistence and legacy defaults | `IssueExecutionMetadataTests`, `ImplementationEngineTests`, `ExecutionHistoryStoreTests`, `ExecutionReportTests`, `EngineStatusContractTests`: invalid keys/values/conflicts, default Codex with CCC absent, no fallback, engine migration/freeze/reopen and report labels. |

Useful focused self-validation (run sequentially):

```sh
dotnet build tests/CodexWorker.Tests/CodexWorker.Tests.csproj -m:1
dotnet test tests/CodexWorker.Tests/CodexWorker.Tests.csproj --no-build --no-restore -m:1 --filter 'FullyQualifiedName~Claude|FullyQualifiedName~WorkerV011Tests|FullyQualifiedName~AutomaticIssueDiscoveryTests|FullyQualifiedName~IssueExecutionMetadataTests|FullyQualifiedName~ImplementationEngineTests|FullyQualifiedName~ExecutionHistoryStoreTests|FullyQualifiedName~ExecutionReportTests|FullyQualifiedName~EngineStatusContractTests|FullyQualifiedName~CredentialProvisioningTests|FullyQualifiedName~WorkerCredentialCliTests|FullyQualifiedName~ManagedWorkerReadinessTests|FullyQualifiedName~ProvisioningCommandTests|FullyQualifiedName~WorkerStatusTests'
```

`tests/worker-local-e2e.py` is an additional built-apphost administration campaign
with isolated HOME/PATH and no credentials. Its missing-tool inventory includes
optional `claude-cli`. It is separate from the lifecycle tests above.
`tests/managed-worker-e2e.py` is an **opt-in live** campaign requiring approved
Server/GitHub resources; do not run it as deterministic automated evidence.

## Parent final-integration handoff

Record actual local build/test outcomes in the task result, including environment
restrictions. Do not infer passing results from this coverage map. Fake flags and
responses validate our contract only. Vendor compatibility, commercial account
access, service environment and actual confinement need the optional
[operator-initiated live checks](dual-engine-operations.md#automated-evidence-and-optional-live-acceptance).
Those real-node, real-credential checks were not run as part of this change.

## Local results (2026-11-01)

- Worker build passed with zero warnings/errors.
- Backend test-project build passed with zero warnings/errors using a temporary
  MSBuild override outside the repository to skip dashboard generation. A normal
  dashboard build was interrupted during npm restore; an offline restore failed
  because the npm cache lacked `zustand-5.0.11.tgz`. No dashboard
  production/build configuration was changed.
- The focused changed dual-engine selection passed all 45 tests. The test runner
  needed loopback socket access outside the restricted sandbox.
- The wider lifecycle/assignment/history/credential/status regression selection
  passed all 360 tests.
- The built-Worker local administration campaign passed, including isolated
  missing-tool inventory, provisioning denial, fake enrollment verification and
  degraded startup/shutdown/restart. Its local fake HTTP server also needed
  loopback socket access.
- Python syntax, local documentation link targets and diff whitespace checks
  passed. No live Claude/Codex/GitHub campaign was executed.

These are Codex self-validation results, not the Worker's authoritative checks.
