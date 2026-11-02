# Server and Worker second-pass audit (370.8.4 / #456)

Audited 2026-11-02 on Linux with .NET SDK 10.0.114 and Node 24.21.0. Parent handoff for #370.8: this independently revisits the current source, representative test bodies, workflow boundaries and guidance against the [Server inventory](server-functionality-audit-370.8.1.md), [Worker inventory](worker-functionality-audit-370.8.2.md) and [test consolidation](test-suite-audit-370.8.3.md). Those records remain the first-pass evidence; this report does not replace their inventories or claim their checks ran here.

The bounded simplifications remain justified by source and assertion review. No further production change or test deletion is justified by this pass. This is **not a runtime regression certification**: .NET restore is denied, and the dashboard suite is only partially runnable. Actual executed .NET case counts and before/after test durations remain unavailable. Open policy, presentation and measurement risks below must accompany the parent’s acceptance decision.

## Second-pass capability dispositions

Rows follow every area of the original inventories in order. Paths are relative to `src/CodexServer` or `src/CodexWorker` as appropriate; named tests are in `tests/CodexWorker.Tests`. “Retain” means source/contract review found no simplification regression in the inspected boundary, not that all associated tests passed. “Open” routes an unresolved finding to the follow-up table. No live provider, installed-node, deployment, visual or penetration validation was performed.

### Server: all 18 original areas

| Area | Second-pass evidence and disposition |
| --- | --- |
| Host/configuration | Retain `Program`, configuration mutation and help separation; `ServerAdministrationDispatchTests` still dispatches seven successful read-only commands through `Program.Main`. No startup or version behavior was simplified. Guidance discrepancy is G1 below. |
| HTTP administration | Retain. `AdministrationSessions.Authorized` rejects an explicit bad bearer without session fallback; `Validate` retains exact host, mutation origin and CSRF checks. `WorkerRoutesRequireExactDurableIdentityAndRetainOwnershipChecks` covers the independent Worker boundary. |
| Dashboard | Retain canonical React routes/shared API owner. No UI changes in this pass. Model/source tests partially executed below; approved-reference rendering remains unverified, not replaced by model assertions. |
| UI/API freshness and uncertainty | Retain shared session/runtime/client and Server event-stream cancellation boundaries. `status-cache.test.mjs` could not load esbuild, so no fresh runtime confirmation of mutation fences/session-generation isolation is claimed. |
| MCP and OAuth | Retain scope/provider boundary; confirm F2 in `ServerBackup` and `McpOAuthTests` below. Scrubbing consent/token authority is independent of management, Worker and delivery authentication. |
| Worker registry/onboarding | Retain registration/heartbeat versus execution readiness. `RequestAssignmentCoreAsync` still requires registered, fresh, running, execution-ready heartbeat; enrollment and token revocation remain separate flows. |
| Projects | F1 retained. Both revision-conditional writes return their own SQL row; invalid revision/store tests remain. Failure paths still distinguish conflict/missing; lifecycle and definition share revision and active-work deletion protection. |
| GitHub administration | Retain discovery/planning versus Worker claiming/reporting. `ServerGitHubAdministrationTests` and `ServerRepositoryDiscoveryTests` remain distinct from reusable WET provider tests. No planning mutation moved into Codex. |
| Read acceleration/automatic discovery | Retain read-cache versus fresh enqueue/assignment authority. `AutomaticIssueDiscoveryTests` retains deadline/paging and history suppression coverage; no discovery cache became a lease. |
| Scheduling/capacity | Retain. Registry admission takes the SQLite write gate before reading policy, reservations and limits. It rejects paused/unresolved-update, stale/not-ready and unsynchronized slot states before assignment; Worker reservation is a separate local guard. |
| Assignments/leases | Retain. Report SQL still requires exact assignment, Worker, latest generation, active unexpired lease; lease expiry classifies pre-integration versus uncertain work. `LeaseRenewalIsGenerationAndOwnerBoundAndExpiryCreatesSafeRetry` and stage-expiry theory remain. |
| Results/reconciliation | Retain monotonic stage SQL and terminal acknowledgement contract. Completion correction still requires a running scoped maintenance operation, original identity/generation and transaction-local drain/conflicting-Issue checks. `ManagedExecutionMaintenanceTests.CompletionCorrectionUsesOriginalGenerationAndExplicitRunningMaintenanceAuthority` asserts rejection before claim, for changed generation and after drain release. |
| Node inventory/diagnostics | Retain inventory/readiness distinction; Open F3 clock consistency. `NodeProvisioning.Describe` still uses wall-clock freshness; no evidence of this being corrected by test consolidation. |
| Provisioning | Retain legacy plans and typed commands, Open F4. `ProvisioningCommandStore` validates supported typed requests and enforces one active node/capability operation; shared protocol bounds timeout to 5–600 seconds and allowlists action/capability pairs. Distinct persisted state is not redundant validation. |
| Credentials | Retain separate management assignment, exact-Worker delivery verifier and Ready/assigned metadata checks in `ServerApplication`; `CredentialAdministrationTests`/`SharedCredentialServicesTests` retain payload/projection boundaries. No credential material was collected by this audit. |
| Node maintenance | Retain scoped maintenance, update/restart, journal and Telegram protocols. Completion tests still exercise explicit authority; journal remains capped at 64 requests and prunes expiry. Open F3–F5; no general remote shell or replay workflow introduced. |
| Persistence/backup | F2 retained. Scrub runs on the copy in a transaction; both optional OAuth tables are emptied. Validation rejects either nonempty table before offline replacement under exclusive access. Existing no-OAuth-table compatibility remains. SQL smoke check passed, full .NET archive test unavailable. |
| Health/packaging | Retain health/readiness distinction, embedded asset route and existing publish gates. No packaging/version edits. Build/typecheck/lint/deployment not certified; Open F6. |

