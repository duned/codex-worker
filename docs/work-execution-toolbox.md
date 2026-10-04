# Work Execution Toolbox

`WorkExecutionToolbox` is a standalone .NET 10 library. It has no package or project dependencies on Codex Server, Codex Worker, provisioning, or `cw`. A host can reference `src/WorkExecutionToolbox/WorkExecutionToolbox.csproj` directly. The independent solution includes the library, standalone CLI and test host:

```sh
dotnet build src/WorkExecutionToolbox/WorkExecutionToolbox.sln
dotnet test src/WorkExecutionToolbox/WorkExecutionToolbox.sln
```

The projects are also included in `CodexWorker.sln` so normal repository validation includes the toolbox. The independent solution lives in the toolbox directory so root-level `dotnet build` and `dotnet test` select the repository solution unambiguously. Worker references the toolbox for its CLI authentication host adapter; the library remains independent of the applications.

`RepositoryContext` carries a provider's repository identifier; it carries no project configuration, local checkout, credentials, or scheduling authority. Providers validate their own repository syntax. `IssueReference` pairs that context with a positive human Issue number. Summaries and relationships retain that scope, including for cross-repository reads. Internal database IDs, GraphQL node IDs and HTTP payloads do not belong in these contracts.

`IIssueDependencyProvider` exposes Issue reads, direct blocked-by reads and single/batch dependency operations. `IIssueRelationshipProvider` inherits it and adds organizational relationship reads and parent operations. `SetParentRequest` sets a parent or clears it with `null`; assigning a parent is also how a child is added. `SetDependencyRequest` adds/removes a blocked-by prerequisite with `Applied`. Mutations are scoped to one repository, reject nonpositive numbers and self-links at construction, and support a read-only preview. Parent/child links are organizational and do not imply dependencies. `BlockedBy` contains prerequisites, while `Blocking` contains dependents.

Providers preflight visibility and return typed changed, unchanged, preview, conflict, failed or partial results for operational outcomes. A different existing parent returns conflict and requires an explicit clear before setting a new parent. Cancellation remains cancellation rather than a failed relationship result. Read methods return `null` for missing or invisible Issues; transport/authentication failures must retain actionable, redacted context rather than masquerade as missing Issues.

`IIssueProvider` defines Issue reads and is inherited by `IIssueDependencyProvider`. `GitHubIssueProvider` implements Issue reads and blocked-by operations using a host-owned `HttpClient` and an injected async credential callback. It resolves human numbers through the repository-scoped REST endpoint, keeps database IDs internal, rejects pull requests, and validates the returned number, state and repository URL. Missing or invisible Issues and pull requests return `null`. Authorization, rate-limit, transport and malformed-response failures use `GitHubIssueException` with a typed failure; cancellation propagates. Response bodies, tokens and raw underlying exceptions are excluded from diagnostics. Responses are bounded to 1 MiB. The provider implements `IIssueRelationshipProvider` and `IIssueGraphProvider`; command parsing remains outside the library.

