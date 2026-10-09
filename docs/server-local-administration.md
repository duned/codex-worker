# Codex Server local administration

The Server executable provides local status, diagnostics, and configuration commands. These commands reuse the Server configuration and registry services. They do not start a second web host, use a separate registry, or contact the running Server over loopback.

## Commands

```sh
codex-server status
codex-server status --json
codex-server diagnostics
codex-server diagnostics --json
codex-server config show
codex-server config show --json
codex-server config validate
codex-server config validate --json
codex-server config set EnableLocalProvisioning true
codex-server config set AllowLocalProvisioningElevation true --json
codex-server worker list
codex-server worker list --json
codex-server worker list --limit 100 --offset 0
codex-server worker show <worker-id>
codex-server worker enable <worker-id>
codex-server worker drain <worker-id>
codex-server worker disable <worker-id>
codex-server worker revoke-token <worker-id>
codex-server worker revoke-delivery-token <worker-id>
codex-server worker-token create
codex-server worker-token authorize <worker-id> <rotate|recover|associate>
codex-server projects list
codex-server projects show <project-id> --json
codex-server projects create <definition.json>
codex-server projects update <project-id> <expected-revision> <definition.json>
codex-server projects enable <project-id> <expected-revision>
codex-server projects disable <project-id> <expected-revision>
codex-server projects delete <project-id> <expected-revision>
codex-server executions list [--project <id>] [--state <state>] [--work-type issue] [--work-id <number>] [--limit <1..100>] [--offset <0..10000>] [--json]
codex-server executions show <execution-id> [--json]
codex-server executions cancel <execution-id> [--json]
codex-server executions reconcile <execution-id> <NotIntegrated|Integrated> <evidence> [full-commit-id] [--json]
codex-server github access <project-id> [--json]
codex-server github issues <project-id> [--state open|closed|all] [--limit 1..100] [--label <name>] [--refresh] [--json]
codex-server github issue <project-id> <issue-number> [--refresh] [--json]
codex-server github relationships <project-id> <issue-number> [--refresh] [--json]
codex-server github graph <project-id> <root-issue-number> [--max-depth 0..20] [--max-issues 1..200] [--max-edges 1..2000] [--refresh] [--json]
codex-server github create <project-id> --title <title> --body <body> [--preview] [--json]
codex-server github update <project-id> <issue-number> [--title <title>] [--body <body>] [--preview] [--json]
codex-server github label <project-id> <issue-number> <add|remove> <configured-label> [--preview] [--json]
codex-server github dependency <project-id> <issue-number> <add|remove> <blocking-issue-number> [--preview] [--json]
codex-server github dependency-batch <project-id> <issue-number> <add|remove> <issue-number,...> [--preview] [--json]
codex-server github parent <project-id> <child-issue-number> <parent-issue-number|none> [--preview] [--json]
codex-server github sub-issues <project-id> <parent-issue-number> <add|remove> <child-issue-number,...> [--preview] [--json]
codex-server github enqueue <project-id> <issue-number> [--json]
codex-server github refresh <project-id> <issue-number> [--json]
codex-server credential list [--json]
codex-server credential show <credential-id> [--json]
codex-server credential create <provider> <type> --secret-stdin [--json]
codex-server credential assign <credential-id> <worker-id> [--json]
codex-server credential replace <credential-id> --secret-stdin [--json]
codex-server credential revoke <credential-id> [--json]
codex-server provision list [--limit 1..100] [--offset 0..10000] [--json]
codex-server provision show <command-id> [--json]
codex-server provision create <node-id|server> <capability-id> <typed-action> [--timeout-seconds 120] [--allow-elevation] [--repository owner/repository] [--json]
codex-server provision cancel <command-id> [--json]
codex-server provision reconcile <command-id> --node-quiescent [--json]
```

Root `--help` / `-h` is an index. Each command family and leaf supports the same aliases, for example `codex-server backup --help`, `codex-server worker show -h`, and `codex-server github access --help`. Help does not load configuration, open databases, or start the Server. Invalid arguments exit non-zero and point to the selected command's help.

`worker list` discovers registered Worker IDs in ascending ID order, with name, version, platform, availability, lifecycle, scheduling policy, active assignment count, and last-seen time. It reads the existing database without initialization or migration. It uses the same explicit database/service configuration resolution as other Worker commands; an optional positional database path is supported. Defaults are limit 100 and offset 0; bounds are 1..100 and 0..10000. Empty pages are reported clearly. Use `worker show` for details and `diagnostics` for aggregates.