### Worker: all 19 original areas

| Area | Second-pass evidence and disposition |
| --- | --- |
| Entry point/exit/help | Retain `Program`/command dispatch and generated version metadata; no scheduler startup added to read-only commands. `OperabilityTests` retained; G1 records stale version guidance. |
| Standalone startup/isolation | Retain host/startup coordinator and per-project unavailable state. `StartupCoordinatorTests` and `WorkerPreparationTests` remain; no project failure was promoted to a fleet-wide policy change. |
| Configuration/live administration | Both corrections retained: common `SameExecutionIdentity` uses full platform-compared paths; provider removal checks platform-compared parent before deletion. Existing last-valid snapshot/rejection assertions remain. Open W1/W2; lexical confinement is not a symlink security claim. |
| Managed startup/cache/materialization | Retain synchronized configuration and engine authentication gates. `WorkerHost.PublishReadinessAsync` explicitly denies cached-project execution authority; `ManagedCheckoutTests` retains first-assignment materialization, mismatch preservation and cancellation coverage. |
| Identity/enrollment/transport | Retain durable enrollment reconciliation, API versus delivery authorization and HTTPS/redirect boundaries. `WorkerEnrollmentTests`, `WorkerServerHttpTransportTests`, `ManagedHttpsSecurityTests` remain; no compatibility fallback was added. |
| Scheduling/capacity/drain | Retain registry/lifecycle lock, global/per-project reservations and repository gates. `ReplaceConfiguration` validates before swapping the set and rejects active identity/removal changes. Active-case regression still asserts preservation and normal capacity release. Open W4 display semantics. |
| Assignment/lease/result delivery | Retain `WorkerHost` lease identity checks, renewal before claiming/preparation, cancellation/join and revision checks. Server authority is independent of local history/cached config; original-generation reporting remains. |
| Issue discovery/dependencies/claiming | Retain provider retry classification and Worker-owned mutations. Dependency cancellation test now reaches the REST dependency command and asserts token identity, propagated cancellation and exactly two calls; the former duplicate pre-cancelled test remains at the query boundary. |
| Per-execution lifecycle | Retain `ExecutionRunner` structured incomplete-task result versus infrastructure and shutdown exceptions. Safe outcomes return capacity through host coordination; uncertain resources preserve history. `ImplementationEngineTests`, `WorkerV011Tests`, `ValidationRepairContractTests` remain. |
| Engine invocation/child environment | Retain `CodexEnvironment.Create`: strips inherited GH/GITHUB/ACTIONS identity variables, replaces home/gh config, disables helper/askpass/prompt and prevents project environment overriding protected keys. `CodexServiceEnvironmentTests`, task-contract and process tests remain distinct. |
| Validation/integration | Retain sequential configured commands and repair bounds. Runner checks cancellation/authority after repository-gate acquisition and passes fresh authority into Git integration; post-rebase validation remains separate from initial validation. Open W5 conflict policy; not a blanket policy-compliance finding. |
| Completion/reports/notifications | Retain durable completion/checkpoints versus nonfatal notification behavior. `GeneratedMessageOriginTests`, `IssueCommentContextTests` and managed completion maintenance coverage remain; reporting-only retry cannot rerun implementation. |
| Interrupted implementation/capacity/quota | Retain distinct recovery coordinator, quota policy and bounded attempt lineage. Codex capacity waits do not justify generic service retries. Interruption/capacity/quota suites remain. |
| Standalone interruption/integration recovery | Retain exact process/worktree/branch/base proof and managed-history exclusion. `StandaloneInterruptedExecutionTests.ManagedHistoryWithStaleGenerationCannotEnterStandaloneContinuation` remains, as do shutdown-during-validation and same-attempt continuation tests. |
| History/retention/cleanup | Retain inspection versus mutation authority, drain/repository gating, history reread and ownership proof. Expiry alone grants no cleanup permission; `ExecutionMaintenanceTests` and real `GitWorktreeTests` still cover separate boundaries. |
| Standalone/managed maintenance | Retain local ownership versus Server-scoped authority. `ManagedExecutionMaintenanceTests` retains lost-ownership rejection, receipt replay without reexecution and expired dispatch nonreplay; standalone maintenance remains separate. |
| Status/API/presentation | Retain metadata projection rather than invented scheduling authority. `WorkerStatusTests`, `ManagementApiTests`, resource contract tests remain. Open W3/W4; collector still uses raw mount-path prefix. |
| Provisioning/auth/update/restart | Retain shared typed actions and node-local permission, private credentials and readiness revocation. Lifecycle retains failed restart/update states rather than returning ready. Provisioning/restart/update suites remain. |
| Shared/product boundaries | Retain actual shared protocols in `src/Shared`; no Server registry duplicated in Worker. WET and `cw` were neither changed nor invoked; independent WET suite inventory is unchanged. |

