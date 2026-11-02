# Server and Worker test consolidation (370.8.3 / #455)

Reviewed 2026-11-02 with .NET SDK 10.0.114 on Linux. The [Server audit](server-functionality-audit-370.8.1.md) and [Worker audit](worker-functionality-audit-370.8.2.md) are present in this checkout. This review inventories both solution test projects, reviews their test intents and ownership boundaries, and inspects candidate consolidation bodies and representative boundary tests. It does not claim exhaustive assertion-by-assertion verification or live-service validation.

## Scope and decisions

Server and Worker tests share `tests/CodexWorker.Tests`; there is no separate Server or Shared test project. Its references include both applications, which reference `src/Shared/CodexProvisioning.csproj`. The other solution test project is `tests/WorkExecutionToolbox.Tests`. The per-file inventory below accounts for every C# file containing Fact/Theory declarations in both projects. `FakeClaudeCli`, `IsolatedHttpsCertificates`, `TestCapabilityDiscovery` and `Usings` are support files, not additional suites.

| Reviewed area | Coverage retained and reason |
| --- | --- |
| Server host, configuration, CLI and dashboard delivery | `CodexServerTests`, `ServerAdministration*`, `ServerCommandHelpTests`, `ServerConfigurationMutationTests`, `DevelopmentDashboardAssetsTests`. Keep dispatch, offline/no-state behavior, configuration precedence, health/readiness and embedded asset contracts. Consolidate only repeated invalid dispatch calls described below. |
| Registry, projects, admission, assignments and leases | `CodexServerTests`, `AutomaticIssueDiscoveryTests`, `ServerOperationalDiagnosticsTests`. Keep optimistic revisions, atomic fleet/project limits, exact repository identity, generation/owner fencing, safe versus uncertain expiry and durable reporting. Consolidate one repeated project-creation fixture while retaining all its assertions. |
| Authorization and control transport | `AdministrationSessionTests`, `McpOAuthTests`, `ServerMcpTests`, `ServerEventStreamTests`, `ManagedHttpsSecurityTests`, `WorkerServerHttpTransportTests`, enrollment/registration/delivery tests. Session, management bearer, per-Worker API and delivery authority are distinct. Keep TLS/redirect matrices, expiry, revocation, lost acknowledgement and shutdown paths. |
| GitHub adapters, discovery and planning | `GitHub*Tests`, `ServerGitHub*Tests`, `ServerRepositoryDiscoveryTests`, `ManagedGitHubIssueDiscoveryTests`. Server planning/provider reads and Worker claiming/reporting are separate contracts; WET coverage does not replace adapter coverage. Remove one duplicate no-dependency case; correct the cancellation test's unexercised dependency path. |
| Configuration and scheduling | `ConfigurationTests`, `MultiProjectConfigurationTests`, `ProjectConfigurationManagementTests`, `ProjectRuntimeRegistryTests`, `ExecutionEligibilityTests`, `StartupCoordinatorTests`, `WorkerLifecycleTests`, `SchedulerCapacityLogTests`. Keep YAML strictness, atomic last-valid snapshots, active reservation/history guards, stale configuration, per-project isolation, capacity and drain coordination. |
| Managed ownership and readiness | `ManagedConfigurationTests`, `ManagedWorkerReadinessTests`, `ManagedCheckoutTests`, `AuthenticationReadinessTests`, Worker status/preparation tests. Registration, installation, authentication and execution readiness remain separate. Cached observations do not grant assignment authority or standalone scheduling. |
| Execution, engines and validation | `WorkerV011Tests`, `WorkerExecutionTests`, `ImplementationEngineTests`, Codex/Claude invocation, profile, result and validation tests. Keep structured task versus infrastructure outcomes, worker-owned Git lifecycle, sequential validation, repair bounds, shutdown and child credential isolation. |
| Integration, interruption and recovery | `GitWorktreeTests`, `StandaloneInterruptedExecutionTests`, `InterruptedWorkspaceProcessesTests`, `CodexInterruptionTests`, `CodexCapacityRetryTests`, `ExecutionHistoryStoreTests`, `LegacyCodexSessionTests`, all maintenance tests. Real Git tests and orchestration fakes protect different boundaries. Keep exact worktree/branch/base proof, integration uncertainty, attempt lineage, managed-history exclusion, concurrency and fresh authorization before cleanup. |
| Provisioning, credentials and maintenance | Node capability/setup, all provisioning, credential, update/restart, self-update, service context and journal tests. Keep legacy plan and typed-command protocols: both have supported callers/persisted state. Keep fixed actions/arguments, local policy, secret-free projections, deadlines, cancellation and at-most-once uncertain dispatch. |
| Shared contracts | `NodeCapabilityTests.ManagedWireContractRoundTripsInventoryWithoutChangingLegacySchedulerCapabilities`, `EngineStatusContractTests`, `IssueExecutionMetadataTests`, `SharedCredentialServicesTests`, `WorkerHostResourcesTests`, `ProvisioningCommandTests.WireProtocolRejectsCommandsAndSecretFields`, managed snapshot/enrollment/delivery tests. Keep wire compatibility, normalization, allowlists, scoped delivery and Server-to-Worker composition even when each application has its own boundary tests. |
| Presentation and diagnostics | Execution reports, generated message origin, issue context, quota, operability, Telegram and status tests. Keep redaction, bounded output, correlation and nonfatal notification failures independently of business outcomes. |
| WET library, provider, CLI and MCP | All 11 Toolbox test files below remain. Contract validation, pagination/graph limits, caching/invalidation, verified/uncertain writes, cancellation and presentation cover reusable behavior independently of Server/Worker adapters. |

