# Risk-based Worker validation

The Worker executes validation commands from project YAML, sequentially in the execution worktree, including after rebase and bounded repairs. Selection belongs to the repository that knows its coverage; the generic Worker does not infer application paths or rewrite `dotnet test` commands. Configure **this repository's** project YAML as follows (replace the previous build/test commands rather than appending them):

```yaml
validation:
  timeoutSeconds: 900
  maxFixAttempts: 2
  commands:
    - python3 tools/validate.py --base main
```

This activates the policy for each Validation pass. Existing deployed YAML is external to this checkout and must be updated by its owner. Other repositories retain their configured commands and can supply their own policy. Python 3, Git, .NET 10 and the existing dashboard Node/npm prerequisites must be available. The base argument must match the configured Git base branch; it is a local ref, never fetched or mutated by this command. Task-to-main integration, Git ownership, leases, cancellation, rebase validation and repair budgets remain Worker-owned and unchanged.

For explicit or release-level validation:

```sh
python3 tools/validate.py --base main --full
```

## Selection

The command combines the diff from the merge-base of HEAD and the configured base ref to the current worktree with untracked, nonignored files. This includes committed task changes after rebase, staged and unstaged repairs, deletions and renames (Git's name-only output includes both sides of a rename). Missing base refs, failed discovery, empty scope and uncertain scope select full validation. Selection is sorted and deterministic.

| Scope | Validation |
| --- | --- |
| Markdown or documentation assets only | `git diff --check` |
| Dashboard only (optionally with docs) | npm clean install, typecheck, lint, dashboard tests and build, plus legacy dashboard contract tests |
| Reviewed deployment/proxy tooling only | Mapped Python contract tests, without .NET |
| Reviewed small .NET area with complete classification | Build solution once; Unit/Component baseline in both test projects; separately guarded direct area filters |
| Shared contracts, lifecycle, test/validation infrastructure, cross-project changes, or any unmapped inputs | Build solution once; unfiltered tests in each test project; policy and maintained dashboard package tests |
| Explicit `--full` | Same full path regardless of scope |

The coverage map in `tools/validate.py` deliberately starts small: Issue and execution identity presentation, and local repository discovery. Its fully qualified class/method filters are reviewed coverage declarations, not inferred from matching production and test filenames. Selection preserves every relevant boundary type in that area, including Integration tests. Local repository discovery includes real Git/process tests. Git/worktree lifecycle, SQLite, HTTP/TLS, leases, execution/recovery and all unmapped boundaries currently take the conservative full path. Expand the map only after reviewing the actual exercised contracts and integration boundaries. A mapping cannot authorize new files or changed tests; those changes select full validation.

Focused selection checks the current source metadata for exactly one recognized `TestType` on every Fact/Theory. Unsupported syntax or missing/ambiguous metadata selects full validation. `Duration` and `ValidationProfile=FastCandidate` from [28.3](test-runtime-profile-28.3.md) do not determine safety: a slow integration case cannot be dropped for its runtime. The benchmark inventory is immutable measurement evidence, not an evergreen coverage map.

The Unit/Component baseline requires both test projects and their production dependencies, including Server, so the build scope is the solution even for a small edit. `dotnet build -m:1` includes restore as needed. Every test run then uses `--no-build --no-restore`; tests do not rebuild those artifacts. A fresh worktree therefore always compiles before running tests.

## Results and failure behavior

The command emits one bounded `CODEX_VALIDATION_RESULT=` JSON line with selection mode/reason, build scope, run kind, project/filter, actual passing case count and wall duration per build/test/check. The Worker carries this metadata in `ValidationResult.Reports` and writes correlated scope/measurement lines for both successful and failed Validation passes. Raw logs and environment contents are excluded from metadata; Worker result parsing bounds fields and redacts configured secrets. This does not add a separate persistence or scheduling mechanism.

The shared TRX guard requires positive passing cases for **each** test invocation. Each area filter runs separately so the baseline cannot hide an empty or misspelled area. Unknown non-.NET inputs also select the conservative full path; deployment/proxy maps avoid unnecessary .NET tests where relevant coverage is reviewed. Counts come from the current TRX results, including theory rows; benchmark case counts are never hard-coded into policy. Failed/skipped cases, absent TRX, invalid filters, command errors, build errors and timeouts fail Validation. A malformed or contradictory metadata report also fails the Worker command even if its exit code is zero. Normal configured commands without metadata retain their existing behavior.

The 28.3 host measured Unit plus Component at 20.567 seconds versus 315.05 seconds for full tests, excluding compilation. Measure a reviewed focused pass and `--full` on the same host/artifacts to quantify this policy's improvement; compare the emitted build and test durations separately. Added regression tests exercise focused, boundary, unknown/full, explicit/full and non-.NET selection, build reuse/failure, metadata fallback and empty filters without requiring external services.

## Local measurement (28.4)

On the 28.3 host/toolchain, a representative Issue identity formatting plan ran against the current compiled sources. A local harness substituted `changes()` with `src/CodexWorker/IssueFormatting.cs`; everything after discovery was the real policy, including solution compilation, VSTest execution, TRX counts and guards. This is a plan measurement, not a claim that this infrastructure-changing task qualifies for focused validation.

| Run | Passing cases | Wall seconds |
| --- | ---: | ---: |
| Solution build (including up-to-date restore and dashboard generation) | — | 31.303 |
| Worker Unit/Component | 921 | 8.624 |
| Toolbox Unit/Component | 240 | 4.734 |
| Execution report area | 13 | 2.775 |
| Git branch/identity area | 14 | 2.775 |
| Operability area | 31 | 2.925 |
| Worker integration validation/reporting scenario | 1 | 2.928 |

Tests took **24.761 seconds**, and build plus tests **56.064 seconds**. The area runs deliberately overlap the baseline to independently guard each mapped selection. These are current observed counts, not policy constants. A single local sample is not a statistical performance guarantee. The historical 28.3 full test command took 315.05 seconds, excluding build.

The unfiltered .NET path also ran on the same host/artifacts: Worker passed 1,823 cases in 309.237 seconds and Toolbox passed 269 in 11.319 seconds, for **320.556 seconds** of tests. Its solution build took 31.150 seconds (351.706 seconds for build plus .NET tests). All cases passed without skips, including the seven new metadata-reporting regressions. The 19 Python policy/count checks and 52 maintained dashboard package tests passed too. Against this sample, the focused plan used about one thirteenth of the .NET test time and one sixth of the build-plus-test time. These local checks do not claim the Worker's separately configured authoritative checks have run.