### Test inventory: all 12 original groups

| Original group | Second-pass disposition |
| --- | --- |
| Server host/configuration/CLI/dashboard delivery | Retain; five removed invalid invocations are covered by nine-row family-help theory; successful dispatch remains separate. |
| Registry/projects/admission/assignments/leases | Retain; merged null-requirements case still validates, creates and reads back an empty list after CRUD assertions. F1/lease/expiry tests remain. |
| Authorization/control transport | Retain all distinct browser/management/Worker/delivery scopes and cancellation/expiry/redirect cases. No test count saving claimed here. |
| GitHub adapters/discovery/planning | Retain; empty-dependency selection survives in REST-command test, cancellation corrected at actual dependency lookup. Server/Worker/WET adapters own different contracts. |
| Configuration/scheduling | Retain strict YAML, atomic snapshot, active reservation/history and capacity tests. Case-path regressions remain. |
| Managed ownership/readiness | Retain registration/authentication/execution-readiness distinctions and outage rejection. |
| Execution/engines/validation | Retain engine outcomes, environment isolation and sequential authoritative validation/repair coverage. |
| Integration/interruption/recovery | Retain real Git tests and orchestration fakes; they do not substitute for each other. W5 requires a policy decision. |
| Provisioning/credentials/maintenance | Retain both persisted protocols, local authorization, deadlines, uncertainty and at-most-once dispatch tests. |
| Shared contracts | Retain wire/allowlist/normalization composition tests despite similar application boundary inputs. |
| Presentation/diagnostics | Retain bounded/redacted/correlated diagnostics and notification behavior; W2–W4 remain open. |
| WET library/provider/CLI/MCP | Retain all 11 files and all declarations; generic provider coverage does not replace product adapters. |