`worker list --json` returns a versioned object with `contractVersion: 1`, `limit`, `offset`, `hasMore`, and `workers`. Each Worker summary contains `workerId`, `displayName`, `workerVersion`, `platform`, `availability`, `lifecycleState`, `schedulingPolicy`, `activeAssignments`, and `lastSeenAtUtc`. No credential payloads, capabilities, or project inventories are included. Use `hasMore` and increment `offset` by `limit` to page through results; pages reflect current registry state.

`status` checks the configured local database through the Server health service. It reports control-plane persistence readiness and explicitly reports process health as `not-observed`: a separate offline command cannot establish whether the web process is running. `diagnostics` summarizes registered Worker availability and lifecycle, reported capacity, and project count from the local registry. It does not probe node capabilities, and Worker availability does not establish project eligibility.

`config show` displays effective non-secret settings. Data-directory and database paths are redacted. `config validate` checks Server settings and path resolution without opening or creating the database. Registration, management, and credential-encryption secrets are not included in either output. JSON documents include `contractVersion: 1` for callers that need a versioned output contract.

`config set <setting> <value>` atomically updates a supported value in the installed `/etc/codex-server/server.env`: `AdministrationOrigin`, `ListenUrl`, `DataDirectory`, `DatabasePath`, `EnableLocalProvisioning`, `AllowLocalProvisioningElevation`, `WorkerStaleAfterSeconds`, `ExecutionLeaseDurationSeconds`, and `ExecutionLeaseRenewalIntervalSeconds`. It validates the resulting Server configuration, preserves file ownership and mode, and reports that the service must be restarted. The installed helper uses a fixed configuration path and invokes only this setting allowlist as root; all values remain ordinary arguments and are validated before writing.

Worker commands use the configured Server database directly and do not contact the running Server. `worker show` emits the Worker registry projection and the separate credential-delivery authorization status. `enable`, `drain`, and `disable` change only the Server-owned scheduling policy. Enabled Workers may receive assignments; Draining and Disabled Workers receive none. Both policies preserve already assigned or running executions and their leases. The active assignment count makes a drain visible until it reaches zero; the stored policy remains Draining until an operator enables or disables scheduling. Reported heartbeat lifecycle and readiness remain Worker observations.

`provision` inspects and changes the typed provisioning command history in the same local Server database. `create` uses the bounded shared command and capability contracts, requires a registered Worker to be online, and queues work without contacting loopback. Worker-local provisioning policy remains final authority, and Server-local mutation/elevation opt-ins apply to Server commands. `cancel` only cancels queued commands. A running command cannot be remotely cancelled; it retains its node/capability conflict lock until a terminal report or deadline. After the deadline, verify that the node process has stopped, then pass `--node-quiescent` to `reconcile`; this closes the uncertain operation without retrying it or claiming its mutation was undone. The CLI never treats provisioning state as project eligibility or execution readiness.

Failed typed commands retain a stable high-level diagnostic plus a bounded failure code and, for ordinary nonzero exits, the process exit code. Failure details use fixed descriptions for sudo denial, missing executables, process failures, verification failures, and timeouts. The local CLI JSON, management API, and dashboard expose those details. Process output and credentials are never persisted or logged. Server journal entries correlate lifecycle and failure records by command ID.

Legacy plan and typed-command history views return the newest 100 records by default. Pass `--limit 1..100` and `--offset 0..10000` to `provision list`; the matching `GET /api/v1/provisioning` and `GET /api/v1/provisioning/commands` endpoints accept the same query parameters. The dashboard's existing history views use the default recent page. This bounds responses while retaining persisted history in SQLite.

Legacy provisioning plans remain a separate compatibility surface at `/api/v1/provisioning`. Existing clients and stored plan history continue to work with their established replay behavior. New operator initiated node actions use typed commands. There is no automatic plan-to-command migration: the action catalogs, authorization checks, report formats, and uncertain-operation behavior are not equivalent. Plan creation is deprecated for new operator integrations; removal will require a separately versioned API migration after supported clients have moved.

`worker revoke-token` revokes the generated per-Worker API token and records its revocation time. This blocks new calls authenticated by that token. It does not directly delete active assignment leases; without authorization, the Worker cannot renew or report them, so the existing lease expiry and recovery path applies. `worker revoke-delivery-token` revokes the independent Worker credential-delivery token. A Worker API-token revocation does not revoke delivery authorization, and delivery-token revocation blocks future retrieval without revoking the Worker API token or erasing secrets already delivered. The one-use bootstrap authorization is consumed at registration. Every normal Worker API request requires the active durable credential for the exact Worker ID in the URL. Bootstrap, management, and delivery credentials cannot substitute for it. The legacy shared `CODEX_SERVER_REGISTRATION_TOKEN` is ignored by both Server and Worker. Revocation stays effective after Server restart.

