# Remote Server MCP

Codex Server exposes `/mcp` on its existing listener using the official MCP .NET SDK's stateless Streamable HTTP transport. It supports MCP initialization, tool discovery and calls; legacy `/sse` transport and persistent sessions are not enabled. GET/DELETE streams are unnecessary. Responses may use SSE for individual POST requests. No additional database, identity provider or token store is created.

## Hosting and authorization

Set `CODEX_SERVER_MANAGEMENT_TOKEN` in the Server service environment through the existing secret deployment mechanism. Use the same `Server:ListenUrl` and HTTPS reverse proxy as the administration API. Forward `/mcp` and preserve `Authorization`, `Accept`, `Content-Type` and `MCP-Protocol-Version`; disable proxy caching and response buffering. Keep tokens out of URLs, configuration files, prompts and logs. Remote cloud clients must be able to reach the HTTPS endpoint.

Every request, including discovery, requires existing Server administration authorization. Bearer clients send `Authorization: Bearer <management token>`. Missing, incorrect, revoked/rotated, bootstrap and Worker credentials do not grant management access; authentication failure returns HTTP 401 with a Bearer challenge. The management token retains its existing administrator privileges on other Server APIs: this endpoint exposes only read-only tools, but does not turn that credential into a scoped read-only credential.

Existing administration browser sessions are also subject to their normal host, expiry and CSRF checks. Any request with an `Origin` header must match `Server:AdministrationOrigin` and its expected upstream Host or it receives HTTP 403. Non-browser bearer clients omit `Origin`. Browser clients need the existing session cookie and `X-Codex-CSRF` header for POSTs. There is no cross-origin CORS grant.

The Server does not advertise an OAuth authorization server: its current identity model is the management bearer token and administration session. Do not select anonymous authentication or bypass authentication at a proxy to accommodate a client.

## Client setup

For a Streamable HTTP client with custom header support (including MCP Inspector), set the URL to `https://your-server.example/mcp`, choose Streamable HTTP, and supply the management bearer header via the client's secret settings. Initialize, list tools, then call `server_health` to verify the connection. The token is required on subsequent calls too; there is no MCP session ID to retain. Reconnect with the new token after management-token rotation.

