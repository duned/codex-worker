# Server and Worker audit integration (370.8 / #452)

Completed source integration review on 2026-11-02. This report joins the four child deliverables already present in this checkout and records the additional parent corrections. It does not assert GitHub Issue state or successful runtime certification. Worker-configured validation remains the separate authoritative gate.

## Baseline and dependency handoff

| Dependency | Delivered evidence and initial disposition | Second-pass disposition |
| --- | --- | --- |
| #453 / 370.8.1 Server | [18-area functional inventory, workflow traces, fixes and validation](server-functionality-audit-370.8.1.md) | All 18 areas revisited in [370.8.4](server-worker-reaudit-370.8.4.md). Retain the project-revision and OAuth-backup corrections; preserve the unresolved Server follow-ups below. |
| #454 / 370.8.2 Worker | [19-area functional inventory, workflow traces, fixes and validation](worker-functionality-audit-370.8.2.md) | All 19 areas revisited in 370.8.4. Retain the active-checkout identity and YAML deletion corrections; parent resolves W1, W3 and W5 below. |
| #455 / 370.8.3 tests | [12 coverage groups, per-file baseline, consolidation mapping and measurements](test-suite-audit-370.8.3.md) | All 12 groups revisited in 370.8.4. Keep the consolidation and corrected dependency-cancellation test; add targeted parent regression coverage. |
| #456 / 370.8.4 final audit | [Full second-pass dispositions and repeated inventory](server-worker-reaudit-370.8.4.md) | Findings integrated here. Its measurements remain historical evidence for the child state; the parent changes and final measurements below supersede its W1/W3/W5 status and source totals. |

Together these inventories cover product code, shared protocols, administration/dashboard surfaces, user workflows, documentation and tests. Initial and second-pass dispositions are recorded for every inventoried capability, rather than extrapolating from a few passing tests. `cw` developer tooling, Server administration and reusable WET remain separate; neither `cw` nor WET implementation was changed. No provider authentication, live Issue operation, deployment, installed-node campaign or visual acceptance was performed.

## Before and after behavior

The Server child changes return the project row accepted by the revision-conditional SQL update instead of rereading a potentially newer row, reject nonpositive revisions at the store boundary, and scrub OAuth authority from backup copies while rejecting archives retaining it. The Worker child changes share the active execution-identity predicate and use platform path comparison for checkout identity and deletion confinement. Those changes and their regression tests are already integrated; they are not new parent edits.

The parent resolves these confirmed findings:

| Finding | Before | Finished behavior and coverage |
| --- | --- | --- |
| W1: YAML filename identity | On Linux, case-distinct filenames could both be excluded from replacement validation or the remaining-file count. Updating a manually cased file could leave its old copy behind. | Candidate replacement, removal filtering and old-file retirement use the platform path comparison. Regression fixtures use actual provider filenames renamed by case, verify duplicate repository rejection before writing, permit removal while the independent file remains, and retire the replaced manual filename. Windows keeps its case-insensitive behavior. Project/repository uniqueness semantics are unchanged. |
| W3: host resource telemetry | Mount `/app` could match directory `/application`; an empty `/proc/stat` indexed line zero and escaped the optional-counter error boundary. | Mount selection requires directory containment and still selects the longest matching root. CPU parsing checks presence, aggregate-row identity, minimum field count and nonnegative counters; unavailable data stays within the existing optional telemetry exception boundary. Deterministic tests cover parent/sibling/root mounts, empty/incomplete/incorrect/negative CPU rows, processor counts and exclusion of guest time. Existing cancellation coverage remains. |
| W5: integration conflict policy | Both normal integration and explicit integration recovery supplied engine conflict-resolution callbacks, contrary to AGENTS and operations guidance. | Both paths decline automatic conflict resolution. Existing Git code verifies abort and preserves the implementation/recovery resources, or reports infrastructure failure if clean abort cannot be proven. Successfully rebased source still receives authoritative validation and the established bounded integration-repair loop. Two existing orchestration tests now probe the supplied policy with an engine that would report resolution success, assert it is never invoked, and retain conflict history/scheduler continuation and explicit recovery lineage assertions. No new permission decision is required: repository guidance already prohibits automatic conflict resolution. |

Second review of these edits traced initial and recovery integration through `ExecutionRunner` into `GitRepository.RebaseForIntegrationAsync`, and checked candidate validation before YAML writes, file retirement and registry refresh. The CPU parser is called inside the existing bounded optional-counter exception handling; cancellation remains propagated. No scheduling, lease, durable recovery, credential-delivery or node-local authorization guarantee was removed. These are source conclusions; the full regression tests could not execute locally.

## Removed work and retained checks

The child test audit removes two duplicate Facts, merges their meaningful assertions into surviving tests, removes five repeated invalid CLI invocations, and fixes the dependency test to cancel at the actual dependency endpoint. Its coverage mapping explains each surviving guarantee. The successful project-update reread and duplicated execution-identity predicate were also removed. The parent removes engine conflict-resolution work from both execution flows; it does not remove post-rebase validation repairs.

Retain separate API/session/Worker/delivery authentication, transactional admission, current assignment/generation predicates, authority checks after gate waits, node-local policy, Git resource ownership verification, sequential configured validation, repair limits, uncertainty preservation and explicit recovery lineage. These checks protect different trust boundaries or different moments. Keep both persisted provisioning protocols until supported-client and restore semantics justify consolidation. No speculative class split, alternate state store, generic retry loop, force-push, version change or new configuration contract was introduced.