## Removed work and authoritative guarantees

No production protection was removed by the first-pass fixes or test consolidation. Inspection confirms these specific reductions rather than relying on counts:

* **Project successful-path reread removed:** `UPDATE ... RETURNING` returns the row accepted by the revision predicate. Normalization, validation, uniqueness and stale revision checks remain at their boundaries. The advancing-trigger theory distinguishes returned revision 2 from stored revision 3 for both definition and lifecycle, including content/enabled assertions; the nonpositive-revision theory checks existing and missing IDs without state change. Reexecuted production SQL smoke confirms both statements and stale/missing-row behavior, but does not substitute for Microsoft.Data.Sqlite tests.
* **Duplicated active checkout predicate removed:** service durable-history guard and registry active-reservation guard call the same platform-aware predicate. These guards remain necessary at different moments; no authority was moved to a cached flag. Service rejection test asserts YAML unchanged; registry test checks old configuration and release; provider test asserts outside file retained and valid inside deletion succeeds.
* **Empty-dependency Fact removed:** surviving `DependencyLookupUsesRestApiAndIssueListingDoesNotRequestBlockedBy` selects Issue 1 from the empty-dependency fixture and asserts exact paginated REST lookup, supported list fields and absence of label mutations. Closed/open/mixed/later-page cases remain distinct.
* **Null-requirements Fact merged:** `CentralProjectsSupportValidatedCrudDurabilityUniquenessAndRevisionConflicts` still asserts validation succeeds, created requirements empty and freshly read non-null project requirements empty. No production default was weakened.
* **Five invalid CLI calls removed:** `UnknownOperationsPointToFamilyHelpWithoutEchoingArguments` includes worker, backup, github, credential and worker-token plus four other families. It uses `Program.Main`, asserts exit 2, matching family help and no untrusted argument echo. Seven successful dispatches remain in the smoke test.
* **Dependency cancellation test corrected:** cancellation now occurs inside the dependency process seam, after successful listing. Exact command/token and two-call assertions prohibit a retry/mutation. Pre-cancelled query and uncertain write cancellation coverage remains separate.

F2 adds security work rather than reducing steps: the OAuth regression asserts code/access/refresh denial after restore with unchanged issuer/management authority, source authorization retained, rejection of archives containing either authority table and preservation of restore-target project state. Hashing bearer credentials alone is not a restore authorization guarantee.

## Remaining findings and follow-ups

These are parent-visible follow-up scopes, not newly created Issues or authorization to change behavior. No larger change was attempted during this audit.

