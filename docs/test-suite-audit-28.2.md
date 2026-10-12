# Test suite audit (28.2)

## Scope and measurement

Audited both .NET test projects in `CodexWorker.sln`: Worker/Server integration and contract tests, and Work Execution Toolbox tests. The repository also contains dashboard Node tests, shell installer/update/release tests, Python deployment checks and explicitly scoped browser/local end-to-end campaigns. These remain separate validation layers; no external-service campaign was run or removed. There is no checked-in `.github` CI workflow. README development validation uses `dotnet build CodexWorker.sln` and `dotnet test CodexWorker.sln`; the example project YAML configures `dotnet test`. Worker-configured validation remains authoritative.

Measured on the same Linux host with .NET SDK 10.0.112, Debug/net10.0, sequential MSBuild projects and normal xUnit collection scheduling. `ServerTokenEnvironment` disables collection parallelism. Build/restore/frontend asset generation is excluded from test execution time. Duration is the sum of each project's TRX `Times.finish - Times.start`, not the sum of individual tests (which can overlap).

Reproduce after building the solution:

```sh
dotnet build CodexWorker.sln -m:1
dotnet test CodexWorker.sln --no-build -m:1 \
  --logger 'console;verbosity=detailed' --logger trx \
  --results-directory /tmp/test-audit-before \
  --blame-hang-timeout 60s --blame-hang-dump-type none
# After cleanup/build, use the same command with /tmp/test-audit-after.
```

The initial sandbox invocation could not create MSBuild sockets. Local execution was then permitted outside that restriction. An initial build-and-test invocation was cancelled after a long silent interval; this was not a usable measurement. The detailed baseline completed all Worker tests successfully. Because the interrupted initial build had not produced the Toolbox assembly, its unchanged baseline was completed with the same options against `tests/WorkExecutionToolbox.Tests/WorkExecutionToolbox.Tests.csproj`. The baseline totals below combine those two successful test sessions. The final measurement runs the whole solution. Both measurements use the same per-project runner options/environment; neither includes build time. This qualification matters when comparing command wall time.

## Baseline and cost

Baseline: **2,088 passed tests**, **357.607 seconds** of aggregate project test-session elapsed time (Worker 1,819 / 347.008 seconds; Toolbox 269 / 10.599 seconds).

Principal class costs from TRX individual durations (these are diagnostic sums, not the suite elapsed metric):

| Class | Seconds | Decision |
| --- | ---: | --- |
| ManagedWorkerReadinessTests | 103.770 | Retain managed startup, outage, reconnect and preparation contracts. |
| WorkerV011Tests | 58.810 | Retain orchestration, ownership and failure semantics. |
| GitWorktreeTests | 56.394 | Reduce repeated Git initialization and overlapping cases. |
| CodexServerTests | 30.240 | Retain control-plane contracts. |
| ManagedHttpsSecurityTests | 22.207 | Retain TLS enrollment/rotation and lost-response recovery. |
| ProvisioningCommandTests | 17.985 | Retain allowlisted command and durable active-operation recovery. |
| ManagementApiTests | 17.172 | Retain history bounds, exact lookup and redaction. |
| WorkerServerHttpTransportTests | 16.627 | Retain redirect credential/body isolation. |

Slowest individual cases were unmaterialized catalog outage/reconnect (16.657 and 14.320 seconds), exact history lookup beyond the recent window (16.425), node preparation beyond terminal/unrelated history (15.954), device-login recovery beyond general history (8.360), cached project reconnect (7.539), and installed Git identity discovery (7.296). Large durable history setup and real control-loop/HTTPS lifecycles are significant costs. These cases protect distinct boundaries; removing them by count would weaken coverage.

## Focused reduction

* `GitWorktreeTests` now initializes one immutable class-scoped seed, then copies it into a new temporary checkout **and bare remote** per case. Only the copied checkout's URL rewrite is relocated. Each case still exercises real Git with its own objects, refs, configuration and worktree registry. No execution workspace, hook or mutable repository is shared. This replaces ten Git setup processes per case with one relocation process; all recovery, conflict, fencing and cleanup scenarios remain.
* Consolidated `CleaningExecutionADoesNotRemoveExecutionB` into `ExecutionWorktreeIsIsolatedAndRemovedAfterTaskFailureCleanup`. The surviving test retains the isolated path, canonical branch/file, feature-ref removal assertions and now checks the sibling's diagnostic contents and registered worktree after cleanup. This removes one repeated fixture without dropping the sequential sibling-preservation contract.
* Reduced `DirectIntegrationPushSequenceIsIndependentOfRetryAndRecovery` from six cases to four. Fresh retained completion, retry without completion, and recovered integration with and without completion remain. Removed retry-with-retention and fresh-without-retention cross-product repetitions. Assertions still verify exact remote transaction order, commit ancestry/message, remote base SHA and completed-ref presence/absence. Other focused retry naming and integration-recovery tests remain.

No production code, product version, validation configuration, lifecycle waits or security policy changed. No test assertions were weakened and no new broad matrix or external dependency was introduced.

## Final validation

The cleaned solution build passed with **zero warnings and errors**. The final full solution run passed **2,085 tests**, with **337.949 seconds** of aggregate project test-session elapsed time (Worker 1,816 / 327.158 seconds; Toolbox 269 / 10.791 seconds).

| Metric | Before | After | Change |
| --- | ---: | ---: | ---: |
| Passed test cases | 2,088 | 2,085 | 3 overlapping cases consolidated/removed |
| Aggregate project session time | 357.607 s | 337.949 s | 19.657 s faster (5.50%) |
| Git worktree case count | 141 | 138 | 3 fewer |
| Git worktree individual duration sum | 56.394 s | 51.798 s | 4.596 s faster (8.15%) |

This is one successful before/after pair, not a statistical benchmark. Unchanged lifecycle cases also vary; the entire observed suite improvement cannot be attributed to the Git setup change. The targeted class itself improved while retaining real Git and distinct failure/recovery behavior. Further reductions should address deterministic control-loop timing and repeated durable-history population rather than delete critical scenarios. No additional full-suite runs were performed after the passing final run.

Diff review verified independent fixture ownership, preserved unique contracts and no unrelated changes. `git diff --check` passed. These are local checks, not a claim that the Worker's separately configured authoritative validation gate has run.
