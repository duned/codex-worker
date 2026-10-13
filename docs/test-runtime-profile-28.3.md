# Validation runtime profile (28.3)

## Scope and reproducibility

Measured on 2026-10-13 on one Ubuntu 24.04 x64 host with two logical CPUs (AMD EPYC 7763), .NET SDK 10.0.112/runtime 10.0.12, VSTest 18.0.2, Debug/net10.0. Both solution test projects use xUnit 2.9.3, Microsoft.NET.Test.Sdk 17.14.1 and the Visual Studio adapter 3.1.4. MSBuild projects ran sequentially; existing xUnit collection scheduling was retained, including the nonparallel `ServerTokenEnvironment` collection.

Inspection found **no `scripts/validate.sh` and no checked-in CI workflow**. [Development](development.md) documents solution restore/build/test; the example project YAML configures `dotnet test`. The actual deployed Worker's project YAML is outside this checkout and was not inspected. These measurements decompose the documented solution gate, not an invented replacement script or a claim about the deployed authoritative command.

One full baseline ran before adding metadata. Subsequent execution used focused filters only. Commands, all on the same host:

```sh
/usr/bin/time -p dotnet restore CodexWorker.sln -m:1 --disable-parallel
# Fresh checkout: no pre-existing bin outputs; then repeat unchanged for incremental timing.
/usr/bin/time -p dotnet build CodexWorker.sln --no-restore -m:1
/usr/bin/time -p dotnet test CodexWorker.sln --no-build --no-restore -m:1 \
  --logger trx --results-directory /tmp/issue-373-baseline \
  --blame-hang-timeout 60s --blame-hang-dump-type none
/usr/bin/time -p dotnet test CodexWorker.sln --no-build --no-restore -m:1 --list-tests
# Discovery-only also measured individually against each test csproj with the same flags.
```

The initial sandbox restore failed with `NU1301` (NuGet socket permission denied); successful measurement used authorized sandbox escalation for restore/build/test. The initial parallel-MSBuild invocation also exited unsuccessfully without useful output; it is not a timing sample. Restore includes available package caches/feed access, not a controlled cold-cache experiment. The fresh build includes npm dependency installation. The Server build always runs dashboard typechecking, linting and Vite generation, even when C# inputs are unchanged.

## Measured breakdown

| Phase | Wall seconds |
| --- | ---: |
| Successful restore | 8.28 |
| Fresh checkout solution build, excluding NuGet restore | 67.82 |
| Unchanged solution build, excluding restore | 30.59 |
| Full no-build/no-restore test command | 315.05 |
| Solution discovery/startup only (`--list-tests`) | 4.40 |
| Worker discovery/startup only | 2.92 |
| Toolbox discovery/startup only | 1.71 |
| Metadata rebuild, same solution build command | 49.07 |

The decomposed first validation costs **391.15 seconds** (restore + fresh build + tests); with the measured unchanged build, build + tests costs **345.64 seconds**. These are sums of measured phases, not a separately repeated end-to-end run. The metadata rebuild recompiles test sources; it is not an unchanged-build comparison or evidence of a speed regression. Metadata changes no execution algorithm, and no second full suite was run merely to claim a before/after improvement.

| Test project | Passed cases | TRX session seconds | Active case interval union seconds | Sum of case durations seconds |
| --- | ---: | ---: | ---: | ---: |
| CodexWorker.Tests | 1,816 | 302.705 | 299.055 | 344.500 |
| WorkExecutionToolbox.Tests | 269 | 10.598 | 8.239 | 16.188 |
| Total | **2,085** | **313.303** | **307.294** | **360.688** |

All baseline cases passed; none failed/skipped. TRX session time is `Times.finish - Times.start`. The union merges overlapping case start/end intervals within each project; it measures time with at least one active case, including waits and any fixture work inside those intervals. Session time outside those intervals is 3.650 seconds for Worker and 2.359 seconds for Toolbox; the command adds another 1.747 seconds outside project sessions. These gaps include hosting/discovery, fixture setup/teardown, scheduling and reporting, and cannot all be attributed to discovery. Discovery-only wall time is an independent estimate, **not** an additive phase of the baseline. Parallel case duration sums are diagnostic costs, not wall time or pure assertion CPU time.

Test execution, particularly Worker integration tests, dominates. Incremental dashboard/build work is a material additional cost, but restore and discovery do not explain the roughly six-minute warm validation.

## Slow groups and cases

Largest class costs by summed case durations (overlapping where collections run concurrently):

| Class | Seconds |
| --- | ---: |
| ManagedWorkerReadinessTests | 106.976 |
| GitWorktreeTests | 43.452 |
| CodexServerTests | 29.136 |
| WorkerV011Tests | 26.590 |
| ManagedHttpsSecurityTests | 19.387 |
| ManagementApiTests | 18.971 |
| ProvisioningCommandTests | 15.917 |
| WorkerServerHttpTransportTests | 11.595 |
| InstallerGitIdentityTests | 8.912 |
| GitHubIssueRootTests (Toolbox) | 8.163 |

Slowest case/method observations:

| Method (class prefix) | Slowest row seconds | Cases / summed seconds |
| --- | ---: | --- |
| ManagementApi.ExactAndIssueLookupsIncludeOldAttemptsOutsideBoundedRecentList | 18.419 | 1 / 18.419 |
| ManagedWorkerReadiness.UnmaterializedCatalogSurvivesRestartOutageAndReconnect | 15.970 | 2 / 29.951 |
| ProvisioningCommand.NodePreparationRecoversActiveOperationsBeyondUnrelatedAndTerminalHistory | 14.288 | 1 / 14.288 |
| InstallerGitIdentity.InstalledServiceAccountIdentitySatisfiesGitConfigurationCheckAndInventory | 8.912 | 1 / 8.912 |
| ManagedWorkerReadiness.CachedUnmaterializedProjectReconnectsWithoutRestartOrAssignment | 7.691 | 1 / 7.691 |
| ManagedWorkerReadiness.AssignmentWithNoCheckoutHasBoundedPreparationOutcome | 7.479 | 6 / 35.829 |
| ManagedHttpsSecurity.AcceptedEnrollmentWithLostTlsResponseRecoversAcrossBothRestarts | 6.100 | 4 / 19.387 |
| GitHubIssueRoot.PersistentMultiPageScanRefreshesOnlyOpenOrChangedIssues (Toolbox) | 2.601 | 1 / 2.601 |

Durable-history population, real control-loop waits, HTTPS lifecycle and Git subprocess work are the principal candidates for further profiling. Preserve the ownership/recovery/security contracts while investigating those costs.

## Filter metadata and inventory

Every one of the **1,170 .NET test methods** has exactly one method-level `TestType` and one `Duration` trait. Classification follows the exercised boundary, including fixture setup, rather than the filename: for example, `WorkerAdministrationIntegrationTests` uses injected process results and is `Component`; real local repository discovery is `Integration`. Mixed files have mixed method traits.

| Trait | Definition | Methods | Cases |
| --- | --- | ---: | ---: |
| TestType=Unit | Isolated local logic, value validation, parsing, formatting or state transitions; no real I/O boundary | 200 | 358 |
| TestType=Component | In-process contracts/collaborators, CLI/API serialization or orchestration with stubbed transport/process/provider seams | 404 | 797 |
| TestType=Integration | Real filesystem/persistence, SQLite, Git/subprocess, local HTTP/TLS or service-host fixtures; external providers remain fake | 566 | 930 |
| Duration=Fast | Maximum measured row duration < 0.100 seconds | 874 | 1,562 |
| Duration=Medium | Maximum measured row duration >= 0.100 and < 1.000 seconds | 264 | 471 |
| Duration=Slow | Maximum measured row duration >= 1.000 seconds | 32 | 52 |

The baseline row distribution has p50=3.431 ms, p75=86.073 ms, p90=303.294 ms, p95=455.104 ms, p99=4.330 s, maximum=18.419 s. The 100 ms cutoff captures the dense lower three quarters; 1 second separates the small expensive tail. Raw rows below 100 ms number 1,591 and below 1 second number 2,036. Method-level buckets conservatively put **all rows of a theory in its slowest row's bucket**, explaining the different trait totals. Labels describe this host/run, not timing assertions or universal performance guarantees.

For new or changed tests, inspect the actual boundary, measure the affected methods with TRX on the recorded host/options, and assign the bucket from the maximum row. Record the new measurement separately from this immutable baseline and update expected filter counts. Revisit candidate membership by the contract it protects, independently of its duration; do not add timing assertions or change behavior to achieve a label.

[Inventory](benchmarks/validation-28.3/inventory.csv) records every source/method, boundary type, bucket, independent candidate membership, case count, maximum and summed seconds. [Per-case evidence](benchmarks/validation-28.3/baseline-cases.csv) records all 2,085 durations/outcomes; `duration_rank` orders rows of each method from slowest to fastest without persisting argument values. [Machine-readable summary](benchmarks/validation-28.3/summary.json) records context and phase/project totals. Raw TRX is local at `/tmp/issue-373-baseline`; raw output, environment contents and parameter values are not committed.

No .NET end-to-end/system category is needed: provider calls are fake and the real boundaries are local integration fixtures. Other existing validation layers are separate from `dotnet test` and have no xUnit traits:

| Existing layer | Actual boundary / invocation |
| --- | --- |
| `tests/dashboard/*.test.cjs`, `src/CodexServer/worker-poc/tests/*.test.mjs` | Node unit/component checks of dashboard models/rendering/source contracts; existing Node commands |
| `tests/*-tests.sh`, `tests/cw-tests.sh`, `tests/wet-tool-tests.sh` | Shell component/integration checks with stub commands and temporary filesystem/package fixtures; invoke the existing scripts |
| `tests/dashboard-deploy-tests.py`, `tests/remote-https-proxy-tests.py` | Deployment/proxy component and local integration checks; existing Python commands |
| `tests/worker-local-e2e.py`, browser campaigns documented in dashboard/managed-worker guides | Explicitly scoped local system/end-to-end campaigns; not part of this measured solution gate |
| `tests/test-filter-guard-tests.py` (added here) | Five Python unit checks of the count guard's failure contracts |