| Prior finding | Second-pass disposition / next evidence needed |
| --- | --- |
| Server F1 project mutation response/revisions | Correction retained; production SQL smoke passed. Full .NET regression execution remains unavailable. |
| Server F2 OAuth backup authority | Correction retained; both scrub statements passed SQL smoke; archive/authorization regression body inspected. Execute full archive test after restore is available. |
| Server F3 clock consistency | Still open: node projection, journal deadlines and maintenance authorization use direct wall clock while stores expose injected time. Add existing-clock plumbing and exact freshness/deadline tests before changing semantics. |
| Server F4 command protocols/restore | Still open: specify supported clients and pending/running restore/restart semantics before retiring legacy plans or sharing transition logic. Keep at-most-once uncertainty protections. |
| Server F5 retention/paging | Still open: journal already bounded (64/expiry); OAuth replay verifiers and full Worker/project collections need measured churn, retention and stable pagination design. No arbitrary pruning justified. |
| Server F6 service size/deployment | Still open: extract cohesive responsibilities only on concrete need while preserving transactions. Managed campaign, proxy/TLS/logging, real-node authorization and approved visual comparisons were not run. |
| Worker correction 1: active checkout case | Retained in both service and registry through common predicate; no runtime pass claimed. |
| Worker correction 2: deletion confinement | Retained platform parent comparison before delete; symlink aliasing remains outside this lexical correction. |
| W1 configuration filename identity | Still open: `Configuration.cs:ValidateCandidate` and service remove/rename filters use case-insensitive file paths. Need deterministic case-distinct manually named YAML fixtures and explicit selection/uniqueness semantics. |
| W2 duplicate reload rejection | Still open: service publishes rejection then watcher catches/publishes another. Choose one notification owner with coordinated tests; do not assume one filesystem event per write. |
| W3 resource telemetry | Still open: `WorkerHostResourceCollector` matches mount roots with raw prefix and reads CPU line zero before checking line count. Add input seams for `/app` versus `/application` and malformed/empty input; keep telemetry optional/cancellable. |
| W4 drain/readiness display | Still open: `CancelDrain` sets state ready while host execution readiness is independent. Define unavailable-after-drain display and test with controlled coordination; retain scheduler preflight. |
| W5 integration conflict policy | Still open, significant guidance mismatch: `ExecutionRunner` supplies `engine.ResolveIntegrationConflictAsync` to integration while AGENTS/operations prohibit automatic conflict resolution. Existing callback was not introduced by these fixes. Parent must choose supported behavior and add policy-focused conflict tests; this pass does not certify that workflow as compliant. |
| Test consolidation cancellation gap | Corrected; inspected command/token/exception assertions actually reach dependency lookup. Runtime execution remains unavailable. |
| G1 version guidance drift (additional documentation finding) | Repository AGENTS says intended version 0.15.0; existing `Directory.Build.props` declares 0.33.0. Do not infer a version from Issue series or downgrade source. Release owner should reconcile the historical sentence with release policy/history; neither version nor authoritative instructions changed here. |

Remaining repeated authorization checks are justified by trust or time: API authentication identifies caller; store predicates serialize current ownership; Worker validates local policy/resources; integration/maintenance reauthorize after gate waits. Removing one because an earlier check passed would weaken those boundaries. Legacy/typed provisioning stores are parallel protocols with different durable semantics, not demonstrated duplicate implementations. The two large product test files remain a navigation/maintenance cost; no speculative split or count target was imposed.

## Repeated inventory and measurements

[Per-file CSV](benchmarks/audit-370.8.4/test-inventory.csv) contains all 127 test-bearing C# files. All four per-file F/T/I/M counts were compared programmatically with the published 370.8.3 after-table and match exactly. No ClassData declarations occur. Source declarations are not discovered/executed test cases; five MemberData expansions are not guessed.

| Metric | 370.8.3 before | 370.8.3 after | This pass |
| --- | ---: | ---: | ---: |
| Server/Worker files | 116 | 116 | 116 |
| Server/Worker Facts | 867 | 865 | 865 |
| Server/Worker Theories | 310 | 310 | 310 |
| Server/Worker InlineData / MemberData | 1,171 / 5 | 1,171 / 5 | 1,171 / 5 |
| WET files | 11 | 11 | 11 |
| WET Facts / Theories / InlineData | 95 / 52 / 178 | 95 / 52 / 178 | 95 / 52 / 178 |
| Total methods | 1,324 | 1,322 | 1,322 |
| Discovered/executed/passed .NET cases | Unavailable | Unavailable | Unavailable |
| TRX session duration | Unavailable | Unavailable | Unavailable |
| Failed no-restore command wall seconds / exit | 1.09 / 1 | 1.09 / 1 | 1.44 / 1 |

