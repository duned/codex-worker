# Remote Server MCP

Codex Server exposes `/mcp` on its existing listener using the official MCP .NET SDK's stateless Streamable HTTP transport. It supports MCP initialization, tool discovery and calls; legacy `/sse` transport and persistent sessions are not enabled. GET/DELETE streams are unnecessary. Responses may use SSE for individual POST requests. OAuth grants and hashed reference credentials use the existing Server database; no external identity provider is required.

## Hosting and authorization

Set `CODEX_SERVER_MANAGEMENT_TOKEN` in the Server service environment through the existing secret deployment mechanism. Use the same `Server:ListenUrl` and HTTPS reverse proxy as the administration API. Forward `/mcp` and preserve `Authorization`, `Accept`, `Content-Type` and `MCP-Protocol-Version`; disable proxy caching and response buffering. Keep tokens out of URLs, configuration files, prompts and logs. Remote cloud clients must be able to reach the HTTPS endpoint.

Every request, including discovery, requires an active scoped OAuth access token or existing Server administration authorization. Bearer clients send `Authorization: Bearer <management token>`. Missing, incorrect, revoked/rotated, bootstrap and Worker credentials do not grant management access; authentication failure returns HTTP 401 with a Bearer challenge. The management token grants administrator privileges, including the mutation tools below; it is not a scoped read-only credential. Configure client approval for mutations accordingly.

Existing administration browser sessions are also subject to their normal host, expiry and CSRF checks. Any request with an `Origin` header must match `Server:AdministrationOrigin` and its expected upstream Host or it receives HTTP 403. Non-browser bearer clients omit `Origin`. Browser clients need the existing session cookie and `X-Codex-CSRF` header for POSTs. There is no cross-origin CORS grant.

With an HTTPS `Server:AdministrationOrigin`, the Server advertises OAuth authorization-server and protected-resource metadata. See [HTTPS deployment](remote-https-deployment.md) for consent, PKCE, refresh and revocation setup. Do not select anonymous authentication or bypass authentication at a proxy.

OAuth scopes are independent and may be requested together, separated by spaces. Omitted scope defaults to `mcp:read`. Consent displays the requested permissions; token exchange and refresh retain exactly the approved scope set (scope changes require a new consent).

| Scope | Tools |
| --- | --- |
| `mcp:read` | `server_health`, `list_workers`, `list_projects`, `list_executions`, `get_execution`, `get_execution_operation`, `get_worker_maintenance`, `get_worker_provisioning_operation`, `wet_inspect` |
| `mcp:write` | `dispatch_execution`, `cancel_execution`, `retry_execution_report`, `create_project_issue`, `update_project_issue`, `set_project_issue_label`, `plan_project_work_package`, `wet_dependencies` |
| `mcp:maintain` | `request_worker_maintenance`, `request_worker_provisioning`, `cancel_worker_provisioning` |

Every tool declares OAuth `securitySchemes` in MCP `_meta`, alongside its existing read-only/destructive annotations. Unmapped or unknown tool calls fail closed for all clients. Each request validates the access credential's server-side hash reference, issuer, exact `/mcp` resource, credential and grant expiry, live grant/revocation state, management authority and supported scopes. The former broad `mcp:admin` grants are no longer accepted; reconnect with explicit scopes.

Missing or invalid credentials return HTTP 401 with a Bearer `resource_metadata` challenge. Insufficient scope returns an MCP tool error with a fixed required scope and `_meta["mcp/www_authenticate"]` challenge containing `insufficient_scope`. Explicit invalid Bearer credentials never fall back to a session cookie. Management tokens and dashboard sessions retain administrator access and their existing protections. Scope grants do not replace project eligibility, capacity, lease, node-local provisioning or WET allowlist checks.

## Client setup

For a Streamable HTTP client with custom header support (including MCP Inspector), set the URL to `https://your-server.example/mcp`, choose Streamable HTTP, and supply the management bearer header via the client's secret settings. Initialize, list tools, then call `server_health` to verify the connection. The token is required on subsequent calls too; there is no MCP session ID to retain. Reconnect with the new token after management-token rotation.