## Coverage and measurements

Counts use the declaration inventory method published in 370.8.3 and 370.8.4; they are not discovered or executed test cases. Per-file child inventories remain unchanged historical records. Parent changes only three existing test files.

| Source metric | Before child test cleanup | Integrated child baseline | Parent final |
| --- | ---: | ---: | ---: |
| Server/Worker test files | 116 | 116 | 116 |
| Facts / Theories | 867 / 310 | 865 / 310 | 867 / 313 |
| InlineData / MemberData | 1,171 / 5 | 1,171 / 5 | 1,182 / 5 |
| WET files | 11 | 11 | 11 |
| WET Facts / Theories / InlineData | 95 / 52 / 178 | 95 / 52 / 178 | 95 / 52 / 178 |
| Total methods | 1,324 | 1,322 | 1,327 |
| Executed .NET cases / TRX session duration | Unavailable | Unavailable | Unavailable |

The parent adds five test methods (two Facts and three Theories, with eleven InlineData rows) and strengthens two existing integration tests. Meaningful coverage takes precedence over a smaller count. There is no demonstrated improvement in test duration, flakiness or product performance. Child failed-startup wall times are not test duration.

Exact final local commands and results, from the repository root with SDK 10.0.114:

```sh
dotnet test CodexWorker.sln -m:1 --logger 'console;verbosity=minimal' \
  --logger trx --results-directory /tmp/audit-452-baseline

/usr/bin/time -f 'command_wall_seconds=%e exit=%x' -o /tmp/audit-452-final.time \
  dotnet test CodexWorker.sln --no-restore -m:1 \
  --logger 'console;verbosity=minimal' --logger trx \
  --results-directory /tmp/audit-452-final > /tmp/audit-452-final.log 2>&1

node --test src/CodexServer/worker-poc/tests/*.test.mjs > /tmp/audit-452-node.log 2>&1

dotnet run --project /tmp/audit-452-telemetry/Telemetry.csproj

git diff --check
```

* Restore-enabled baseline attempt: exit 1, NU1301, permission denied connecting to `api.nuget.org:443`. No test execution/TRX.
* Final no-restore solution attempt: exit 1, the unresolved NU1301 restore errors remain in assets; 1.08 seconds failed-command wall time. No test execution/TRX or solution compiler/analyzer certification.
* Dashboard: exit 1, 23 file-level harness tests, 11 passed and 12 failed to load missing `esbuild`, 1,075.239812 ms. This matches the child failure classification. No UI code changed; typecheck/lint/build and approved-reference rendering remain unverified.
* Telemetry smoke: exit 0, `Telemetry smoke passed: 11 checks.` A temporary package-free net10.0 console project links the actual collector and shared resource contract with nullable, implicit usings and warnings-as-errors enabled. It exercises the five mount inputs and four unavailable CPU inputs from the added tests, verifies `(Total, Idle, Count) == (360, 90, 2)` for the valid fixture, and checks pre-cancelled collection. Its NuGet configuration clears package feeds. This compiles only these two production files and checks telemetry behavior; it does not substitute for the solution or xUnit suite.
* Repeated declaration inventory: 116 Server/Worker files with F/T/I/M/C totals `867/313/1182/5/0`; 11 WET files with `95/52/178/0/0`. Source/diff review and `git diff --check` passed.

On the authoritative validation host, run the configured commands. For a runtime before/after comparison, use the same restored/build state and TRX measurement procedure recorded in 370.8.3 for each phase; do not infer elapsed test time from failed restore attempts.

## Remaining follow-ups and limits

The audit deliverables and bounded corrections are complete; these dispositions retain explicit risks rather than certify unexecuted workflows:

* Server F3: align node projection, journal deadlines and maintenance authorization with existing injected clocks, with exact deadline/freshness coverage. No authoritative lease predicate was changed here.
* Server F4: establish supported legacy/typed provisioning clients and restored pending/running operation semantics before protocol retirement. Preserve at-most-once dispatch and uncertain-operation reconciliation.
* Server F5/F6: measure collection/replay-verifier churn before retention/paging changes; extract responsibilities only for a concrete need. Managed campaigns, TLS/proxy/deployment and visual acceptance remain separate validation scopes.
* Worker W2: reload rejection is published by both service and watcher. Establish one notification owner, including services constructed without a runtime registry, and deterministic per-reload coordination coverage before altering that contract. This remains a confirmed diagnostic duplication, not a scheduling defect; no one-event-per-filesystem-write guarantee is claimed.
* Worker W4: define and test drain cancellation presentation when execution readiness is unavailable. Current scheduling preflight remains independent; this report does not certify the ready display in that state.
* G1: AGENTS' historical intended-version sentence differs from the existing source version. Leave release metadata and authoritative instructions untouched; the release owner should reconcile the guidance through the explicit release process.
* Lower-level conflict-resolution adapter APIs remain, but neither normal nor recovery execution supplies them as integration callbacks. Removing unused adapter capabilities and their dedicated isolation tests can be a separate API cleanup; no production execution route to them was retained in `ExecutionRunner`.
* Full .NET compilation/analyzers and regression execution, runtime counts/durations, dashboard dependency checks, live-provider/installed-node campaigns and visual comparisons remain unavailable or outside this local pass. No unsupported pass, speedup, complete defect absence or deployment-readiness claim is made.

Final self-review checked requested behavior, preserved authority/cancellation/recovery boundaries, targeted coverage, remaining limitations and focused scope. No Git/GitHub lifecycle, product version, release policy or configuration key was changed.