`projects` reads and changes central projects through the same registry contracts used by the management API. It opens only the configured local SQLite database and does not require the web Server to be running. Create/update read a JSON `CentralProjectDefinition` with `name`, `repository`, `defaultBranch`, `description`, optional structured `requirements`, and optional `issueReadyLabel` / `issueBlockedLabel` policy. When configured, the ready label is required and the blocked label disqualifies an Issue. Open Issues with any open `blocked_by` relationship are always ineligible. `list`, `show`, and mutations support `--json`. Every definition update, enable/disable transition, and delete requires the current revision; stale revisions return exit code `3` with the current revision. Disabled projects retain queued requests and active executions: the requests remain queued until re-enabled, and assigned/running executions keep their current lease and may finish. Deletion returns exit code `3` and state counts while queued, assigned, or running requests still refer to the project. Missing projects return exit code `4`.

Managed project definitions also accept optional `maxParallelTasks`: null or omission means Automatic (no extra project cap); 1–8 caps assigned/running executions globally across Workers. This field persists through restart and revisioned CRUD. Lowering it preserves active executions and blocks new admission; increasing it or selecting Automatic permits additional work only within Worker capacity and eligibility. Standalone local project concurrency remains unchanged. See [upgrade order and installed YAML compatibility](linux-installation.md#managed-project-concurrency-upgrade).

Managed projects may opt in to periodic Server discovery with `automaticDiscovery` in the same create/update JSON. Omission or `enabled: false` disables it. See [the managed project example](../config/managed-project.example.json). The settings are `intervalSeconds` (30–86400, default 300), `pageSize` (1–100, default 25), and `deadlineSeconds` (10–120, default 120). Bounds are validated even when disabled. Definition updates persist these settings and advance the existing project revision; they are not Worker YAML settings.

For an enabled example, replace the `automaticDiscovery` object in that central project definition with:

```json
"automaticDiscovery": {
  "enabled": true,
  "intervalSeconds": 300,
  "pageSize": 25,
  "deadlineSeconds": 120
}
```

Apply the complete definition with `codex-server projects update <project-id> <expected-revision> <definition.json>` or the existing management API. Ensure the Server service account has GitHub read access separately from Worker execution authentication.

The running Server checks due projects every five seconds and reads at most one page per project per interval, under the cycle deadline. It continues to the next page on later cycles and wraps to the beginning after the last page. A fully read page advances continuation even if a later enqueue check fails, so early candidates cannot indefinitely hold up later pages. A failed page authorizes no work and retries only at the next interval; invalid continuations reset to the beginning. Continuation is operational state and resets after Server restart, project replacement, or project revision changes. Cycle work is serialized and canceled/joined on Server shutdown. Disable, delete, repository, or policy changes invalidate stale enqueue checks; the existing assignment eligibility, lease, capacity, and capability gates remain in force.

Automatic discovery uses the existing managed queue and fresh enqueue eligibility checks. Missing, unready, and blocked Issues are skipped and reconsidered on subsequent sweeps. Durable canonical repository/Issue deduplication covers active work, aliases, concurrent explicit enqueues, and Server restarts. Automatic discovery suppresses every Issue with any previous execution request, including completed, failed, and canceled requests; keeping an Issue open/ready never retries it. Explicit enqueue and operator retry/recovery remain available. For an eligible candidate already queued in the project, the cycle uses the existing live eligibility refresh to reconsider requests blocked by an assignment check or an unavailable read. Blocker completion can therefore make the same request assignable on a later sweep, without a manual enqueue or a new attempt. Assignment still checks current eligibility independently. Automatic discovery does not change recovery or execution lineage. Discovery does not claim Issues or mutate GitHub lifecycle labels, and standalone Worker scheduling is unchanged.

Cycle summaries record cycle ID, project/revision, counts, duration, and outcome. Enqueues are logged at Information; unchanged/no-work cycles and candidate skip/deduplication decisions are Debug. Authentication, rate-limit, access, and transport failures are redacted and isolated to the affected project, with no immediate service retry loop. Enable Debug logging to inspect candidate skips and no-work cycles.

`executions` reads the same persistent queue and lease records as the management API. List results are newest first and bounded to 100 rows; filters accept project ID, execution state, supported GitHub Issue type and positive Issue number. `show` returns one request including lease generation, outcome, recovery status, attempt lineage, and the last checked managed Issue eligibility. A queued Issue found blocked remains queued and nonassignable until its eligibility is refreshed explicitly or by an enabled automatic discovery cycle. `cancel` changes only a Queued request to Cancelled; it cannot interrupt an assignment or a running Worker. Management state transitions that assign, start, complete or fail executions are retired because those transitions belong to the normal Worker assignment/report contracts.

`github` reads and administers Issues in the repository selected by the central project, using the Server service account's existing `gh` login and configured environment. `access` checks CLI authentication and the selected repository's read API; it does not ask for or infer Issue write or Git push permissions. `issues` returns at most 100 Issues for an explicit query and includes labels, `blocked_by` Issues, and managed eligibility. Reading or listing Issues never enqueues work. `relationships` returns the Issue's parent, sub-issues, Issues it is `blocked by`, and Issues it is `blocking`. Parent/sub-issue is GitHub's hierarchical work grouping; dependency relationships express ordering and do not create a parent hierarchy. Relationship JSON includes `contractVersion: 1`.

GitHub Issue reads share a persistent repository/Issue identity cache. Active metadata and relationships are fresh for 30 seconds; closed Issues with `codex-done` are cached for 30 days. `--refresh` on `issue`, `issues`, `relationships`, or `graph` bypasses Issue-data freshness but always reuses stable identities, and each Issue snapshot is fetched once per operation. List reads batch up to ten snapshots per GraphQL query; relationship connections are paginated. Mutation and scheduling eligibility checks always fetch fresh data. Mutations invalidate repository Issue data while retaining identities. GitHub administration has a 120-second command deadline; local administration retains its 15-second deadline. Ctrl+C cancels both.

`graph` builds a read-only view from those same relationship reads. The selected Issue is the display root; its parent is shown as context and its sub-issues are traversed as a hierarchy. Blocked-by and blocking Issues are also expanded recursively; Issues outside the selected hierarchy appear under `Related Issues` in text output. A successful GraphQL read with a null parent means there is no parent; visibility and API errors remain failures. Dependency edges are printed once from the blocked Issue perspective as `Blocked by` annotations and never become tree branches. Text nodes show open/closed state and execution labels, hide redundant `codex-done` on closed Issues, and identify open Issues with no execution label. JSON retains up to 20 labels (`labelsTruncated` marks longer lists), prioritizing execution labels when necessary. The default bounds are depth 5, 100 Issues, and 500 edges; each can be lowered or raised within the command's documented range. When a bound omits relationships, text and JSON output set `isTruncated` and list `truncationReasons`. Repeated hierarchy nodes appear once in JSON and as references in the text tree; cycles are marked and not traversed again. An Issue that disappears during traversal remains in the graph with `missing` availability. API, authentication, and malformed-response failures stop the command with the existing redacted diagnostics. Depth bounds apply to the shortest traversal distance through children or dependencies; dependency references at the boundary remain visible without further expansion. JSON uses `contractVersion: 1` and includes nodes, typed edges, bounds, cycle/repetition details, and truncation state. This presentation does not determine managed eligibility or scheduling.

For example, inspect Issue `102`, preview making `100` its parent, or preview dependencies without changing the parent hierarchy:

```sh
codex-server github relationships <project-id> 102 --json
codex-server github graph <project-id> 100 --max-depth 4 --max-issues 100
codex-server github graph <project-id> 100 --json
codex-server github parent <project-id> 102 100 --preview
codex-server github dependency <project-id> 102 add 99 --preview
codex-server github sub-issues <project-id> 100 add 101,102 --preview
```

The text view keeps the relationship kinds visually distinct:

```text
#100 [open] Release plan (labels: ready)
    Parent: #18 [open] Program (no execution label)
├── #101 [open] Implement parser (no execution label)
│   Blocked by: #99 [closed] Define format
│   Blocked by: #102 [open] Add tests (no execution label)
└── #102 [open] Add tests (no execution label)
```

`create` accepts a title up to 256 printable characters and a body up to 65,536 characters. `update` accepts a title, body, or both with the same bounds; an empty body clears it. Both support `--preview`, which validates the request without writing. `label` can only add or remove the project's configured `issueReadyLabel` or `issueBlockedLabel`, and is idempotent when the label already has the requested state. It cannot change Worker execution lifecycle labels. `dependency` adds or removes one GitHub `blocked_by` relationship; `dependency-batch` performs the same operation for up to 50 blocker Issues. `parent` sets, changes, or clears one child's parent using a positive parent Issue number or `none`. `sub-issues` assigns or removes one parent relationship for up to 50 children. Relationship mutations preflight Issue visibility and scope in the central project's repository, reject self-links and duplicate batch entries, and support `--preview`. Batch operations report each item's `changed`, `unchanged`, `preview`, `failed`, or `partial` status; provider failures can leave an accurately reported partial batch, so inspect the result and refresh relationships before retrying failed items. Changing a child's parent removes its current parent relationship before adding the new one; if the second operation fails, the diagnostic calls out that partial change.

API mutations require Server management authentication and always use the central project's repository; no generic GitHub proxy is exposed. Operators supply Issue numbers only. The Server resolves GitHub's internal Issue IDs inside its integration layer. Write failures return bounded guidance for authentication, repository permission, missing Issues, or rejected values without echoing `gh` output or credentials. If a write outcome is uncertain, refresh the target before retrying.

`enqueue` remains an explicit operator action that re-reads the selected Issue, checks project label policy and current dependencies, then creates a canonical `github-issue` request only when eligible. `refresh` re-reads eligibility for an already queued Issue, including one previously blocked. A blocked request remains reserved against duplicate enqueue until it is refreshed, canceled, or completed. Server administration does not claim Issues, update execution labels, post execution comments, or close Issues; those mutations stay in Worker orchestration. Node-local `gh` authentication remains a separate Worker execution concern.

The management API uses `GET /api/v1/executions?projectId=...&state=...&workType=issue&workId=...&limit=50&offset=0` for the bounded list, `GET /api/v1/executions/{id}` for detail, and `POST /api/v1/executions/{id}/cancel` for queued cancellation. GitHub reads use `GET /api/v1/projects/{id}/github/access`, `GET /api/v1/projects/{id}/github/issues?state=open&limit=50&label=...`, `GET /api/v1/projects/{id}/github/issues/{number}`, and `GET .../issues/{number}/relationships`. `POST /api/v1/projects/{id}/github/issues/{number}/enqueue` is the explicit discovery-to-queue action, and `POST .../{number}/eligibility/refresh` rechecks a queued request. The existing `POST /api/v1/executions` path remains an explicit enqueue contract and performs the same live eligibility check. Enqueue accepts only `issue` or `github-issue` with a positive decimal Issue number. An optional URL must be an HTTPS `github.com/{owner}/{repository}/issues/{number}` URL for the same central project repository. The Server persists the canonical `github-issue` type, normalized decimal number, and canonical Issue URL.

Issue writes require the same management token and central project scope: `POST /api/v1/projects/{id}/github/issues` creates, `PATCH .../issues/{number}` updates title/body, `PUT .../issues/{number}/labels/configured` adds or removes only a configured eligibility label, `PUT .../issues/{number}/dependencies/blocked-by` adds or removes one `blocked_by` relationship, `PUT .../issues/{number}/dependencies/blocked-by/batch` applies one dependency action to up to 50 Issue numbers, `PUT .../issues/{number}/parent` sets or clears a child Issue's parent, and `PUT .../issues/{number}/sub-issues` assigns or removes the path Issue as parent for up to 50 child Issues. Batch responses and relationship reads use versioned JSON contracts. Requests with `previewOnly: true` return validated proposed state without writing; clients should show that preview before sending the corresponding mutation with `previewOnly: false`.

For parent administration, the request uses `parentIssueNumber` as a repository Issue number; send `null` to clear it. Sub-issue batches use `childIssueNumbers`, `applied`, and `previewOnly`. Dependency batches use `blockerIssueNumbers`, `applied`, and `previewOnly`. These contracts never require GitHub's internal numeric Issue IDs.

Before returning a Worker assignment, the Server re-reads the selected Issue's state, configured labels, and all paginated `blocked_by` dependencies. Ineligible work is returned to the Queued state, its unused lease is released, and it remains visible with a managed eligibility reason. GitHub read failure also releases the unreturned reservation and fails the assignment request closed. A Worker `gh` login, Server `gh` login, Issue read permission, Issue write permission, and Git push authorization are independent facts. Issue reads and eligibility do not change Worker-owned checkout, execution, Issue write, or Git integration behavior.

An expired lease during or after integration is retained as `LeaseExpiredUncertain` and is never automatically retried. Before `reconcile`, inspect authoritative repository integration evidence. `NotIntegrated` requires an operator evidence note and queues a new linked attempt using a fresh Worker workspace. `Integrated` requires a full integration commit ID and evidence note; it records the resolution on the failed Server attempt and does not rerun work or impersonate a Worker completion report. The Worker still owns physical workspace and Git recovery. An active request for the same canonical Issue blocks retry creation. The evidence note is redacted and bounded; do not include credentials.

The matching API operation is `POST /api/v1/executions/{id}/reconcile` with `{ "disposition": "NotIntegrated", "evidence": "..." }` or `{ "disposition": "Integrated", "integrationCommit": "<full-commit-id>", "evidence": "..." }`. The legacy `POST /api/v1/executions/{id}/state` returns `410 Gone`.

`credential` uses the same configured local Server database and encrypted credential store as the management API. List and show return `CredentialMetadata` only; they never return the secret. Create and replace require `--secret-stdin` and read the value from standard input, never from command arguments. For example, pipe a value from the deployment's protected secret source to `codex-server credential create <provider> <type> --secret-stdin`. Configure `CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY` out of band as base64 for 32 random bytes before creating or replacing secrets. Assignment targets a registered Worker; assigning the credential again moves it to that Worker. Revoke removes the stored payload, clears the assignment, and prevents future delivery. Delivery authorization remains a separate per-Worker permission, and revoking it does not revoke Worker API authentication.

Server-managed credential revocation stops delivery from the Server; it cannot revoke authorization at the external provider and cannot erase credentials already delivered to a Worker. Revoke the credential at its provider when needed, then provision a newly issued secret to the Server and assign it to the Worker again. Backup restore marks encrypted credentials as needing re-provisioning, so re-enter and assign their secrets after restore. Encryption-key rotation has no administration workflow in this issue and requires separately scoped design work.

Keep four authorization facts separate when diagnosing access: Worker registration/API authentication authorizes Worker-to-Server calls; credential-delivery authorization allows retrieval of an assigned Server secret; node authentication is the Codex or GitHub CLI status observed in that node's service-account environment, which may include configured node-local environment credentials; provider-side authorization controls what the provider will allow that login or token to do. None of these alone grants project scheduling eligibility or repository write access. `worker show` and the dashboard label the first two Server permissions separately; node provisioning reports service-account CLI authentication, and provider scopes/access must be checked with the provider.

For node-local authentication, use `codex-worker provision login codex-cli`, `check-authentication codex-cli`, and `logout codex-cli` for Codex device login/status/logout. The dashboard offers the same typed flow and shows the transient device code only while login is active. For managed headless Server GitHub authentication, enable local provisioning and run `codex-server provision create server github-cli PrepareAuthentication`, then `codex-server provision create server github-cli Login --timeout-seconds 300`. While Login is running, run `codex-server provision show <command-id>` (also available with `--json`), open the displayed GitHub device URL on any browser, and enter the one-time code before the displayed deadline. Repeat show if the challenge is not available yet. No browser or manual service-account login is required on the Server. Login verifies authentication before reporting success; then `codex-server provision create server github-cli CheckAuthentication` checks the same managed context used by Server GitHub administration. The validated URL/code are stored with the active command so separate local CLI invocations can read them; they are hidden after the deadline, cleared on terminal completion/reconciliation, and excluded from backups. Tokens and raw login output never enter command diagnostics. Timeout or Server shutdown terminates the login process. A timeout requires a new Login action; it does not replay the old command. Worker node authentication also supports `prepare-authentication github-cli`, `login github-cli`, `check-authentication github-cli`, and `logout github-cli`. Logout removes product-local cached login state; provider-side authorization must be revoked separately. Neither login flow reports project scheduling or repository write authorization.

All listed CLI operations use local configuration or state only. They do not call `/livez`, `/readyz`, `/health`, or any other loopback endpoint; status and diagnostics JSON include `loopbackContacted: false`. The read-only status and diagnostic queries can run while the Server is active. Restore remains an offline backup operation: the Server process holds an exclusive `<database-path>.access-lock` for its lifetime, and restore must acquire that lock before extracting or replacing state. A conflict fails without changing the target database. The empty lock file remains in the configured database directory so competing processes coordinate on a stable path.

Backup archives retain Worker and project definitions/revisions, execution metadata/history/queue/leases, provisioning history, credential metadata and revoked per-Worker API-token metadata. They scrub credential payloads, assignments and delivery authorization rows, zero and revoke Worker API-token hashes, and remove bootstrap tokens. They exclude Server appsettings/environment/service-manager configuration, management and registration tokens, node-local Codex/GitHub authentication and SSH private keys, Worker identity, checkouts/workspaces, and external secret-provider contents. Restore configuration from the protected deployment source, create fresh Worker API and delivery tokens, re-enter or retrieve credential values and assign them, then restore any lost node-local authentication/SSH setup. Verify `status` and `diagnostics` after restart. Restore does not establish clean remote deployment readiness; it only validates local archive and database recovery contracts.

## Migrating from shared Worker authentication

Deployments using `CODEX_SERVER_REGISTRATION_TOKEN` must explicitly enroll each Worker before managed scheduling can resume:

1. Stop the Worker service and retain its existing identity file and recovery resources. Inspect `sudo codex-server worker list`. If the ID is already visible, create an explicit short-lived authorization with `sudo codex-server worker-token authorize WORKER_ID recover`; a general bootstrap cannot replace an existing identity. For a previously unknown/new node, use `sudo codex-server worker-token create`. Supply the authorization through a protected secret source rather than command arguments or shell history.
2. Run `codex-worker register --server https://server.example --operation recover --token-stdin --identity-file /path/to/existing/worker-id` as the Worker service account for a known ID; use the default `enroll` operation for an unknown/new node. Retain the intended identity path. Registration stages fresh owner-only material in `.pending`, then publishes `.token` and `.server` after acknowledgement and authentication; do not copy a shared token into these files.
3. Verify the same Worker ID with `sudo codex-server worker show <worker-id>`: API credential status must be `active`. Run `codex-worker status --config /path/to/worker.yml` as the service account and verify persisted identity and credential state. Start the service and verify successful registration updates, heartbeats, and configuration synchronization; local file presence alone does not prove Server acceptance.
4. Remove `CODEX_SERVER_REGISTRATION_TOKEN` from both Server and Worker service environments and restart the affected services. It grants no API authority after this change.

Existing enrolled Workers with active durable per-Worker credentials need no re-enrollment. Missing, invalid, unreadable, or revoked credentials stop managed requests; the Worker does not automatically enroll, replace its identity, restore revoked authorization, or schedule from a cached snapshot during an outage. Restore known valid protected local state when appropriate and involve the Server operator for revoked credentials. Generic bootstrap cannot bypass revocation. Explicit operator-authorized recovery can replace revoked authentication. Follow the [enrollment, rotation and Server migration procedure](worker-local-administration.md#enrollment-rotation-and-server-migration), retaining any pending journal through retries. Standalone mode remains available only when explicitly configured.

## Configuration resolution

The commands use the same configuration builder and precedence as the Server host: the published application's `appsettings.json` and environment-specific settings, environment variables, then command-line configuration options. The application content root is the installed executable directory. `Server:DataDirectory` defaults to the current user's home data directory, and a relative `Server:DatabasePath` is resolved from that data directory. `--Server:<setting>=<value>` options can be supplied to an administration command for a one-off override.

On Linux installations, invoke these commands through the installed helper with `sudo`. The helper runs them as the `codex-server` service account, from the published application directory, with `/etc/codex-server/server.env` loaded by systemd. This selects the same data directory and database as the service without printing values from the environment file. Direct executable invocations use the caller's environment and should only be used when it matches the service configuration or when the intended configuration is supplied explicitly.

## Output and exit codes

Human-readable output is intended for operators. Use `--json` for structured output; diagnostics remain concise and exclude Worker identities and secret payloads. Invalid command syntax returns `2`, unhealthy or invalid local state returns `1`, and Ctrl+C returns `130`. Local status and diagnostics have a 15-second command deadline; a timeout returns `1` with recovery guidance.

Configuration validation prints actionable setting-level errors without echoing supplied values. Check the installed environment file and application configuration for the named setting, then rerun `codex-server config validate`. A status or diagnostics failure directs the operator to check the configured data directory, database, and filesystem permissions.

### Read-only managed Issue discovery

`GET /api/v1/projects/{id}/github/discovery?limit=50&after=...` requires management authentication and uses the Server service account's existing GitHub integration. It reads one repository Issue connection page, restricted to open Issues and the selected project's configured ready label (no label filter when unset). Pull requests are excluded by the native Issue connection. It returns `projectId`, canonical lowercase `repository`, `candidates`, `nextCursor`, and `isComplete`. Each candidate contains a canonical `github-issue` work reference, an `eligible` or `ineligible` classification, and bounded eligibility reasons. State and labels are checked again from Issue snapshots, so an Issue closed or relabeled since enumeration is classified as ineligible. Native `blocked_by` prerequisites use the existing managed completion policy; parent/sub-issue hierarchy does not establish execution ordering.

The default page limit is 50, with a supported range of 1–100. Transport enumeration orders by creation time ascending; candidates within a page are deduplicated and ordered by Issue number. A full page does **not** imply complete discovery: `isComplete` is true only when GitHub reports no next page. Callers must URL-encode and pass `nextCursor` as `after` on the next cycle, even when the current page contains no eligible candidates, to reach later work. Cursors are opaque printable values bounded to 1024 characters; keep them scoped to the project/repository and its ready-label policy. Reset to the first page after reaching the end, or when that policy changes. This is a traversal of mutable provider data, not an atomic repository snapshot; callers should tolerate duplicates across cycles.

Each discovery call performs one fresh page enumeration and reads at most 100 Issue snapshots through the existing repository cache and batches of up to ten. Existing native relationship pagination bounds (25 pages / 2500 references per connection) and process deadlines still apply, with a 120-second overall discovery deadline. Ordinary transport snapshots retain the existing 30-second freshness window; completed historical snapshots retain their existing longer window. Eligibility is recomputed for the selected project's policy, including aliases of the same repository, and is never persisted as authorization. Enqueue and assignment retain their independent authoritative eligibility checks.

Read errors fail the discovery response rather than classifying work as ineligible or returning a misleading complete/partial page. The API returns HTTP 503 with a bounded diagnostic and code: `read-unavailable`, `read-failed`, `authentication-failed`, `rate-limited`, `invalid-response`, or `query-timeout`. Request cancellation propagates as cancellation. Issues that disappear during snapshot retrieval are returned as ineligible candidates with a missing/unreadable reason; transport failures still fail the page. Invalid limits/cursors return HTTP 400; unknown projects return HTTP 404. The read-only discovery endpoint creates no executions, assignments, leases or GitHub mutations. Periodic enqueue is separately opt-in through the central project settings described above. The existing bounded Issue-list endpoint remains an inspection contract without discovery completeness semantics.

## Control-plane operational logs

The Server uses the existing ASP.NET Core `Logging` configuration and providers. `CodexServer.SqliteRegistryStore` and `CodexServer.ServerGitHubAdministrationService` emit structured event `2201` (`ServerOperationalDecision`). `CodexServer.AutomaticIssueDiscoveryService` emits event `2203` (`AutomaticDiscoveryCycle`) with cycle correlation, project revision, candidate/enqueue/skip/duplicate counts, duration and outcome. No additional event database or telemetry service is required.

Each event has `Operation` and a bounded `ReasonCode`, with `ProjectId`, canonical GitHub Issue URL (or canonical type/number when a stored URL is absent) in `WorkReference`, Server `ExecutionId`, `WorkerId`, `AssignmentId`, `LeaseGeneration`, and `WorkerExecutionId` when available. A rejected request may have only input identifiers (or a type/number work reference); invalid or credential-like identifiers are redacted. Request trace IDs remain available through the existing HTTP request diagnostics and `X-Codex-Request-Id` response header.

Information events cover project creation/update/enable/disable/delete, accepted enqueue, duplicate active work rejection, assignment/lease acquisition, eligibility changes and rejected assignments, stage changes, queued cancellation, and accepted/rejected Worker outcomes. Expected decisions such as `issue-ineligible`, `project-disabled`, `duplicate-active-work`, and `ownership-or-stage-rejected` are not infrastructure errors. `outcome-failed` describes a Worker task outcome, not a Server failure. Warning events `expired-requeued` and `expired-uncertain` distinguish lease recovery decisions and are emitted only after reconciliation commits. Error events identify infrastructure or GitHub read failures with fixed codes; they omit exception objects and messages.

Idle assignment decisions (`no-eligible-work`, `worker-not-ready`, `scheduling-paused`, `capacity-reserved`, `worker-unregistered`, `worker-disabled-or-no-capacity`), repeated stage reports, duplicate final acknowledgements, rejected renewals, and expected request cancellation use Debug. Successful heartbeats, successful renewals and no-change expiry polls emit no operational events. Set `Logging__LogLevel__CodexServer.SqliteRegistryStore=Debug` or `Logging__LogLevel__CodexServer.ServerGitHubAdministrationService=Debug` temporarily to inspect these decisions. The default Information level avoids routine poll/renewal noise.

For a systemd installation, inspect the existing Server unit with `journalctl -u codex-server --since '1 hour ago'` (use the installed unit name if customized). Filter by execution ID, project ID or Issue URL to follow an attempt; filter for `lease-expiry`, `expired-uncertain`, `result-report` or `duplicate-active-work` to investigate a decision. JSON console logging can be selected with the standard `Logging__Console__FormatterName=json` setting for structured filtering.

Use `codex-server executions show <execution-id> --json` or the dashboard execution detail to inspect persisted eligibility reasons, pending reason, lease ownership/generation, result, recovery disposition and retry lineage. Use `codex-server worker show <worker-id> --json` for eligibility/capacity diagnostics (see the command syntax above). Logs explain transitions; the existing registry projections remain the source of current state. Issue bodies, Worker completion summaries, validation/integration text, process output, environment contents and authentication material are excluded from operational events.

See [secret delivery and operational output](secret-protection.md) for trusted TLS termination, API proxy cache/log exclusions, remote sentinel checks, and the complete node-compromise response.

For HTTPS dashboard setup or upgrade, use
`sudo codex-server config set AdministrationOrigin https://YOUR-SERVER-HOST`
(no trailing slash), then `sudo codex-server config validate` and
`sudo systemctl restart codex-server`. `config show` includes the safe origin and
browser-login guidance; an unset origin remains valid for bearer-only clients.
The TLS proxy's fixed upstream Host must match the origin's authority while the
listener remains loopback HTTP. Existing settings, token and state are preserved.
See [HTTPS login recovery and safe diagnostics](remote-https-deployment.md#upgrade-recovery-and-bounded-rejection-diagnostics)
for network/configuration rejection, existing-token retrieval and deployed checks.