Additional validation layers were inventoried: `src/CodexServer/worker-poc/tests/*.test.mjs`, `tests/dashboard/*`, top-level shell/Python checks and explicitly scoped managed/local end-to-end campaigns. They exercise UI, packaging/deployment and installed/live workflows rather than duplicating the .NET contract tests. No UI, packaging or campaign tests were removed or executed for these C# test-only edits. The immutable measurements in [28.2](test-suite-audit-28.2.md) and [28.3](test-runtime-profile-28.3.md) are historical, not this checkout's baseline. Validation policy and benchmark archives are unchanged.

## Cleanup coverage mapping

| Removed/merged group and original purpose | Why consolidation is justified | Meaningful coverage after cleanup |
| --- | --- | --- |
| `GitHubDependencyTests.ReadyIssueWithoutDependenciesIsEligible`: select a ready Issue with an empty dependency list | Same fixture input and `FindOldestReadyAsync` path as `DependencyLookupUsesRestApiAndIssueListingDoesNotRequestBlockedBy`; only the label string differs | The surviving test still asserts selected Issue 1 and additionally verifies the paginated REST dependency read, supported list projection and absence of mutation arguments. Closed, open, mixed and later-page dependency cases remain separate. |
| `CodexServerTests.CentralProjectsWithoutRequirementsRemainValidAndPersistAsAnEmptyList`: validate/create/read a project with null requirements | Repeated project CRUD setup at the same store boundary; its special null-input assertions can share the existing CRUD fixture | All three assertions move to `CentralProjectsSupportValidatedCrudDurabilityUniquenessAndRevisionConflicts`, after its deletion/empty-store assertion. Null requirements validate, the created project has an empty list and a fresh database read has an empty list. The read now explicitly asserts a non-null `CentralProject`. Nonempty normalization, restart durability, uniqueness and revision-conflict assertions remain unchanged. |
| Five invalid-operation calls in `ServerAdministrationDispatchTests.ProgramDispatchesAdministrationRootsToTheirHandlers`: dispatch unknown `github`, `credential`, `backup`, `worker-token`, `worker` operations | `ServerCommandHelpTests.UnknownOperationsPointToFamilyHelpWithoutEchoingArguments` already invokes the same `Program.Main` path for every one of these families, plus four others | The theory checks exit code 2, the correct family help and absence of the untrusted argument in diagnostics. The dispatch smoke test retains every successful read-only command and its handler-dispatch assertions. No theory rows or command families are removed. |

There are two fewer Fact methods/cases, five fewer repeated CLI invocations inside a surviving Fact, and one corrected regression test. No executable-case saving is claimed from source counts alone.

## Audit finding and focused correction