Those layers were neither timed nor relabeled by intuition. They cannot be selected by a `dotnet test` filter and remain independently runnable. Production code, assertions, test cases, required validation configuration and product version are unchanged.

## Independent high-signal candidate and verified filters

`ValidationProfile=FastCandidate` explicitly marks **62 methods / 86 cases**, independently of `TestType` and `Duration`. It covers structured outcome parsing, worker lifecycle, registry capacity/stale ownership, authentication preflight, invalid configuration and capability admission, allowlisted provisioning, task-vs-infrastructure failure handling, cancellation, cache preservation and worktree isolation/cleanup. Toolbox cases cover repository input validation and rejecting discovery before authentication. It includes Unit, Component and Integration tests, including two Medium Git cases. The inventory lists every selected method; Fast alone selects 1,562 cases and is not a criticality label.

Build first. Direct commands use the framework-supported filter mechanism:

```sh
dotnet test CodexWorker.sln --no-build --no-restore -m:1 --filter 'TestType=Unit'
dotnet test CodexWorker.sln --no-build --no-restore -m:1 --filter 'TestType=Component'
dotnet test CodexWorker.sln --no-build --no-restore -m:1 --filter 'TestType=Integration'
dotnet test CodexWorker.sln --no-build --no-restore -m:1 --filter 'Duration=Fast'
dotnet test CodexWorker.sln --no-build --no-restore -m:1 --filter 'Duration=Medium'
dotnet test CodexWorker.sln --no-build --no-restore -m:1 --filter 'Duration=Slow'
dotnet test CodexWorker.sln --no-build --no-restore -m:1 --filter 'ValidationProfile=FastCandidate'
```

**A bare VSTest invocation may exit successfully for an empty filter.** Use [the explicit count guard](../tools/check-test-filter.py) for a checked result. It runs the same `dotnet test --filter` command with TRX and hang detection, requires the expected positive aggregate case count (including theory rows), and rejects skipped/failed cases, absent TRX and command failures. A project with no matches is permissible only when the total across the solution exactly matches the positive expected count. Update counts deliberately when tests change. This helper is optional local tooling, not a replacement authoritative gate.

To avoid executing the expensive suite repeatedly, complete Unit/Component/Fast sets and representative Integration/Medium/Slow intersections were verified after adding traits:

```sh
python3 tools/check-test-filter.py 'TestType=Unit' --expected 358
python3 tools/check-test-filter.py 'TestType=Component' --expected 797
python3 tools/check-test-filter.py 'TestType=Integration&FullyQualifiedName~LocalRepositoryDiscoveryTests' --expected 4
python3 tools/check-test-filter.py 'Duration=Fast' --expected 1562
python3 tools/check-test-filter.py 'Duration=Medium&FullyQualifiedName~GitWorktreeTests.ExecutionWorktreeIsIsolatedAndRemovedAfterTaskFailureCleanup' --expected 1
python3 tools/check-test-filter.py 'Duration=Slow&FullyQualifiedName~ProvisioningCommandTests.DeadlineCancelsRunningHandler' --expected 1
python3 tools/check-test-filter.py 'ValidationProfile=FastCandidate' --expected 86
# Intentional negative check: must return nonzero, even if dotnet test returns zero.
python3 tools/check-test-filter.py 'Duration=Typo' --expected 1
```

All seven positive filter checks passed with the exact expected counts. Wall times were Unit **8.266 s**, Component **12.301 s**, representative Integration **6.393 s**, Fast **22.846 s**, representative Medium **7.457 s**, representative Slow **6.301 s**, and the independent candidate **9.021 s**. The machine-readable summary records each result. The focused runs use the same host, configuration, no-build/no-restore, sequential project scheduling, TRX and hang-detection options as the baseline. Source review verifies that removing the new trait lines reproduces all original test source exactly. The solution builds passed with zero compiler/analyzer warnings/errors; the five count-guard regression checks passed. These local checks do not claim that the Worker's separately configured authoritative gate has run.

The intentional `Duration=Typo` run matched zero cases in both projects: `dotnet test` completed, but the helper correctly exited **1** with `Expected 1 nonzero cases; TRX contains 0`.

## Recommended profiles (proposal only)

* **Inner loop:** after a build, run the 86-case candidate plus the directly affected methods/projects. It protects central failure/ownership/security decisions at low execution cost; it does not cover all integration paths. Build time will still dominate a tiny subset unless dashboard/build inputs can safely be made incremental in a separate change.
* **Per change:** run Unit + Component and the relevant Integration groups, plus dashboard/shell checks when those layers change. Choose by affected boundary, not just duration. Changes to Git/recovery, persistence/leases, TLS/authentication or managed startup need their full affected integration groups regardless of bucket.
* **Full validation:** retain the existing full solution and Worker-configured commands before integration. Any future reduced mandatory gate requires a separate decision and coverage review.

Next work should measure history setup separately, use established clock/coordination seams for avoidable lifecycle waits, and investigate safe dashboard build incremental inputs. Repeat a controlled single baseline after an actual performance change; do not remove unique scenarios, weaken assertions, or infer improvements from this metadata-only run.