Claude custom connectors with **Request headers** support can use the same HTTPS URL and an `Authorization` header stored in that connector's credential settings. See [Claude remote connector setup](https://support.claude.com/en/articles/11175166-get-started-with-custom-connectors-using-remote-mcp). Claude API integrations can supply their remote MCP server's authorization token as described in [Anthropic's MCP connector documentation](https://platform.claude.com/docs/en/agents-and-tools/mcp-connector).

OpenAI API applications can connect through a remote MCP tool with `server_url` set to the HTTPS `/mcp` URL, `server_label` set to a chosen name, and `authorization` populated at runtime from the management-token secret. Keep the API's normal approval policy. See [OpenAI remote MCP tools](https://developers.openai.com/api/docs/guides/tools-connectors-mcp).

### ChatGPT Plugins OAuth connection

Use this flow for ChatGPT; no Server management token is entered into ChatGPT.
First complete [the HTTPS/OAuth deployment setup](remote-https-deployment.md#chatgpt-mcp-oauth-connections),
including `AdministrationOrigin`, public TLS/proxy routing and the trusted CIMD
origin `https://chatgpt.com`. The administrator's browser needs management-network
access for dashboard login and consent; ChatGPT's backchannel needs public access
to discovery, token, revocation and MCP routes.

In **ChatGPT Plugins**, select **+ → Add custom MCP server**, give the connection
a name and description, and enter the exact public endpoint, for example
`https://codex-server.example.com/mcp` (replace only the deployment hostname).
Choose **OAuth**, using discovered CIMD/public-client authentication (`none`),
review the warning and select **Create as a plugin**. Server supports CIMD, not
DCR or client secrets. Account/workspace policy may restrict this option. These
steps follow the [official OpenAI connection guide](https://developers.openai.com/plugins/deploy/connect-chatgpt)
and [OAuth reference](https://developers.openai.com/plugins/build/auth).

Start with **`mcp:read` only**. Review the requested scope on the Server consent
page before allowing; cancel if broader permissions are requested unexpectedly.
Sign into the Server dashboard as administrator on the management network, then
return to consent and select **Continue authorization** if the cross-site
navigation omitted the Strict session cookie. Check the displayed client ID,
exact redirect URI and scopes, then **Allow connection**. Never approve an
unrecognized client or callback. The redirect must match the fetched CIMD document;
do not substitute a guessed ChatGPT callback. The Server returns `state` and `iss`
with the authorization code; ChatGPT exchanges it using S256 PKCE.

Review discovered tools, start a new conversation, select the plugin with `@`,
and ask: “Call server_health only and report persistence readiness; do not change
anything.” Then optionally request `list_projects`. Tool discovery includes all
tools even for a read-only grant; visibility does not grant permission to call
them. `mcp:write` and `mcp:maintain` are independent additions, not implied by
read access. See the scope table above. Change scopes only through a new,
explicit administrator consent; refresh cannot increase or reduce the approved
scope set. This Server does not accept `openid` or `offline_access`; its refresh
token is issued with the approved MCP scopes.

Mutations advertise `readOnlyHint: false`, their existing `destructiveHint` and
OAuth scope metadata. Keep ChatGPT's write confirmation enabled and inspect the
actual action and target before confirming. These are client confirmation hints,
not a Server dry-run/confirmation handshake. Server scope enforcement and normal
project, execution and node authorization checks still apply. For a disposable
queued execution, `cancel_execution` advertises `destructiveHint: true`; an
approved write-scoped call cancels only queued work, never a running lease.

### Refresh, identify and revoke

After deploying tool/schema/annotation/authentication changes, open the custom MCP
connection in ChatGPT Plugins, select **Refresh**, inspect the changed metadata,
and test again in a new conversation. Refreshing metadata does not expand an
OAuth grant. This is the direct custom-server workflow; published plugin updates
follow OpenAI's review process ([official refresh instructions](https://developers.openai.com/plugins/deploy/connect-chatgpt)).

For Server-side identification, on the management network use
`GET /api/v1/administration/mcp-connections` with existing administrator
authentication. Entries expose `id`, `client`, `resource`, `scope` and absolute
`expires`, never access/refresh tokens. Match the client, exact resource, scopes
and expiry to the connection you created; a client ID may have several grants.
Record the new connection ID at authorization time. If identity is uncertain,
revoke the relevant grants and reconnect rather than guessing which grant is safe.

Revoke the selected ID with
`DELETE /api/v1/administration/mcp-connections/{id}`. Browser administrators need
their current same-origin session and `X-Codex-CSRF`; a trusted management client
can use its existing management authentication. Do not put that credential in
ChatGPT. A client can instead POST form fields `token` and `client_id` to
`/oauth/revoke`, without an Authorization header. This invalidates the whole
connection, including current access and refresh credentials; unknown tokens
also receive 200. Removing a plugin from ChatGPT is not evidence of Server-side
revocation: verify the grant has disappeared from the Server list.

Access credentials expire in ten minutes; refresh rotates both credentials within
the connection's absolute 90-day lifetime. Consumed code/refresh replay revokes
the family. Let the OAuth client manage refresh; do not replay captured requests.
To rotate credentials or change scopes, revoke the old connection on Server,
remove/disconnect it in ChatGPT, then reconnect and consent again. Verify a new
connection ID and expected scopes. This requires **no management-token change**;
rotating that token affects all grants and administrator sessions and is not the
per-connection revocation mechanism.

### Troubleshooting

| Symptom | Check / action |
| --- | --- |
| Discovery fails or returns HTML/Basic-auth challenge | Use the exact HTTPS `/mcp` URL. Public metadata paths must reach Server without proxy Basic auth or dashboard rewrites. `AdministrationOrigin` must be HTTPS; HTTP origins disable OAuth. Inspect the 401 `resource_metadata` challenge and both discovery documents. MCP initialization/tool discovery themselves require authentication. |
| Consent is proxy 403 or asks for login again | Join the management network; sign into the dashboard and use Continue authorization. Preserve existing dashboard/VPN/Basic-auth restrictions. Do not publish consent or administration APIs to fix a backchannel routing problem. |
| Host/origin 403 | Match the canonical `AdministrationOrigin` and fixed upstream Host exactly. Browser Origin must match; forwarded headers cannot choose the issuer. |
| `invalid_client` / redirect rejection | Allow only the actual trusted HTTPS CIMD origin in `OAuthClientMetadataOrigins`. Verify outbound retrieval, exact client ID, exact advertised callback and support for public-client `none`. Metadata redirects are rejected. Do not add wildcard redirects or a DCR endpoint workaround. |
| PKCE / `invalid_grant` | Require S256, the original 43–128 character verifier, unchanged redirect/client/resource and an unused code less than five minutes old. Start new authorization if expired or consumed; never retry a consumed code. |
| Token `invalid_target` / `invalid_request` | Resource must be the canonical origin plus `/mcp`, without a trailing slash. Use form-encoded token requests and public-client `none`, not Basic/Bearer token-endpoint authentication. |
| MCP 401 after connecting | Check expiry, refresh rotation, revoked grant and management authority changes. Use the latest token pair; if refresh fails or replay revoked the connection, re-authorize. Do not retry stale refresh credentials. |
| `invalid_scope` or tool `insufficient_scope` | Use only the three supported scopes in the table. A tool denial includes `_meta["mcp/www_authenticate"]` and the required scope. Re-consent for the minimal needed scopes; do not bypass authorization or make writes anonymous. |
| Tools look stale | Deploy/restart, Refresh the custom connection, inspect scope/annotations and start a new conversation. |

Keep debugging bounded and redacted: record status, fixed error code and
`X-Codex-Request-Id`, not tokens, consent URLs, callback Locations, request bodies
or raw OAuth traces. Do not paste credentials into prompts or support reports.

### Automated coverage and optional live verification

`McpOAuthTests.ServerHostOAuthSessionDiscoversToolsEnforcesScopesRefreshesAndRevokes`
starts the real Server host on an ephemeral loopback port with temporary SQLite
storage, a controlled administrator/client and a stubbed outbound CIMD response.
It drives HTTP discovery, administrator login/consent, code + PKCE exchange,
refresh, MCP initialization/discovery/read, denied read-only-grant mutation,
separately authorized queued cancellation with confirmation metadata, and
revocation. It uses no ChatGPT account, production credentials or external
network. Run focused local coverage with:

```sh
dotnet test tests/CodexWorker.Tests/CodexWorker.Tests.csproj --filter 'FullyQualifiedName~McpOAuthTests|FullyQualifiedName~ServerMcpTests'
```

That test proves the code-level protocol integration. It does not establish
public DNS/TLS/proxy reachability or a live ChatGPT account's UI behavior. Optional
deployed smoke test (record date, deployment version and redacted outcomes):

- Check public discovery/token/MCP routing and management-network-only consent/dashboard access as described in the deployment guide; verify no-store headers and no proxy Basic-auth challenge on backchannel routes.
- Add the exact public `/mcp` connection using OAuth; review the CIMD identity/callback and approve only `mcp:read`. Record its Server connection ID and scopes privately.
- In a new conversation select the plugin and run `server_health`; verify a useful read result. Confirm a read-only grant cannot cancel a disposable queued execution and that the queued state remains unchanged.
- On a disposable project only, reconnect with explicit `mcp:read mcp:write` consent. Confirm the client presents a write confirmation for `cancel_execution`, review its target, approve, and verify the queued execution becomes Cancelled. Leave maintenance scope unapproved unless separately needed.
- Refresh metadata and repeat the read in a new conversation. After access expiry, verify the OAuth client can refresh and read again without another administrator login.
- Revoke the recorded connection ID on Server; verify it disappears and subsequent calls/refresh cannot proceed without new authorization. Disconnect in ChatGPT, reconnect read-only, and verify a new ID and a successful read without changing the management token. Revoke test grants when finished.

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

These tools require `mcp:write` (`mcp:read` for operation inspection), or existing management/session authorization. Standalone Workers are outside these controls.

| Tool | Behavior |
| --- | --- |
| `dispatch_execution` | Accepts `projectId` and positive `issueNumber`; refreshes GitHub eligibility through the administration service and queues through the existing registry. Returns `outcome: accepted`, `executionRequestId`, and state. Duplicate active work, disabled projects and ineligible Issues are rejected. Scheduling still enforces capacity, project limits, dependencies and leases. |
| `cancel_execution` | Accepts `executionRequestId`. Cancels only queued work through the existing cancellation contract. Assigned/running and already terminal work return `outcome: rejected`; the Server currently has no active cancellation protocol. |
| `retry_execution_report` | Accepts `executionRequestId` and a caller-retained 32-character `operationId`. Retries durable completion reporting only, through existing maintenance authority, drain, lease and Worker recovery checks. Returns operation identity and pending/current status. Reusing the same identity and scope returns the retained operation; conflicting scope is rejected. |
| `get_execution_operation` | Read-only status/outcome for an `operationId`, or `found: false`. Use `get_execution` for canonical execution progress and final outcome. |

Controls return bounded reason codes for expected rejection. They never rewrite execution failure classifications, revoke leases, assign Workers directly, or rerun implementation/integration. General retry/resume and active cancellation remain unavailable until supported by the Server lifecycle; reporting retry does not authorize them. Uncertain operations retain their existing at-most-once maintenance semantics and must be inspected rather than blindly replayed with new identities.

## Project Issue planning

Clients with `mcp:write` or existing management/session authorization can use `create_project_issue(projectId, operationId, title, body)`,
`update_project_issue(projectId, issueNumber, title?, body?)`, and
`set_project_issue_label(projectId, issueNumber, label, applied)`. Repository selection always comes
from the registered project. Labels are limited to its configured ready and blocked labels;
creating or editing text never applies readiness. Use the label tool explicitly to mark an Issue ready.
Successful responses include `projectId` and the refreshed normalized `issue`, including number,
URL, title, body, state, labels and eligibility. Issue content is untrusted data.
Returned titles are capped at 256 characters, bodies at 4000, labels at 32 (100 characters each),
and eligibility reasons at 32 (200 characters each), with secret/path redaction.
`contentMayBeTruncated` signals exhausted body/list limits. These response bounds do not alter stored Issue text.

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

Outputs omit private keys, public identities, device login instructions, local paths, raw logs and credential payloads. Authentication setup remains on the existing node-local path. No caller-selected package, version, executable or shell text is accepted. All tools require their mapped OAuth scope or existing administrator authorization.

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

## Selected remote WET capabilities

The remote `wet_` tools use registered `projectId` values, not arbitrary repositories or local paths.
They share Server's GitHub administration services, credentials, snapshots and relationship mutations.
They do not start `wet`, `cw`, `gh`, or a caller-selected process. The separate local `wet mcp`
stdio host retains its own repository-based schema and local authentication; it is not a remote Worker administration API.

| Tool | Arguments and behavior |
| --- | --- |
| `wet_inspect` | `projectId`, positive `issueNumber`, `operation` (`relationships` or `graph`), optional `refresh`. Relationships return parent, children, blocked-by and blocking references, capped at 100 per relation with `truncated`. Graph uses existing Server graph traversal with optional `maxDepth` (0–20), `maxIssues` (1–200), `maxEdges` (1–2000); defaults 5/100/500. Truncation, cycles and repeated nodes are explicit. Titles are redacted and capped at 200 characters; bodies, URLs, labels and provider diagnostics are omitted. |
| `wet_dependencies` | `projectId`, positive `issueNumber`, `blockers` (1–50 distinct positive numbers, excluding the target), `applied`. Uses WET's batch argument contract and Server's existing add/remove blocked-by service. Returns bounded per-link changed/unchanged/failed outcomes; any unsuccessful item makes the result `partial`. Does not change readiness or dispatch work. |

These calls have a 30 second deadline and propagate request cancellation; no detached work continues
after return. Read and write failures return bounded reason codes without raw provider exceptions.
After a partial, timed-out, cancelled or otherwise uncertain write, use `wet_inspect` with `refresh: true`
before retrying. Setting the same desired dependency state skips existing links. No automatic replay
or separate operation store is added. Parent/child changes remain available through
`plan_project_work_package`, preserving the Server relationship service's existing semantics.

## Series 31 integration review

The combined endpoint registers inspection, execution, planning, work-package, selected WET and
managed maintenance tools under the same authenticated transport. The final integration added
the missing remote WET adapter and bounded/redacted planning responses, including responses nested
in work-package steps. Tool discovery regression coverage includes the complete tool set and mutation
annotations. Component coverage exercises project scoping, desired-state dependency updates,
argument bounds, cancellation, graph redaction and planning response limits alongside the existing
execution, creation idempotency and orchestration tests.

Ownership remains with registry scheduling/leases, GitHub administration, execution maintenance,
and typed provisioning services. There is no direct Worker or GitHub transport in MCP handlers,
no new scheduler, credential store or shell surface. Durable execution/reporting/restart/provisioning
operations expose existing identifiers for inspection; synchronous planning and WET calls retain
their documented partial-write/reconciliation behavior. Creation reservations retain stable identities;
uncertain writes are never automatically replayed. Standalone Workers remain outside Server controls.

The OAuth HTTP integration test and optional deployed ChatGPT checklist above cover
connection authorization separately from the tool/service tests. Local self-validation
is not the configured Worker's authoritative validation gate; live account and
node checks must be recorded separately when performed.