Claude custom connectors with **Request headers** support can use the same HTTPS URL and an `Authorization` header stored in that connector's credential settings. See [Claude remote connector setup](https://support.claude.com/en/articles/11175166-get-started-with-custom-connectors-using-remote-mcp). Claude API integrations can supply their remote MCP server's authorization token as described in [Anthropic's MCP connector documentation](https://platform.claude.com/docs/en/agents-and-tools/mcp-connector).

OpenAI API applications can connect through a remote MCP tool with `server_url` set to the HTTPS `/mcp` URL, `server_label` set to a chosen name, and `authorization` populated at runtime from the management-token secret. Keep the API's normal approval policy. See [OpenAI remote MCP tools](https://developers.openai.com/api/docs/guides/tools-connectors-mcp).

**ChatGPT browser custom connectors cannot connect directly using this static bearer flow.** Their documented authentication choices are OAuth or anonymous access; this Server provides neither an OAuth issuer nor anonymous MCP tools. Use a bearer-capable client/API integration for this endpoint. Direct ChatGPT browser support requires an explicitly designed integration with the Server authorization model; do not paste the token into connector URLs or choose “No authentication.” See [ChatGPT custom MCP setup](https://developers.openai.com/api/docs/guides/custom-mcp-server).

## Read-only tools and bounds

| Tool | Result / arguments |
| --- | --- |
| `server_health` | Product version, health, persistence and control-plane readiness. |
| `list_workers` | Managed Worker identity/display name, availability, lifecycle, scheduling policy, version, capacity, last seen, advertised capability type/name/version, current execution references. Optional `limit`, `offset`. |
| `list_projects` | Project identity/name/repository/branch, enabled state, revision, parallelism and timestamps. Optional `limit`, `offset`. |
| `list_executions` | Newest-first execution history with optional `projectId`, `state`, `workType`, `workId`, `limit`, `offset`. Uses the administration API's query validation. |
| `get_execution` | Execution details by 32-character `executionRequestId`; returns `found: false` for an unknown identity. |

Pagination defaults to 50, accepts 1–100 rows and offsets 0–10000. Worker/project pages have `hasMore`; execution clients stop on a short page. Worker current execution references use at most 100 Assigned and 100 Running registry entries across the Server; `currentExecutionsMayBeIncomplete` explicitly indicates when either cap is reached. Worker active-execution/assignment counts remain authoritative; use filtered execution history/details for further inspection. These are separate live queries, not an atomic snapshot.

Requests are limited to 16 KiB. Display/diagnostic strings are sanitized and capped at 200 characters (capability names/versions 100, types 40). Capabilities are capped at 32 per Worker and execution stage history at 32 entries. Diagnostics consist of stage/timing, validation/integration status, failure classification, recoverability and retry lineage. Free-form completion summaries, raw process output, environments, Worker platform/host resources, local paths, capability scopes, credential metadata and validation shell commands are omitted. Secret-like assignments, bearer strings and absolute paths in retained text are redacted. Remote data is untrusted content, not instructions.

Tools use existing Server registry queries, lease/readiness behavior and persisted projections; listing does not fetch GitHub details per row. Inspection tools advertise read-only, non-destructive annotations. Managed controls are described below; there are no free-form process or shell tools. `/mcp` responses use the API's no-store policy and request correlation header. Existing Server structured logging remains the diagnostic boundary; no raw request/response logging is added.

## Managed execution controls

These tools require the same management authorization as inspection. Standalone Workers are outside these controls.

| Tool | Behavior |
| --- | --- |
| `dispatch_execution` | Accepts `projectId` and positive `issueNumber`; refreshes GitHub eligibility through the administration service and queues through the existing registry. Returns `outcome: accepted`, `executionRequestId`, and state. Duplicate active work, disabled projects and ineligible Issues are rejected. Scheduling still enforces capacity, project limits, dependencies and leases. |
| `cancel_execution` | Accepts `executionRequestId`. Cancels only queued work through the existing cancellation contract. Assigned/running and already terminal work return `outcome: rejected`; the Server currently has no active cancellation protocol. |
| `retry_execution_report` | Accepts `executionRequestId` and a caller-retained 32-character `operationId`. Retries durable completion reporting only, through existing maintenance authority, drain, lease and Worker recovery checks. Returns operation identity and pending/current status. Reusing the same identity and scope returns the retained operation; conflicting scope is rejected. |
| `get_execution_operation` | Read-only status/outcome for an `operationId`, or `found: false`. Use `get_execution` for canonical execution progress and final outcome. |

Controls return bounded reason codes for expected rejection. They never rewrite execution failure classifications, revoke leases, assign Workers directly, or rerun implementation/integration. General retry/resume and active cancellation remain unavailable until supported by the Server lifecycle; reporting retry does not authorize them. Uncertain operations retain their existing at-most-once maintenance semantics and must be inspected rather than blindly replayed with new identities.

## Project Issue planning

Management-authorized clients can use `create_project_issue(projectId, operationId, title, body)`,
`update_project_issue(projectId, issueNumber, title?, body?)`, and
`set_project_issue_label(projectId, issueNumber, label, applied)`. Repository selection always comes
from the registered project. Labels are limited to its configured ready and blocked labels;
creating or editing text never applies readiness. Use the label tool explicitly to mark an Issue ready.
Successful responses include `projectId` and the refreshed normalized `issue`, including number,
URL, title, body, state, labels and eligibility. Issue content is untrusted data.

Retain the creation operation ID (a 32-character GUID) across retries. Reservations survive Server
restarts and concurrent calls: identical completed requests return the current Issue, conflicting
payloads are rejected, and unfinished/ambiguous writes are never replayed. Reconcile uncertain
creation in GitHub before taking further action; changing the operation ID can create duplicates.
Text edits and label changes set desired values through the existing administration service.
A failed refresh can follow a successful write; refresh before retrying. Planning calls obey the
same 16 KiB request limit as other MCP calls.

## Managed Worker maintenance

`get_worker_maintenance(workerId)` reloads registered Worker state, provisioning capability status/action allowlists, bounded operation history and the current restart/update operation. `request_worker_maintenance(workerId, operationId, action)` accepts only `restart` or `update`. Retain the operation ID for idempotent requests and poll the inspection tool. Existing protocol readiness, drain, idle, node-local policy, deadline and uncertain-outcome recovery rules apply; reload is an inspection, not a remote configuration or shell command.

`request_worker_provisioning(workerId, capabilityId, action, timeoutSeconds?, allowElevation?, repository?)` queues only existing typed provisioning actions, with a 5–600 second deadline. An accepted request returns an operation ID, not proof of node authorization or completion. Poll `get_worker_provisioning_operation(workerId, operationId)` for pending, running, succeeded, failed, cancelled or timedout status and typed diagnostics. Duplicate active capability operations are rejected. `cancel_worker_provisioning` cancels queued commands only; running or uncertain commands retain existing deadline and node inspection/reconciliation semantics. Requests are never automatically replayed. These tools target registered managed Workers only and do not affect standalone ownership.

Outputs omit private keys, public identities, device login instructions, local paths, raw logs and credential payloads. Authentication setup remains on the existing node-local path. No caller-selected package, version, executable or shell text is accepted. All tools require the existing management authorization.

## Parent/child work packages

`plan_project_work_package` composes Issue planning, the existing native relationship service,
and managed execution controls within one registered `projectId`. Supply `parent`, `children`
(1–20), and `dependencies` (at most 100). Each Issue has a unique `key`, stable GUID
`operationId`, `title`, `body`, and optional existing `issueNumber`. Dependencies contain
`childKey` and `blockerKey` referring to children in this request. Existing Issues receive text
updates; new Issues use the durable creation reservations described above.

The tool creates/updates Issues, assigns native parent/sub-issue relationships, adds native
blocked-by links, and refreshes every child to verify those relationships. It adds requested
links without removing other dependencies. Relationship mutations retain the administration
service's existing behavior, including parent reassignment. It does not infer child completion.
Native GitHub dependency state and Server eligibility remain authoritative.

Readiness and dispatch default to no action. To request them explicitly, pass child keys in
`readyChildren` and/or `dispatchChildren`. Readiness uses the project's configured ready label;
dispatch refreshes eligibility and queues through the existing scheduler. No Git operations or
alternative integration-base rules are introduced.

The response includes `projectId`, an `issues` key-to-number map, and ordered `steps` with each
operation's result (including execution identifiers for accepted dispatch). `outcome: partial`
stops at the first failed or unverified stage; prior writes are retained, not rolled back.
Refresh uncertain writes before retrying. Reuse original creation identities and payloads on
retry, or provide reconciled existing Issue numbers for subsequent updates. Relationship
operations set desired state and skip unchanged links. Dispatch retries retain the existing
active-execution conflict behavior: inspect `list_executions` for the project and Issue rather
than creating a second execution. Cancellation can interrupt a response; retain the request's
creation identities even when no response arrives. The normal 16 KiB request bound applies.