`GitHubDependencyTests.CancellationDuringDependencyLookupPropagatesSafely` was an exact whitespace-normalized duplicate of `GitHubCancellationTests.CancelledReadyIssueQueryPropagatesApplicationCancellation`: both pre-cancelled the token before calling ready-Issue lookup. `GitHubRetryPolicy` checks that token before invoking the process seam, so neither reached the dependency endpoint.

The dependency test now returns a valid Issue list, observes the exact dependency command, cancels at that command and returns a cancelled task. It asserts the caller token at both process calls and on the propagated exception, and exactly two commands (no retry or mutation). This requires no sleeps, external services or process launch. The pre-cancelled-query test and uncertain mutation-cancellation test remain unchanged. No production defect was established by this inspection; no production assertion was weakened to make a test pass.

The functionality audits' unresolved production follow-ups remain routed to those audit records (configuration filename identity/watcher event ownership, resource telemetry, drain presentation, conflict-resolution policy, Server clock/store/journal concerns). They are not grounds to delete regression tests or change production semantics in this cleanup. In particular, preserve `CentralProjectMutationReturnsItsOwnRevisionWhenTheStoredRowAdvances`, `CentralProjectMutationsRejectNonpositiveRevisionsWithoutChangingState`, `McpOAuthTests.BackupRemovesOAuthAuthorityAndRejectsArchivesThatRetainIt`, `ProjectRuntimeRegistryTests.ActiveCheckoutIdentityUsesPlatformPathComparison` and the two case-sensitive configuration-administration regressions added by the prerequisite audits.

## Before/after measurement and exact commands

Source inventory was captured before edits, then repeated after edits using this method from the repository root. Counts are declarations, not VSTest-discovered cases; MemberData expansion is deliberately not guessed.

```sh
python3 - <<'PY'
from pathlib import Path
import re
for project in ('CodexWorker.Tests', 'WorkExecutionToolbox.Tests'):
    rows = []
    for path in sorted(Path('tests', project).glob('*.cs')):
        text = path.read_text()
        counts = [len(re.findall(r'^\s*\[' + kind + r'(?:\]|\()', text, re.M))
                  for kind in ('Fact', 'Theory', 'InlineData', 'MemberData', 'ClassData')]
        if counts[0] + counts[1]:
            rows.append(counts)
            print(path.name, *counts)
    print(project, 'test files:', len(rows), 'totals:',
          [sum(row[i] for row in rows) for i in range(5)])
PY
```

| Source metric | Before | After |
| --- | ---: | ---: |
| Worker/Server test files | 116 | 116 |
| Worker/Server Facts | 867 | 865 |
| Worker/Server Theories | 310 | 310 |
| Worker/Server InlineData declarations | 1,171 | 1,171 |
| Worker/Server MemberData declarations | 5 | 5 |
| Toolbox test files | 11 | 11 |
| Toolbox Facts / Theories / InlineData | 95 / 52 / 178 | 95 / 52 / 178 |
| Total test methods | 1,324 | 1,322 |

Neither project uses ClassData. All five MemberData declarations remain unchanged, as do test traits and xUnit collection scheduling.

Initial baseline attempt (before edits):

```sh
dotnet test CodexWorker.sln -m:1 --logger 'console;verbosity=minimal' \
  --logger trx --results-directory /tmp/test-audit-370.8.3-before
```

Restore reported `NU1301`, `Permission denied (api.nuget.org:443)`. No compilation/test execution or TRX measurement was available. After this failed restore, the following identical measurement method was used before and after edits (substitute `before` and `after` for `PHASE`):

```sh
/usr/bin/time -f 'command_wall_seconds=%e exit=%x' \
  -o /tmp/test-audit-370.8.3-PHASE.time \
  dotnet test CodexWorker.sln --no-restore -m:1 \
  --logger 'console;verbosity=minimal' --logger trx \
  --results-directory /tmp/test-audit-370.8.3-PHASE \
  > /tmp/test-audit-370.8.3-PHASE.log 2>&1
```

| Measurement | Before | After |
| --- | --- | --- |
| Command wall time / exit | 1.09 s / 1 | 1.09 s / 1 |
| Result | NU1301, denied NuGet feed | NU1301, denied NuGet feed |
| Executed/passed test count | Unavailable | Unavailable |
| TRX test-session duration | Unavailable | Unavailable |

