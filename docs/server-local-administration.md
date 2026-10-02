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
codex-server worker show <worker-id>
codex-server worker enable <worker-id>
codex-server worker drain <worker-id>
codex-server worker disable <worker-id>
codex-server worker revoke-token <worker-id>
codex-server worker revoke-delivery-token <worker-id>
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
codex-server credential list [--json]
codex-server credential show <credential-id> [--json]
codex-server credential create <provider> <type> --secret-stdin [--json]
codex-server credential assign <credential-id> <worker-id> [--json]
codex-server credential replace <credential-id> --secret-stdin [--json]
codex-server credential revoke <credential-id> [--json]
codex-server provision list [--json]
codex-server provision show <command-id> [--json]
codex-server provision create <node-id|server> <capability-id> <typed-action> [--timeout-seconds 120] [--allow-elevation] [--repository owner/repository] [--json]
codex-server provision cancel <command-id> [--json]
codex-server provision reconcile <command-id> --node-quiescent [--json]
```

`status` checks the configured local database through the Server health service. It reports control-plane persistence readiness and explicitly reports process health as `not-observed`: a separate offline command cannot establish whether the web process is running. `diagnostics` summarizes registered Worker availability and lifecycle, reported capacity, and project count from the local registry. It does not probe node capabilities, and Worker availability does not establish project eligibility.

`config show` displays effective non-secret settings. Data-directory and database paths are redacted. `config validate` checks Server settings and path resolution without opening or creating the database. Registration, management, and credential-encryption secrets are not included in either output. JSON documents include `contractVersion: 1` for callers that need a versioned output contract.

Worker commands use the configured Server database directly and do not contact the running Server. `worker show` emits the Worker registry projection and the separate credential-delivery authorization status. `enable`, `drain`, and `disable` change only the Server-owned scheduling policy. Enabled Workers may receive assignments; Draining and Disabled Workers receive none. Both policies preserve already assigned or running executions and their leases. The active assignment count makes a drain visible until it reaches zero; the stored policy remains Draining until an operator enables or disables scheduling. Reported heartbeat lifecycle and readiness remain Worker observations.

`provision` inspects and changes the typed provisioning command history in the same local Server database. `create` uses the bounded shared command and capability contracts, requires a registered Worker to be online, and queues work without contacting loopback. Worker-local provisioning policy remains final authority, and Server-local mutation/elevation opt-ins apply to Server commands. `cancel` only cancels queued commands. A running command cannot be remotely cancelled; it retains its node/capability conflict lock until a terminal report or deadline. After the deadline, verify that the node process has stopped, then pass `--node-quiescent` to `reconcile`; this closes the uncertain operation without retrying it or claiming its mutation was undone. The CLI never treats provisioning state as project eligibility or execution readiness.

Legacy provisioning plans remain a separate compatibility surface at `/api/v1/provisioning`. Existing clients and stored plan history continue to work with their established replay behavior. New operator initiated node actions use typed commands. There is no automatic plan-to-command migration: the action catalogs, authorization checks, report formats, and uncertain-operation behavior are not equivalent. Plan creation is deprecated for new operator integrations; removal will require a separately versioned API migration after supported clients have moved.

`worker revoke-token` revokes the generated per-Worker API token and records its revocation time. This blocks new calls authenticated by that token. It does not directly delete active assignment leases; without authorization, the Worker cannot renew or report them, so the existing lease expiry and recovery path applies. `worker revoke-delivery-token` revokes the independent Worker credential-delivery token. A Worker API-token revocation does not revoke delivery authorization, and delivery-token revocation blocks future retrieval without revoking the Worker API token or erasing secrets already delivered. The one-use bootstrap authorization is consumed at registration. The shared `CODEX_SERVER_REGISTRATION_TOKEN` remains a server-wide fallback for Worker API endpoints; a per-Worker token revocation does not revoke that fallback. Operators who need to remove all access through the fallback must rotate or remove the Server configuration value.

`projects` reads and changes central projects through the same registry contracts used by the management API. It opens only the configured local SQLite database and does not require the web Server to be running. Create/update read a JSON `CentralProjectDefinition` with `name`, `repository`, `defaultBranch`, `description`, and optional structured `requirements`. `list`, `show`, and mutations support `--json`. Every definition update, enable/disable transition, and delete requires the current revision; stale revisions return exit code `3` with the current revision. Disabled projects retain queued requests and active executions: the requests remain queued until re-enabled, and assigned/running executions keep their current lease and may finish. Deletion returns exit code `3` and state counts while queued, assigned, or running requests still refer to the project. Missing projects return exit code `4`.

`executions` reads the same persistent queue and lease records as the management API. List results are newest first and bounded to 100 rows; filters accept project ID, execution state, supported GitHub Issue type and positive Issue number. `show` returns one request including lease generation, outcome, recovery status and attempt lineage. `cancel` changes only a Queued request to Cancelled; it cannot interrupt an assignment or a running Worker. Management state transitions that assign, start, complete or fail executions are retired because those transitions belong to the normal Worker assignment/report contracts.

The management API uses `GET /api/v1/executions?projectId=...&state=...&workType=issue&workId=...&limit=50&offset=0` for the bounded list, `GET /api/v1/executions/{id}` for detail, and `POST /api/v1/executions/{id}/cancel` for queued cancellation. Enqueue accepts only `issue` or `github-issue` with a positive decimal Issue number. An optional URL must be an HTTPS `github.com/{owner}/{repository}/issues/{number}` URL for the same central project repository. The Server persists the canonical `github-issue` type and normalized decimal number.

An expired lease during or after integration is retained as `LeaseExpiredUncertain` and is never automatically retried. Before `reconcile`, inspect authoritative repository integration evidence. `NotIntegrated` requires an operator evidence note and queues a new linked attempt using a fresh Worker workspace. `Integrated` requires a full integration commit ID and evidence note; it records the resolution on the failed Server attempt and does not rerun work or impersonate a Worker completion report. The Worker still owns physical workspace and Git recovery. An active request for the same canonical Issue blocks retry creation. The evidence note is redacted and bounded; do not include credentials.

The matching API operation is `POST /api/v1/executions/{id}/reconcile` with `{ "disposition": "NotIntegrated", "evidence": "..." }` or `{ "disposition": "Integrated", "integrationCommit": "<full-commit-id>", "evidence": "..." }`. The legacy `POST /api/v1/executions/{id}/state` returns `410 Gone`.

`credential` uses the same configured local Server database and encrypted credential store as the management API. List and show return `CredentialMetadata` only; they never return the secret. Create and replace require `--secret-stdin` and read the value from standard input, never from command arguments. For example, pipe a value from the deployment's protected secret source to `codex-server credential create <provider> <type> --secret-stdin`. Configure `CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY` out of band as base64 for 32 random bytes before creating or replacing secrets. Assignment targets a registered Worker; assigning the credential again moves it to that Worker. Revoke removes the stored payload, clears the assignment, and prevents future delivery. Delivery authorization remains a separate per-Worker permission, and revoking it does not revoke Worker API authentication.

Server-managed credential revocation stops delivery from the Server; it cannot revoke authorization at the external provider and cannot erase credentials already delivered to a Worker. Revoke the credential at its provider when needed, then provision a newly issued secret to the Server and assign it to the Worker again. Backup restore marks encrypted credentials as needing re-provisioning, so re-enter and assign their secrets after restore. Encryption-key rotation has no administration workflow in this issue and requires separately scoped design work.

Keep four authorization facts separate when diagnosing access: Worker registration/API authentication authorizes Worker-to-Server calls; credential-delivery authorization allows retrieval of an assigned Server secret; node authentication is the Codex or GitHub CLI status observed in that node's service-account environment, which may include configured node-local environment credentials; provider-side authorization controls what the provider will allow that login or token to do. None of these alone grants project scheduling eligibility or repository write access. `worker show` and the dashboard label the first two Server permissions separately; node provisioning reports service-account CLI authentication, and provider scopes/access must be checked with the provider.

For node-local authentication, use `codex-worker provision login codex-cli`, `check-authentication codex-cli`, and `logout codex-cli` for Codex device login/status/logout. The dashboard offers the same typed flow and shows the transient device code only while login is active. For GitHub use `prepare-authentication github-cli`, `check-authentication github-cli`, and `logout github-cli`; preparation creates the managed node-local gh context, then an operator completes browser/device login in a terminal on that node as its service account. GitHub device codes and login output are not sent through the Server. Logout removes product-local cached login state; provider-side authorization must be revoked separately. Neither login flow reports project scheduling or repository write authorization.

All listed CLI operations use local configuration or state only. They do not call `/livez`, `/readyz`, `/health`, or any other loopback endpoint; status and diagnostics JSON include `loopbackContacted: false`. The read-only status and diagnostic queries can run while the Server is active. Restore remains an offline backup operation and requires the Server to be stopped.

## Configuration resolution

The commands use the same configuration builder and precedence as the Server host: the published application's `appsettings.json` and environment-specific settings, environment variables, then command-line configuration options. The application content root is the installed executable directory. `Server:DataDirectory` defaults to the current user's home data directory, and a relative `Server:DatabasePath` is resolved from that data directory. `--Server:<setting>=<value>` options can be supplied to an administration command for a one-off override.

On Linux installations, invoke these commands through the installed helper with `sudo`. The helper runs them as the `codex-server` service account, from the published application directory, with `/etc/codex-server/server.env` loaded by systemd. This selects the same data directory and database as the service without printing values from the environment file. Direct executable invocations use the caller's environment and should only be used when it matches the service configuration or when the intended configuration is supplied explicitly.

## Output and exit codes

Human-readable output is intended for operators. Use `--json` for structured output; diagnostics remain concise and exclude Worker identities and secret payloads. Invalid command syntax returns `2`, unhealthy or invalid local state returns `1`, and Ctrl+C returns `130`. Local status and diagnostics have a 15-second command deadline; a timeout returns `1` with recovery guidance.

Configuration validation prints actionable setting-level errors without echoing supplied values. Check the installed environment file and application configuration for the named setting, then rerun `codex-server config validate`. A status or diagnostics failure directs the operator to check the configured data directory, database, and filesystem permissions.