`GitHubIssueProvider.SetParentAsync` uses the existing `SetParentRequest`, and `ClearParentAsync` explicitly clears a parent. Repeated set/clear requests return `Unchanged` when the state already matches. A different parent returns `Conflict` without writing; replacement requires a separate explicit clear. Previews run visibility, scope and cycle checks without writing. Ancestors are read from GitHub and checked for cycles before assignment, with a safety bound of 1,000 ancestors. Writes use the resolved child's internal ID with GitHub's [native sub-issue endpoints](https://docs.github.com/en/rest/issues/sub-issues), never labels, body text or local storage, and never request implicit parent replacement. After a write, the provider verifies the child is still visible with the same identity and reads its native parent back. A verified mismatch returns `Failed` with safe diagnostics. Even after a write error, the provider reads back state: a matching state returns `Changed`, while an invisible child or failed verification returns `Partial`. It never replays the write; callers must refresh before retrying uncertain results. Cancellation propagates even after sending a write.

`SetParentsRequest` and `SetParentsAsync` assign one parent to 1–50 distinct children.
`ParentBatchResult` includes the parent number and an ordered `ParentChangeResult`
for every child. The provider reuses single-parent preview checks for the entire
batch before writing, then rechecks each child during mutation. Preflight rejection
writes nothing; unsuccessful or uncertain mutations stop remaining operations.
Verified successes remain observable without rollback or automatic retry.

`GetParentAsync` reads the native parent; `ListChildrenAsync` lists direct children with 100-item pages. Missing Issues or pull requests return `null`; a visible parent with no children returns an empty list. Relationship payloads must contain actual Issues in the explicitly selected repository: cross-repository links, pull requests and malformed responses are rejected. Child listings are limited to 100 pages and fail rather than silently truncate at that bound. Read errors retain typed `GitHubIssueException` diagnostics. `GetRelationshipsAsync` completes the existing relationship contract by returning the direct parent, children, blocked-by prerequisites and blocking dependents from GitHub’s native endpoints. It rejects self-links and inconsistent identities across the returned lists.

`GitHubRepositoryContext.Create` requires explicit GitHub.com `owner/name` syntax. Every read validates its context before acquiring credentials or sending HTTP. There is no fallback to `GH_REPO`, `gh` defaults, HTTP base addresses or a different repository. Hosts may offer `GitHubRepositoryContext.Discover` using local remote URLs they have read: it returns a context only when all supplied URLs identify the same GitHub.com repository; empty, ambiguous or unsupported remotes return `null`. An explicit repository takes precedence over discovery. Writes carry that selected context through the existing repository-scoped requests.

The library neither invokes `gh` nor discovers authentication in environment variables or CLI state. Worker’s `GitHubToolboxHost` supplies the CLI boundary, invoking `gh auth token --hostname github.com` with a 30-second deadline via the existing process runner. It uses the current node-local authentication, retains no token store, and never includes CLI output or process exceptions in diagnostics. It does not log in or modify authentication. The token is applied only to the API request rather than shared HTTP default headers. HTTP client ownership remains with the host; configure its timeout and trusted handlers appropriately. Command parsing, presentation, queue eligibility and execution lifecycle stay outside the library. No provider registry or plugin system is required.

The separate xUnit test project references the library and independent CLI and covers repository discovery, scoped Issue resolution, input/response validation, missing Issues, pull requests, typed failures, credential redaction and cancellation through deterministic HTTP fakes. Native parent tests also cover set/clear and idempotent repeats, explicit-clear conflicts, cycles, previews, missing Issues, repository scope, pull requests, paginated direct children, API failures and post-write verification. Worker tests cover the CLI credential boundary without invoking `gh` or using live credentials.

## Native GitHub blocked-by dependencies

For target Issue N, blocker M is a prerequisite: M blocks N. `GetBlockedByAsync` reads only N's direct prerequisites, with bounded pagination; it does not infer dependencies from parent/child links or labels. All dependency numbers use the target's explicit repository. Responses from another repository or pull requests are rejected rather than interpreted in that scope.

`SetDependencyAsync` adds/removes one prerequisite; `SetDependenciesAsync` accepts 1–50 distinct blocker numbers and returns a `DependencyChangeResult` for each. Requests copy their number list and reject invalid numbers and self-links before HTTP. The provider resolves the target and every blocker before writing, skips already satisfied relations, and traverses each new blocker's blocked-by graph to reject cycles. Traversal is bounded to 1000 Issues and lists to 100 pages of 100 items; exceeding a bound fails preflight rather than authorizing a write with an incomplete graph. Preview performs the same preflight without mutations.

Writes use GitHub's native dependency endpoints and resolve internal database IDs only inside the provider. Each attempted mutation is followed by a direct-list read. A successful HTTP response that does not produce the requested state fails verification; an errored response with the requested state verified is changed. Batches preserve per-relation outcomes and report partial when verified changes coexist with failures. An unverifiable write is partial/uncertain, stops remaining writes, and requires refreshing before retrying. Cancellation propagates, so callers cancelling after a write must also refresh. There is no rollback, automatic retry, lifecycle label mutation or scheduling side effect. Concurrent external changes remain subject to GitHub's write-time validation and read-back checks.

Deterministic HTTP tests cover add/remove and batch idempotency, preview, scoped IDs and pagination, self-links and invalid input, missing Issues and pull requests, transitive cycles and shared graph branches, partial writes, lost responses and read-back failures.

## Read-only relationship graphs

`IIssueGraphProvider.GetGraphAsync` builds a graph directly from GitHub on each call, without a relationship store or scheduling effects. `IssueGraph` contains repository-scoped Issue summaries (human numbers, titles, open/closed states and URLs) and typed edges. `ParentChild` points from parent to child; `BlockedBy` points from the blocked Issue to its prerequisite. Parent, child, prerequisite and dependent references are expanded breadth-first, once per Issue, at their shortest distance from the selected root. Shared references are normal and do not imply cycles.

`IssueGraphOptions` defaults to depth 5, 100 Issues, 500 edges, 1000 HTTP requests and 10 pages per relation. Supported maxima are depth 20, 200 Issues, 2000 edges, 5000 requests and 100 pages per relation; depth zero reads only the root’s relationships. Pages contain at most 100 items. Depth excludes new nodes beyond the bound and sets `IsDepthTruncated`; all included Issues are resolved and their direct relations read. Exhausting any other limit throws `GitHubIssueException` with `LimitExceeded`, rather than returning an apparently complete graph. Direct relationship reads retain their 100-page bound. A full final page requires another page to establish completion, so exhausting the page budget on a full page also fails.

Each expanded Issue is resolved against GitHub to verify visibility and internal identity. Graph validation rejects mismatched identities, duplicate paginated results, self-links, pull requests, cross-repository references and asymmetric parent/child or blocked-by/blocking results between included endpoints. API failures remain typed failures and cancellation propagates. Reads are not an atomic GitHub snapshot: inconsistent results can indicate concurrent edits and require a fresh read. Relations beyond the depth boundary are not validated reciprocally, and truncation cannot certify an entire repository graph as valid or acyclic.

Cycle detection uses separate directed hierarchy and dependency graphs, with iterative traversal. `CycleDetected` reports detected cycles and `IsCycle` marks cycle-closing edges; a child blocked by its parent alone is valid. Graph shape, mixed edge types, shared nodes, pagination and traversal bounds, cycles, inconsistent results, pull requests, API errors, cancellation and metadata redaction have deterministic HTTP regression coverage. The provider uses GitHub’s [sub-issue](https://docs.github.com/en/rest/issues/sub-issues) and [dependency](https://docs.github.com/en/rest/issues/issue-dependencies) read endpoints only during inspection.


## Standalone planning CLI (`wet`)

The CLI references only the independent toolbox library, with no Server/Worker dependency.
Install the .NET 10 SDK and GitHub CLI (`gh`), then build and test with the independent
solution commands above. Ordinary builds and tests never install tools or change PATH.

For repository development, use the existing `cw d` Worker deployment workflow.
The development .NET tool convention is `$HOME/.local/share/wet` (override with
`CW_WET_TOOL_DIR`). `cw d` creates an executable launcher at
`$HOME/.local/bin/wet` pointing to that development installation. Ensure
`$HOME/.local/bin` is on your current and login-shell PATH before running `cw d`.
Bash and Zsh login shells are supported (selected by `SHELL`, default Bash).
`cw` checks a fresh login shell and never edits shell profiles or PATH. If your
login setup lacks this conventional user command directory, configure it explicitly
in your own shell setup so it persists across SSH reconnects:

```sh
export PATH="$HOME/.local/bin:$PATH" # Current shell; login setup must also include it.
cw d
wet --version
wet --help
```

`cw d` publishes Worker, packs the independent WET CLI from the configured checkout,
and installs or replaces only `WorkExecutionToolbox.Cli` in the development tool
directory before stopping/replacing Worker. Reinstallation uses an isolated package
cache so source changes with the same product version are picked up. No GitHub
authentication is required for this refresh. Packaging, installation or PATH failures
fail the command with a diagnostic before Worker replacement. An installation failure
can leave development WET absent; fix the reported error and rerun `cw d`.
The existing service deployment still requires non-interactive sudo permission.

Use `type -a wet` and `command -v wet` to identify installations and PATH precedence.
`cw d` checks the installed package version and `wet --version`, and fails if
current or login-shell PATH selects another installation or cannot find the managed
command. Put `~/.local/bin` before other WET commands and rerun. The launcher is
recognized by its exact managed content and updated idempotently, including when
`CW_WET_TOOL_DIR` changes. Existing unrelated files or symlinks at `~/.local/bin/wet`
are preserved and cause an actionable failure; move them aside yourself only if you
intend to select the development installation. If your shell cached an older
command path, run `hash -r` (Bash) or `rehash` (Zsh) before verification.
Unrelated installations and published-release directories are not updated.

Manual .NET tool packaging remains supported:

```sh
dotnet pack src/WorkExecutionToolbox.Cli/WorkExecutionToolbox.Cli.csproj -c Release -o /tmp/wet-packages
dotnet tool install WorkExecutionToolbox.Cli --source /tmp/wet-packages --tool-path /tmp/wet-manual
/tmp/wet-manual/wet --version
/tmp/wet-manual/wet --help
```

The package uses the shared source `Version` from `Directory.Build.props`; an
explicit `-p:Version=VERSION` pack override does not edit the source version.
`wet --version`, `wet -v` and `wet v` print `wet VERSION` from assembly informational
version metadata (excluding supplementary revision metadata). They work without a
checkout, GitHub authentication/API access or cache initialization for .NET tools
and published executables alike.

Local regression checks are `bash tests/cw-tests.sh` (stubbed development deployment)
and `bash tests/wet-tool-tests.sh` (.NET SDK build, packaging, temporary tool installation
and published-executable checks). Both avoid the real development tool installation.

`wet` works outside the source checkout and needs the
.NET 10 runtime. A direct publish is also supported using the repository's
self-contained .NET publishing convention:

```sh
dotnet publish src/WorkExecutionToolbox.Cli/WorkExecutionToolbox.Cli.csproj -c Release -r linux-x64 --self-contained true -o /tmp/wet-publish
/tmp/wet-publish/wet --help
```

Copy the **complete** publish directory to your desired installation location;
self-contained output does not require a separately installed .NET runtime.
No deployment, service installation or release publication is required.

Each product release also publishes `wet-VERSION-linux-x64.tar.gz` as a
self-contained Linux x64 artifact. On Ubuntu 24.04 x86_64, download and extract
that archive, then run `wet --help`; it needs no source checkout or .NET runtime.
See the [published release install steps](release-packaging.md#installing-published-releases).

Authenticate once through GitHub CLI:

```sh
gh auth login --hostname github.com
gh auth status --hostname github.com
```

The CLI invokes `gh auth token --hostname github.com` with a 30-second deadline,
keeps credentials only in memory for the request, and never saves tokens or prints
credential-bearing process output. It neither logs in nor modifies gh state.
Repository permissions must allow the requested native Issue relationship operation.

Use `--repo owner/name` to explicitly select a repository; this always wins and
never inspects the current checkout. Without it, WET reads the current checkout's
local Git fetch/push remotes with a bounded, cancellable invocation and applies
`GitHubRepositoryContext.Discover`. All remotes must identify one unambiguous
GitHub.com repository (HTTPS or SSH). A missing checkout, no remotes, unsupported
or malformed remotes, or distinct repositories fail before authentication and ask
for `--repo owner/name`. Remote URLs and Git diagnostics are never printed.
WET never uses `GH_REPO`, gh defaults or other ambient repository fallbacks.
All positional arguments are positive human GitHub Issue numbers in that repository:

```sh
wet parent set 9 3 --repo owner/name
wet parent set 9 10 3 --repo owner/name
wet parent clear 9 --repo owner/name
wet children 3 --repo owner/name
wet dependency add 9 3 4 --repo owner/name
wet dependency remove 9 4 --repo owner/name
wet relationships 9 --repo owner/name
wet graph 9 --repo owner/name --json
wet graph 9 --repo owner/name --refresh
```

From an unambiguous GitHub.com checkout, repository discovery applies to all commands:

```sh
wet parent set 9 10 3
wet relationships 3
wet graph 9 --json
```

`wet parent set CHILD... PARENT [--repo owner/name]` accepts 1–50 distinct
children followed by one parent; duplicate children and self-parent requests are
rejected before authentication. The complete batch is preflighted before writes,
then each child is rechecked and its write verified. A rejected preflight performs
no writes. Results preserve each child's outcome in input order, including
unchanged relationships and children not attempted. Failed, conflicting or uncertain
operations stop the mutation phase; refresh relationships before retrying. There
is no automatic rollback or replay of remaining children.

Parent/child relationships organize Issues; they do not block execution. Setting
Issue 3 as Issue 9's parent does not make 3 a prerequisite. `dependency add 9 3`
means **Issue 9 is blocked by Issue 3**. Removal removes this prerequisite.
Dependency batches accept 1–50 distinct blockers. A different existing parent
returns conflict; explicitly clear it before assigning a replacement. The library
preflights and verifies writes. The CLI does not add retries, rollback, scheduling,
labels, comments or Issue state changes. `children` selects the children from the
library's direct relationship inspection. `graph` uses the library's default
bounded traversal and displays depth truncation and detected cycles.

Human `graph` output renders parent/child links as hierarchy trees, with Issue
number, state and title at each node. The requested Issue is marked `(root)`;
its topmost available ancestor starts the first tree. Children and compact
`blocked by #N, #M` annotations are ordered by Issue number. A parent is never
implicitly a blocker, and dependencies are not repeated as inverse blocking edges.
Other discovered Issues (including dependency-only prerequisites and dependents)
appear under `Related branches`, with hierarchy roots first, then any remaining
cyclic branches, in numeric order. Titles appear once; shared hierarchy nodes use
numbered references. Hierarchy cycle references stop expansion, dependency cycle
edges are marked in annotations, and depth truncation is reported for the graph
as a whole. `--json` retains the versioned node/edge graph contract and directions.

Options may appear anywhere. `--help` / `-h` requires no credentials or repository.
Ctrl+C cancels pending work. HTTP requests have a 60-second timeout. After a failed,
partial or cancelled write, read `relationships --refresh` before retrying: a write may have
taken effect even if its response could not be verified.

### JSON contract and exit codes

`--json` writes one JSON object, with camelCase keys and enum string values, and
no human progress text. Successful reads and mutation outcomes go to stdout:

```json
{"schemaVersion":1,"command":"parent set","repository":"owner/name","issue":9,"data":{"status":"changed","diagnostic":null}}
```

`data` for `children` is an array of Issue summaries; for `relationships` it has
`issue`, `parent`, `children`, `blockedBy`, and `blocking`; for `graph` it has
`root`, `issues`, `edges`, `isDepthTruncated`, and `cycleDetected`. Issue references
have `repository: {repository: "owner/name"}` and `number`; summaries have `issue`,
`title`, `state` (`open` or `closed`) and `url`. Graph edges have `fromIssueNumber`,
`toIssueNumber`, `kind` (`parentChild` or `blockedBy`), and `isCycle`. `blockedBy`
edges point from the blocked Issue to its prerequisite. Dependency batch data has
`status` and `relations`, each with `blockerIssueNumber` and `result` (status and
diagnostic). Multi-child parent data has `parentIssueNumber`, `status` and
`relations`, each with `childIssueNumber` and `result`. The envelope's `issue`
remains the first child. Single-child parent output retains its existing
`status`/`diagnostic` data shape; batches emit one JSON document. Status strings are `changed`, `unchanged`, `preview`, `conflict`,
`failed`, or `partial`. Arrays follow provider order; callers should use numbers
rather than positions for identity. Null fields are retained.

Usage, missing Issue, provider and host errors go to stderr with empty stdout:

```json
{"schemaVersion":1,"error":{"code":"missingIssue","message":"Issue is missing or not visible in the selected repository."}}
```

Error codes are `usage`, `missingIssue`, `authorization`, `rateLimited`,
`invalidResponse`, `transport`, `provider`, `limitExceeded`, `hostFailure`,
`cancelled`, and `timeout`. Diagnostics are actionable text, not a parsing contract;
use `schemaVersion`, codes and statuses for automation. Mutation outcomes remain
on stdout even when their status gives a nonzero exit code.

| Exit | Meaning |
| --- | --- |
| 0 | Successful read or changed/unchanged mutation |
| 1 | Provider, authentication, timeout, host or failed mutation |
| 2 | Invalid command/options, numbers or repository |
| 3 | Missing or invisible Issue on a read |
| 4 | Relationship conflict requiring an explicit decision |
| 5 | Partial or uncertain mutation; refresh before retrying |
| 130 | User cancellation; refresh before retrying a write |

CLI tests use fake library providers without live GitHub calls, covering routing,
validation before authentication, human and versioned JSON output, graph direction,
mutation statuses, partial batch results, missing Issues, safe failures and cancellation.


## Consume the library from another host

Reference only `WorkExecutionToolbox.csproj`; no CLI, Worker or Server reference is
needed. Supply a host-owned HTTP client and async credential provider. The callback
below is your host's credential boundary, not a toolbox token store:

```csharp
using WorkExecutionToolbox;

static async Task InspectAsync(
    HttpClient http,
    Func<CancellationToken, Task<string>> getToken,
    CancellationToken cancellationToken)
{
    var github = new GitHubIssueProvider(http, getToken);
    var repository = GitHubRepositoryContext.Create("owner/name");
    var child = new IssueReference(repository, 9);
    IIssueRelationshipProvider relationships = github;
    var current = await relationships.GetRelationshipsAsync(child, cancellationToken);
    // Preview validates visibility and cycles without changing GitHub.
    var preview = await relationships.SetParentAsync(
        new SetParentRequest(child, 3, previewOnly: true), cancellationToken);
    IIssueGraphProvider graphs = github;
    var graph = await graphs.GetGraphAsync(child,
        new IssueGraphOptions { MaxDepth = 2, MaxIssues = 25 }, cancellationToken);
}
```

For writes, use the same requests without `previewOnly`. Clear a parent with
`new SetParentRequest(child, null)`. Add prerequisites with
`new SetDependenciesRequest(child, [3, 4], applied: true)`; remove them with
`applied: false`. Handle typed statuses, including `Partial`, before attempting
another write. Read failures throw `GitHubIssueException`; cancellation propagates.
Do not log the credential callback's results or raw host exceptions.

## GitHub permissions and supported scope

The provider targets GitHub.com and pins REST API version `2022-11-28`; GitHub
Enterprise hosts are not supported. Native relationship reads require repository
visibility and, for fine-grained credentials, Issues read permission. Mutations
require Issues write permission and the authenticated user's access to the selected
repository. See GitHub's [sub-issue API](https://docs.github.com/en/rest/issues/sub-issues)
and [dependency API](https://docs.github.com/en/rest/issues/issue-dependencies).
GitHub may reject writes because of relationship constraints or rate limits.
The toolbox does not retry them automatically.

All related Issues must belong to the selected repository. Cross-repository native
relationships are rejected, even if GitHub supports them. Missing and invisible
Issues cannot always be distinguished. Relationships organize and describe
prerequisites; the toolbox does not schedule work or complete Issues.

## Opt-in developer smoke test

Run this workflow only against a dedicated test repository and disposable Issues
that you control. It performs real writes. Substitute its explicit `owner/name`
and three actual Issue numbers for the examples (parent 3, child 9, prerequisite 4).
Start with no parent or dependencies on child 9; never use production planning
Issues for this check. Authentication and creation of those disposable Issues are
operator prerequisites, separate from automated validation.

```sh
wet relationships 9 --repo owner/name --json
wet parent set 9 3 --repo owner/name --json
wet parent set 9 3 --repo owner/name --json
wet children 3 --repo owner/name --json
wet dependency add 9 3 4 --repo owner/name --json
wet dependency add 9 3 4 --repo owner/name --json
wet relationships 9 --repo owner/name --json
wet graph 9 --repo owner/name --json
wet dependency remove 9 3 4 --repo owner/name --json
wet parent clear 9 --repo owner/name --json
wet relationships 9 --repo owner/name --json
wet children 3 --repo owner/name --json
```

Check that the first mutations report `changed`, repeated requests report
`unchanged`, child 9 appears under parent 3, and Issue 9's `blockedBy` contains
3 and 4. The graph should show 3 → 9 as `parentChild` and 9 → 3 / 9 → 4 as
`blockedBy`, without treating mixed edge kinds as a cycle. After removal, child 9
has no parent or blockers and parent 3 no longer lists it. If any write fails,
returns partial or is cancelled, inspect current relationships before continuing;
cleanup must use only the disposable relationships you created.

## GitHub read caching and refresh

The standalone CLI persists validated Issue summaries, stable database identities, and complete
parent, children, blocked-by and blocking sets under the current user's local application data
directory, `wet/github-issue-cache`. On Linux this normally uses `$XDG_DATA_HOME` or
`~/.local/share`. This cache belongs to WET and needs no Server, Worker or `cw` installation.
Repository names (case insensitive) and human Issue numbers scope every entry. Only minimal
validated read data is stored; credentials and raw GitHub response bodies are excluded. Invalid,
unsupported or unreadable entries are treated as misses. Cache writes use atomic replacement.

Open Issue metadata and relationships expire after **30 seconds**; closed Issue data expires
after **7 days**. Closed state alone selects the longer lifetime, with no Worker label policy.
These are read optimizations, not an atomic GitHub snapshot or a guarantee that state remains
unchanged. Missing/invisible Issues are not persistently negative-cached. Graph reciprocity,
identity, cycle, depth, Issue and edge checks remain active. HTTP request budgets count actual
cache misses; cached lists still obey relationship pagination limits.

Use `--refresh` with `children`, `relationships` or `graph` to bypass persistent mutable data.
Stable identities stay cached, and equivalent reads are deduplicated within the command even
with refresh. Refresh is rejected for mutation commands: mutation preflight and verification
always read fresh state. Before and after each attempted write, all mutable data for the
repository is invalidated, including the reverse relationships. Uncertain and cancelled writes
also invalidate it; refresh authoritative relationships before retrying. Identity mappings are
retained. Generation markers prevent reads started before a mutation from republishing stale
data into the current cache generation.

Library hosts can supply `GitHubIssueCacheOptions` to `GitHubIssueProvider`, setting
`DirectoryPath` to their own standalone cache location and optionally supplying `TimeProvider`.
Without a directory, only operation deduplication is enabled. `BeginReadOperation(refresh: true)`
on `ICacheAwareIssueProvider` provides explicit freshness for library callers. Public read methods
create a scope automatically when needed; hosts may wrap several reads in one scope. Dispose
the scope in the caller's execution context. Internal GitHub IDs remain behind the provider.

Cold inspections overlap the four independent relationship endpoints per Issue with bounded
concurrency, while joining every read on failure or cancellation. A deterministic 15-Issue
hierarchy/dependency overlap regression counts **75 unique cold REST
reads** and **zero warm reads** from a new provider using the same persisted cache. This protects
request efficiency without live GitHub credentials; it is not a measured live campaign timing.