These wall times measure failed validation startup, **not test duration or performance improvement**. `--no-restore` still reports the unresolved restore errors recorded in project assets. No successful relevant-suite pass or runtime comparison is claimed. On a host with restored packages, build each state and compare with the same command below, excluding build time; sum each project's TRX `Times.finish - Times.start`, preserving normal collection scheduling:

```sh
dotnet build CodexWorker.sln --no-restore -m:1
dotnet test CodexWorker.sln --no-build --no-restore -m:1 \
  --logger 'console;verbosity=minimal' --logger trx \
  --results-directory /tmp/test-audit-370.8.3-PHASE-executed \
  --blame-hang-timeout 60s --blame-hang-dump-type none
```

`git diff --check` passed. Final source/diff review checked every removed assertion against the mapping, cancellation flow through `GitHubClient`/`GitHubRetryPolicy`, preserved prerequisite regressions, unchanged traits/collections and absence of production, version, validation-policy or configuration changes. Worker-configured checks remain the separate authoritative gate. Successful runtime counts/durations and a relevant-suite pass remain unverified because of the local package-feed restriction.

## Per-file source inventory

Paths are relative to each named test project. F/T/I/M mean Fact, Theory, InlineData and MemberData declarations. An arrow marks a changed Fact count. All files are retained; only the three files identified in the cleanup/finding sections have edits. The area table above records why the other coverage remains distinct.

### CodexWorker.Tests