Two fewer methods (about 0.15%) and five fewer invalid CLI invocations are the established reductions. No improvement in application workflow duration, test runtime, discovered-case count, flakiness or memory usage has been demonstrated. This pass changes none of those counts. The 1.44-second measurement includes first-run SDK startup and missing-assets failure, whereas prior attempts reported unresolved NU1301 assets; it is neither comparable test time nor a performance regression. Historical 28.2/28.3 benchmarks do not fill this gap.

Repeat inventory from repository root (same declaration method as baseline):

```sh
python3 - <<'PY'
from pathlib import Path
import csv, re, sys
w = csv.writer(sys.stdout)
w.writerow(['project', 'file', 'Fact', 'Theory', 'InlineData', 'MemberData', 'ClassData'])
for project in ('CodexWorker.Tests', 'WorkExecutionToolbox.Tests'):
    for path in sorted(Path('tests', project).glob('*.cs')):
        counts = [len(re.findall(r'^\s*\[' + kind + r'(?:\]|\()', path.read_text(), re.M))
                  for kind in ('Fact', 'Theory', 'InlineData', 'MemberData', 'ClassData')]
        if counts[0] + counts[1]:
            w.writerow([project, path.name, *counts])
PY
```

Exact local commands/results:

```sh
/usr/bin/time -f 'command_wall_seconds=%e exit=%x' \
  -o /tmp/test-audit-370.8.4.time \
  dotnet test CodexWorker.sln --no-restore -m:1 \
  --logger 'console;verbosity=minimal' --logger trx \
  --results-directory /tmp/test-audit-370.8.4 \
  > /tmp/test-audit-370.8.4.log 2>&1

dotnet test CodexWorker.sln -m:1 --logger 'console;verbosity=minimal' \
  --logger trx --results-directory /tmp/test-audit-370.8.4-restored \
  > /tmp/test-audit-370.8.4-restore.log 2>&1

node --test src/CodexServer/worker-poc/tests/*.test.mjs \
  > /tmp/test-audit-370.8.4-node.log 2>&1
```

* No-restore .NET command: exit 1, NETSDK1004 missing `project.assets.json` for both test projects; no tests/TRX.
* Restore-enabled .NET command: exit 1, NU1301, permission denied reaching `api.nuget.org:443`; no successful build/test evidence. Compiler/analyzer validation remains unavailable.
* Dashboard: exit 1; Node reports 23 file-level tests, 11 passed, 12 failed to load missing `esbuild`, 1,081.986767 ms. Passed files: enqueue-dialog-reference, execution-missing-text, execution-status, execution-timeline, issue-history-model, node-provisioning-claude-login, sidebar-administration, telegram, worker-actions, worker-editor, worker-restart. These are file-level harness results, not counts of individual assertions. No before/after dashboard speed claim. Typecheck/lint/build were not run because dependencies are absent.
* Reexecuted the exact Python SQL smoke reproduction in the Server audit: passed with SQLite 3.45.1 for both project UPDATE statements (own revision/content/lifecycle and stale/missing rows) and both OAuth DELETE statements. This does not exercise .NET mapping, archive handling or auth endpoints.
* Source inventory/per-file comparison, relative-link/whitespace checks and documentation/diff review passed. No UI mutation, real Git repository operation, provider authentication, live GitHub/Codex, installed-node campaign or release action was performed.

For an execution-capable parent validation host, restore/build each before/after state and use the same baseline `dotnet test CodexWorker.sln --no-build --no-restore -m:1 --logger 'console;verbosity=minimal' --logger trx --results-directory <separate-phase-directory> --blame-hang-timeout 60s --blame-hang-dump-type none`; compare discovered/outcome totals and sum each project's TRX finish-minus-start durations without changing collection scheduling. Worker-configured validation remains the separate authoritative gate.

Final self-review: the deliverable is this evidence record and the repeated inventory. All initial capability groups and findings have a disposition; removed work maps to surviving guarantees; unavailable execution and unresolved policy risks are explicit. No production, tests, ownership/lifecycle, security, configuration, release or version behavior changed in this pass.
