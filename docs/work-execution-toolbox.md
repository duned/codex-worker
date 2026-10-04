# Work Execution Toolbox foundation

`WorkExecutionToolbox` is a standalone .NET 10 library. It has no package or project dependencies on Codex Server, Codex Worker, provisioning, or `cw`. A host can reference `src/WorkExecutionToolbox/WorkExecutionToolbox.csproj` directly. The independent solution includes only the library and its test host:

```sh
dotnet build src/WorkExecutionToolbox/WorkExecutionToolbox.sln
dotnet test src/WorkExecutionToolbox/WorkExecutionToolbox.sln
```

The projects are also included in `CodexWorker.sln` so normal repository validation includes the toolbox. The independent solution lives in the toolbox directory so root-level `dotnet build` and `dotnet test` select the repository solution unambiguously. This does not add a toolbox dependency to the existing applications.

`RepositoryContext` carries a provider's repository identifier; it carries no project configuration, local checkout, credentials, or scheduling authority. Providers validate their own repository syntax. `IssueReference` pairs that context with a positive human Issue number. Summaries and relationships retain that scope, including for cross-repository reads. Internal database IDs, GraphQL node IDs and HTTP payloads do not belong in these contracts.

`IIssueRelationshipProvider` exposes Issue and relationship reads plus the initial single-relationship operations. `SetParentRequest` sets/replaces a parent or clears it with `null`; assigning a parent is also how a child is added. `SetDependencyRequest` adds/removes a blocked-by prerequisite with `Applied`. Mutations are scoped to one repository, reject nonpositive numbers and self-links at construction, and support a read-only preview. Parent/child links are organizational and do not imply dependencies. `BlockedBy` contains prerequisites, while `Blocking` contains dependents.

Providers preflight visibility and return typed changed, unchanged, preview, failed or partial results for operational outcomes. A parent replacement can be partial if removing the previous parent succeeds and assigning the new one fails; callers must refresh before retrying. Cancellation remains cancellation rather than a failed relationship result. Read methods return `null` for missing or invisible Issues; transport/authentication failures must retain actionable, redacted context rather than masquerade as missing Issues.

This foundation defines contracts, not a GitHub transport or command surface. A GitHub implementation must resolve human numbers to its private IDs internally and accept host-supplied HTTP/authentication dependencies (for example, an `HttpClient` configured by the host). The library neither invokes `gh` nor discovers authentication in environment variables or CLI state. HTTP client ownership and credential acquisition stay with the host. Command parsing, presentation, queue eligibility and execution lifecycle stay outside the library. No provider registry or plugin system is required: hosts supply an `IIssueRelationshipProvider` directly.

The separate xUnit test project references only the library and demonstrates a host implementation, scoped relationship reads, mutation requests, input rejection and cancellation without live services.