| File | F before → after | T | I | M |
| --- | ---: | ---: | ---: | ---: |
| AdministrationSessionTests.cs | 2 | 3 | 9 | 0 |
| AuthenticationReadinessTests.cs | 7 | 1 | 2 | 0 |
| AutomaticIssueDiscoveryTests.cs | 14 | 4 | 17 | 0 |
| CapabilityInventoryTests.cs | 2 | 0 | 0 | 0 |
| ClaudeImplementationEngineTests.cs | 3 | 1 | 3 | 0 |
| ClaudeNativeLoginTests.cs | 9 | 3 | 13 | 0 |
| ClaudeProvisioningTests.cs | 2 | 5 | 19 | 0 |
| ClaudeReadinessTests.cs | 3 | 3 | 8 | 0 |
| CodexCapacityRetryTests.cs | 3 | 0 | 0 | 0 |
| CodexCliModelTests.cs | 1 | 1 | 4 | 0 |
| CodexExecutionProfileTests.cs | 7 | 2 | 15 | 0 |
| CodexExecutorArgumentsTests.cs | 4 | 0 | 0 | 0 |
| CodexGuidedAuthenticationTests.cs | 4 | 2 | 4 | 0 |
| CodexInterruptionTests.cs | 4 | 1 | 8 | 0 |
| CodexQuotaTests.cs | 6 | 7 | 17 | 0 |
| CodexResultParserTests.cs | 6 | 0 | 0 | 0 |
| CodexServerTests.cs | 71 → 70 | 13 | 35 | 0 |
| CodexServiceEnvironmentTests.cs | 8 | 3 | 9 | 0 |
| CodexTaskContractTests.cs | 3 | 0 | 0 | 0 |
| CommandLineTests.cs | 5 | 7 | 19 | 3 |
| ConfigurationTests.cs | 10 | 2 | 5 | 0 |
| CredentialAdministrationTests.cs | 2 | 2 | 4 | 0 |
| CredentialProvisioningTests.cs | 2 | 0 | 0 | 0 |
| DependencyGraphTests.cs | 1 | 0 | 0 | 0 |
| DevelopmentDashboardAssetsTests.cs | 1 | 0 | 0 | 0 |
| DockerProvisioningTests.cs | 4 | 2 | 7 | 0 |
| EngineStatusContractTests.cs | 3 | 0 | 0 | 0 |
| ExecutionEligibilityTests.cs | 4 | 4 | 12 | 0 |
| ExecutionHistoryStoreTests.cs | 20 | 9 | 27 | 0 |
| ExecutionMaintenanceCliTests.cs | 0 | 4 | 13 | 0 |
| ExecutionMaintenanceTests.cs | 6 | 3 | 17 | 0 |
| ExecutionReportTests.cs | 13 | 1 | 2 | 0 |
| ExpandedToolProvisioningTests.cs | 2 | 7 | 25 | 0 |
| GeneratedMessageOriginTests.cs | 2 | 4 | 11 | 0 |
| GitBranchTests.cs | 3 | 3 | 11 | 0 |
| GitHubCancellationTests.cs | 2 | 0 | 0 | 0 |
| GitHubDependencyTests.cs | 13 → 12 | 2 | 6 | 0 |
| GitHubDeviceLoginTests.cs | 7 | 4 | 16 | 0 |
| GitHubIssueGraphTests.cs | 11 | 0 | 0 | 0 |
| GitHubIssueReadProviderTests.cs | 17 | 4 | 14 | 0 |
| GitHubLabelTests.cs | 6 | 0 | 0 | 0 |
| GitHubOperationTests.cs | 9 | 4 | 10 | 0 |
| GitHubRetryPolicyTests.cs | 1 | 2 | 4 | 0 |
| GitHubToolboxHostTests.cs | 2 | 1 | 2 | 0 |
| GitWorktreeTests.cs | 44 | 17 | 94 | 0 |
| ImplementationEngineTests.cs | 3 | 0 | 0 | 0 |
| InstallerGitIdentityTests.cs | 1 | 0 | 0 | 0 |
| InterruptedWorkspaceProcessesTests.cs | 0 | 2 | 8 | 0 |
| IssueCommentContextTests.cs | 11 | 3 | 10 | 0 |
| IssueExecutionMetadataTests.cs | 2 | 2 | 17 | 0 |
| LegacyCodexSessionTests.cs | 0 | 1 | 15 | 0 |
| ManagedCheckoutTests.cs | 9 | 3 | 8 | 0 |
| ManagedConfigurationTests.cs | 20 | 3 | 7 | 0 |
| ManagedExecutionLogTests.cs | 1 | 0 | 0 | 0 |
| ManagedExecutionMaintenanceTests.cs | 4 | 3 | 6 | 0 |
| ManagedGitHubIssueDiscoveryTests.cs | 4 | 4 | 18 | 0 |
| ManagedHttpsSecurityTests.cs | 0 | 1 | 4 | 0 |
| ManagedWorkerReadinessTests.cs | 3 | 7 | 19 | 0 |
| ManagementApiTests.cs | 7 | 1 | 3 | 0 |
| McpOAuthTests.cs | 6 | 1 | 7 | 0 |
| MultiProjectConfigurationTests.cs | 14 | 4 | 9 | 0 |
| NodeCapabilityTests.cs | 12 | 1 | 2 | 0 |
| NodeGitHubSetupTests.cs | 10 | 2 | 12 | 0 |
| OperabilityTests.cs | 31 | 0 | 0 | 0 |
| ProcessRunnerTests.cs | 1 | 1 | 2 | 0 |
| ProjectConfigurationManagementTests.cs | 8 | 0 | 0 | 0 |
| ProjectEnvironmentTests.cs | 6 | 1 | 4 | 0 |
| ProjectRuntimeRegistryTests.cs | 11 | 2 | 4 | 0 |
| ProvisioningCliTests.cs | 11 | 4 | 12 | 0 |
| ProvisioningCommandTests.cs | 14 | 1 | 6 | 0 |
| ProvisioningPlanExecutorTests.cs | 15 | 1 | 4 | 0 |
| RecoveryDiagnosticsCliTests.cs | 1 | 0 | 0 | 0 |
| SchedulerCapacityLogTests.cs | 10 | 0 | 0 | 0 |
| SelfUpdateTests.cs | 4 | 5 | 13 | 0 |
| ServerAdministrationDispatchTests.cs | 2 | 0 | 0 | 0 |
| ServerAdministrationTests.cs | 8 | 0 | 0 | 0 |
| ServerCommandHelpTests.cs | 5 | 4 | 23 | 1 |
| ServerConfigurationMutationTests.cs | 3 | 1 | 5 | 0 |
| ServerEventStreamTests.cs | 2 | 1 | 6 | 0 |
| ServerGitHubAdministrationTests.cs | 22 | 9 | 30 | 0 |
| ServerGitHubEnvironmentTests.cs | 3 | 2 | 8 | 0 |
| ServerMcpTests.cs | 1 | 0 | 0 | 0 |
| ServerOperationalDiagnosticsTests.cs | 7 | 3 | 16 | 0 |
| ServerProvisioningAdministrationTests.cs | 6 | 0 | 0 | 0 |
| ServerRepositoryDiscoveryTests.cs | 3 | 2 | 5 | 0 |
| SharedCredentialServicesTests.cs | 3 | 2 | 12 | 0 |
| StandaloneExecutionMaintenanceTests.cs | 2 | 1 | 2 | 0 |
| StandaloneInterruptedExecutionTests.cs | 11 | 6 | 43 | 0 |
| StartupCoordinatorTests.cs | 9 | 1 | 4 | 0 |
| TelegramDeliveryTests.cs | 1 | 3 | 7 | 0 |
| ToolProvisioningLifecycleTests.cs | 3 | 6 | 36 | 0 |
| ValidationRepairContractTests.cs | 4 | 1 | 2 | 0 |
| ValidationReportTests.cs | 4 | 1 | 3 | 0 |
| WorkerAdministrationCliTests.cs | 8 | 1 | 3 | 0 |
| WorkerAdministrationIntegrationTests.cs | 0 | 2 | 12 | 0 |
| WorkerAuthenticationContextTests.cs | 0 | 1 | 2 | 0 |
| WorkerCapabilityDiscoveryTests.cs | 4 | 1 | 3 | 0 |
| WorkerConfigurationAdministrationTests.cs | 10 | 1 | 3 | 0 |
| WorkerCredentialCliTests.cs | 7 | 5 | 14 | 0 |
| WorkerDeliveryAuthorizationTests.cs | 2 | 1 | 2 | 0 |
| WorkerEnrollmentTests.cs | 11 | 2 | 9 | 0 |
| WorkerExecutionTests.cs | 6 | 1 | 4 | 0 |
| WorkerHostResourcesTests.cs | 4 | 1 | 4 | 0 |
| WorkerJournalTests.cs | 5 | 2 | 6 | 0 |
| WorkerLifecycleTests.cs | 6 | 0 | 0 | 0 |
| WorkerPreparationTests.cs | 3 | 2 | 13 | 0 |
| WorkerProvisioningTests.cs | 3 | 2 | 5 | 0 |
| WorkerRegistrationCliTests.cs | 4 | 6 | 20 | 0 |
| WorkerRegistrationTests.cs | 21 | 6 | 19 | 0 |
| WorkerRestartCliTests.cs | 5 | 0 | 0 | 0 |
| WorkerServerHttpTransportTests.cs | 1 | 6 | 19 | 1 |
| WorkerServiceAdministrationContextTests.cs | 2 | 3 | 16 | 0 |
| WorkerStatusTests.cs | 14 | 1 | 5 | 0 |
| WorkerTelegramSettingsTests.cs | 5 | 1 | 4 | 0 |
| WorkerUpdateTests.cs | 12 | 10 | 45 | 0 |
| WorkerV011Tests.cs | 67 | 28 | 109 | 0 |

### WorkExecutionToolbox.Tests

| File | F before → after | T | I | M |
| --- | ---: | ---: | ---: | ---: |
| GitHubIssueCacheTests.cs | 9 | 6 | 20 | 0 |
| GitHubIssueDependencyTests.cs | 12 | 4 | 8 | 0 |
| GitHubIssueInspectionTests.cs | 8 | 9 | 28 | 0 |
| GitHubIssueParentTests.cs | 16 | 8 | 24 | 0 |
| GitHubIssueProviderTests.cs | 6 | 5 | 18 | 0 |
| GitHubIssueRootTests.cs | 14 | 7 | 18 | 0 |
| GraphPresentationTests.cs | 8 | 0 | 0 | 0 |
| IssueContractTests.cs | 4 | 2 | 5 | 0 |
| ToolboxCommandTests.cs | 9 | 6 | 40 | 0 |
| ToolboxDiscoveryTests.cs | 5 | 5 | 17 | 0 |
| ToolboxMcpTests.cs | 4 | 0 | 0 | 0 |

