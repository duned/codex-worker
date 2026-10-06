# Worker local administration

Use `codex-worker <command> --help` or nested help such as
`codex-worker config validate --help` and `codex-worker provision login --help`.
Help does not load configuration, create identity files or perform provisioning.
The existing root help alias `h` remains supported.

| Command | Local behavior |
| --- | --- |
| `status`, `diagnostics` | The same versioned configuration, registration metadata and local capability/readiness snapshot, with actionable human diagnostics. These observations reuse typed installation, configuration and authentication checks; they do not contact Server, perform agent execution preflight, or inspect the running service. Lifecycle and active capacity remain unknown. Missing configuration/tools are reported as state, with exit 0 when collection completes. |
| `config [show]` | Inspect redacted Worker configuration. `config --json` is equivalent to `config show --json`. |
| `config validate` | Validate Worker and project configuration without starting execution; invalid configuration exits 2. |
| `config set <setting> <value>` | Atomically update an existing allowlisted setting after complete validation. Protected installed configuration requires appropriate local permissions. Restart the service to apply the change. |
| `capabilities [list]`, `capabilities refresh` | Observe typed local capability inventory or force fresh detection. Missing capabilities are inventory state. |
| `register` | Enroll using `--token-stdin` from a protected secret source. Credential-valued `--token` arguments are rejected. Explicit `--server`, `--identity-file` and `--capacity` override installed configuration defaults. `--operation enroll` is the default; `rotate`, `recover` and `associate` require a Worker- and operation-bound Server authorization. Registration does not establish execution readiness. |
| `provision status` | Inspect capability state, typed authentication dependencies and local action policy. |
| `provision <operation> <capability-id>` | Run an allowlisted typed operation under node-local provisioning policy. Package changes require `--allow-elevation` as well as policy permission. `verify-repository-access` requires `--repository owner/repository`. |
| `credential status`, `credential check/login/logout <provider>` | Observe shared node-local credential state or run typed GitHub/Codex authentication under the same local policy as provisioning. Login publishes a transient browser/device challenge. |

Managed configuration diagnostics separate retrieval from contract validation and local synchronization. Local `status`/`diagnostics` text and JSON expose the validated cached catalog with source `cached`, retrieval `unverified`, and project preparation `unverified`; they do not infer readiness from a checkout or contact Server. The running Worker's `/api/status` (`managedDiagnostics`) and `/api/configuration-sync` expose current observations, also displayed on its dashboard. Server Worker diagnostics retain these heartbeat observations as `workerReportedManagedDiagnostics`; project readiness includes the reported revision and labels stale heartbeats or revision mismatches separately from Server-derived eligibility.

Project observations progress from `not-materialized` to `materializing` and `ready` after local preparation. `blocked` identifies missing project capabilities; `failed` identifies rejected local preparation. These states describe Worker preparation, not Server authorization to execute. Cached configuration never authorizes scheduling during an outage.

| Diagnostic code | Operator action |
| --- | --- |
| `server-unavailable` | Check Server connectivity and service availability. |
| `server-unauthorized` | Check Worker registration and authorized Server credentials; do not regenerate identity as a connectivity repair. |
| `managed-contract-invalid` | Check Server/Worker contract compatibility and the managed project definition. Retrieval succeeded; this is not a connectivity failure. |
| `managed-local-configuration-invalid` | Check local `managedProjects` runtime settings for the reported project/revision. |
| `managed-synchronization-failed` | Check local cache access, project configuration, and active execution ownership. |
| `project-capabilities-missing` | Inspect project requirements and the existing capability diagnostics; provision missing dependencies. |
| `project-preparation-failed` | Check repository access, checkout ownership, node configuration, and retained recovery resources. |

Diagnostic payloads contain bounded identifiers, revisions, states and allowlisted codes. They do not contain repository credentials, exception messages or command output. A successful HTTP response followed by failed validation or synchronization is reported as `retrieved` with the corresponding failure stage.

These commands accept `--json` for automation. Successful observations and
operation results retain their existing versioned contracts. Adapter errors emit
one JSON object with `contractVersion: 1`, `status: "failed"` and a bounded,
redacted `diagnostic` containing `code` and `message`. Provisioning progress is
written separately to stderr. No registration token is returned in output.
Human provisioning and credential failures include the shared safe failure
description (and process exit code when available), matching JSON `failureDetail`.
After `provision prepare-authentication github-cli`, use
`sudo codex-worker provision login github-cli --timeout-seconds 300`
to complete the typed device flow; successful login still does not prove
repository authorization or execution readiness.

On packaged Linux nodes, `sudo codex-worker provision ...` (and `credential`,
`capabilities`, `status`, and `diagnostics`) runs the fixed Worker administration
command in a transient systemd unit as `codex-worker`. It uses the packaged
service HOME, PATH, working directory, private umask and protected
`/etc/codex-worker/worker.env`, including `CODEX_HOME` and
`CODEX_WORKER_CODEX_EXECUTABLE` overrides. Operator environment variables and
credentials are not forwarded. Login, its verification probe, authentication
checks and logout therefore observe and mutate the same node account. The
existing GitHub managed authentication directory remains under that account's
home; unrelated operator credentials are not copied or removed. Package
operations remain separate from authentication and use the existing node-local
policy and fixed elevation helpers. A missing service context fails rather than
falling back to the administrator's authentication. Unpackaged standalone nodes
retain their explicitly owned caller context; Server-triggered operations already
run inside the Worker service.
For packaged local node administration, use sudo even when the interactive
account is `codex-worker`: its shell may not have loaded `worker.env`.

For Codex, run `sudo codex-worker provision login codex-cli`, then
`sudo codex-worker provision check-authentication codex-cli` or
`sudo codex-worker provision status`. `sudo -u codex-worker -H codex login status`
also checks the service account's default home when no CODEX_HOME override is
configured. Root's `sudo codex login status` does not establish Worker readiness.

Configuration defaults to `/etc/codex-worker/worker.yml` on Linux and
`~/.codex-worker/worker.yml` elsewhere. Use `--config <path>` to select another
file. Registration reads the default file when it exists; enrollment before a
configuration file exists still works with an explicit Server URL. An explicitly
selected missing or invalid file fails before enrollment. Relative identity paths
from YAML resolve against that file's directory; explicit identity paths resolve
in the operator's current directory.

Ctrl+C and SIGTERM cancel async local administration. Registration has a
30-second deadline including token input. Provisioning operations accept
`--timeout-seconds 5..600` (default 120), using the existing typed executor's
deadline and reconciliation behavior. Local administration exits 0 on completed
observations/success, 2 on invalid arguments or operational failure, and 130 on
cancellation or provisioning/registration timeout. Retained enrollment credentials
and interrupted provisioning state should be inspected before retrying mutations.
Cancelled provisioning preserves the terminal report after the executor's bounded
reconciliation; it does not start additional capability discovery for presentation.
The optional result capability may therefore be absent. Observe fresh state with
`provision status` before deciding whether to retry.

The local status contract includes the validated stable `registration.workerId`,
`credentialState` (`persisted`, `environment`, `missing`, `invalid`, `unreadable`,
or `not-applicable`), validated `serverUrl`, and `associationSource`. Identity
state distinguishes `local-identity-present`, `local-identity-missing`,
`local-identity-invalid`, and `local-identity-unreadable`. Status never creates or
repairs identity or credential files and never returns their secret contents.
The `runtime` object reports the .NET framework and process/OS architectures;
Worker version, observed capabilities and local readiness remain separate fields.

For Git, Docker, GitHub CLI and Codex CLI, capability `state: "available"`
means the shared local readiness checks passed, rather than just version detection.
Installed tools with unsatisfied dependencies report `blocked`. The version 1 JSON
contract retains its existing fields and adds `installation`, `authentication`,
`configuration`, `configurationDependency`, `authenticationDependencies` and
`blockingReasons` to these capability summaries. Other capabilities can leave
these details null. Human output identifies missing Git identity (`GitIdentity`)
and Docker service-account daemon access (`DockerDaemonAccess`), and separates
missing Codex installation from its subsequent authentication requirement.
Diagnostics give distinct blocker reasons without repeating dependency diagnostics
when installation is the first required step. These checks never configure tools,
change daemon permissions or log in. Docker remains a workload dependency, so its
blocker alone does not change generic Worker execution readiness.

`serverAcceptance` remains `unverified` for a configured Server: local identity,
credentials and a persisted association do not prove successful enrollment or
detect remote revocation. New registration publishes `.server` only after a supported
acknowledgement matching the Worker ID and authoritative credential verification.
Older installations may retain an unverified association; verify it through an ordinary
registration retry before relying on it. Restart preserves identity and material;
HTTP 401/403 does not regenerate them. Managed status reports `not-ready` when
local identity, credentials or association are invalid or unavailable, and
`server-dependent-unverified` when local prerequisites are present. Neither state
authorizes offline managed scheduling. Persisted endpoints receive the same
HTTPS (except loopback), credential/query/fragment rejection as configured URLs
before authenticated requests. These are local contracts; they add no new remote
transport or authority.

Local provisioning uses the same shared `NodeProvisioningCommandExecutor` and
provider catalog as Server. Supported package operations are `install`, `upgrade`
and `uninstall` for `git`, `github-cli`, `codex-cli`, `dotnet-sdk`,
`dotnet-runtime` and `docker` on Debian-based Linux with compatible configured
apt sources. .NET providers require stable .NET 10 components; runtime provisioning
includes ASP.NET Core. Docker separates installation from service-account daemon
access (`check-configuration`), and requires no CLI login. `configure docker
--allow-elevation` reconciles the fixed service-account membership under the
`tool:docker:configure` policy key; install/upgrade includes this step. A reported
service-context refresh requires draining active work and restarting the Worker. Inventory also exposes
typed provided-tool and local configuration dependency metadata. See
[node provisioning policy](node-capabilities.md#net-and-docker-providers).

Capability inventory and provisioning status expose `authenticationDependencies`
as typed metadata: Git has an empty list (no tool-level authentication), GitHub
CLI declares `GitHubCliLogin`, and Codex CLI declares `CodexCliLogin`. The shared
definition supports multiple dependencies. Installation and node authentication
remain separate observations; successful installation does not complete login,
prove repository permissions, or pass the Worker execution preflight. Metadata
contains no credential values or references to secret payloads.

Missing tools leave the control loop online with execution unavailable in both
managed and standalone mode. Local provisioning failures return typed outcomes;
the resulting capability observations determine readiness. An empty standalone project
directory can be used while preparing the node. Configured projects still require
their normal checkout and GitHub safety checks before scheduling, and an absent
standalone checkout remains a startup configuration error.

Managed Workers need no local
project YAML or checkout at startup: the validated Server catalog is retained,
and compatible projects become eligible after read-only repository access and
Worker readiness checks. A missing checkout does not make a managed project
`Unavailable`; it is materialized only after assignment. Missing capabilities,
access failures and preparation failures remain explicit blockers. The control API, heartbeats,
capability reporting and provisioning remain online; `not-ready` describes
execution readiness, not process failure. On Server loss, a valid cached catalog
keeps the Worker observable but cannot authorize new assignments. Synchronization
must succeed again before execution becomes eligible. Invalid Server contracts
and corrupted persisted catalogs still fail startup.

Provision tools and
authenticate as the Worker service account, then restart the service or wait for
the cached observations to refresh. An unchanged failed Codex preflight is not
automatically retried; restart after resolving its cause.

`codex-worker run` starts foreground execution and uses the existing graceful
shutdown lifecycle. For an installed Linux service use
`sudo systemctl restart codex-worker` (or select `start` / `stop`). The existing
`cw restart` / `cw rs` helper restarts and verifies the service. Server-managed
scheduling enable/drain/disable remains a Server operation. The existing
`codex-worker update` command retains its own update/check/JSON conventions and
installer activation/restart flow; see [self-update](self-update.md).

The [19.9 local E2E report](worker-local-e2e-19.9.md) records the isolated
executable campaign, recovery fixes, and remaining disposable-host acceptance
checks. Run `tests/worker-local-e2e.py` against a built Worker apphost to repeat
the campaign without modifying installed services or credentials.

The [19.10 integration review](worker-administration-integration-review-19.10.md)
records cross-provider consistency, integration fixes and the ownership contracts
to preserve when adding secure remote transport.

## Enrollment, rotation and Server migration

Stop managed work before changing credentials or association. Drain active assignments
and stop the Worker service; revocation or rotation can otherwise interrupt lease renewal.
Standalone mode remains independent of enrollment. Shared registration credentials are
not accepted on Worker routes; each Worker uses its own durable credential.

For a new identity, run `sudo codex-server worker-token create` on the destination
Server, then provide its stdout securely to `codex-worker register --server URL
--token-stdin` under the Worker service account. The authorization expires in 15 minutes
and can enroll one previously unknown identity only. It cannot overwrite an existing
Worker, even when that Worker's credential is revoked.

For rotation, discover the stable ID using `codex-server worker list`, then run
`sudo codex-server worker-token authorize WORKER_ID rotate`. Provide that authorization
to `codex-worker register --server URL --operation rotate --token-stdin` using the
existing identity file. Rotation generates fresh cryptographic material; after commit,
the old credential is rejected. An ordinary `register` retry verifies the active
credential without rotation or consuming another bootstrap authorization.

For a missing or revoked credential, preserve/restore the original identity and use
`worker-token authorize WORKER_ID recover` with `register --operation recover`.
Do not delete identity or recovery files to repair authentication. Rotation and recovery
authorizations only apply to identities already visible on that Server.

To change the Server URL (including HTTP-to-HTTPS migration of the same registry),
authorize the same Worker ID on the destination with
`worker-token authorize WORKER_ID associate`, then run `register --server NEW_URL
--operation associate --token-stdin`. This also works when the destination does not yet
know that identity. Fresh material is generated for the destination; the previous
Server's credential is never sent there. The previous active association and material
remain intact until the new endpoint has acknowledged and authenticated enrollment.
After migration to a distinct Server registry, explicitly revoke the old Server
credential there; moving association does not grant authority to administer the
previous Server. Reconfigure separate credential-delivery authorization for that
destination before restarting. When only the endpoint of the same registry changes,
association already replaces its API credential: do not revoke that Worker's token
afterward, which would revoke the newly associated credential. Existing delivery
authorization in the retained registry remains independent.

The identity, `.token`, `.server`, and `.pending` files are owner-only on Unix. The
pending journal retains Worker ID, endpoint, operation and fresh credential across
failures; it never retains the operator authorization. Retrying with the **same endpoint
and operation** authenticates that retained material first, reconciling a lost response
without generating another credential or blindly consuming another authorization.
Malformed, empty, unsupported or mismatched HTTP success responses preserve pending
state and do not activate registration. A verified journal blocks runtime authentication
until interrupted publication of the active files has completed through a retry.
Concurrent local enrollment attempts are rejected by an exclusive file gate. Preserve
all files and inspect the authoritative Server state if pending material no longer
authenticates; do not replace it or start a different pending operation automatically.

The Linux installer supports `--register --operation rotate|recover|associate` with
`--token-file PATH` (or protected bootstrap-token environment input). It delegates
association changes to registration; `--server` alone does not overwrite an enrolled
association. Installer rollback preserves registration state because a Server commit
cannot be undone by restoring only a local URL. Keep the same Server, operation and
identity when retrying an interrupted installation. Start the service only after
registration is verified; registration does not establish execution readiness.

See [secret delivery and operational output](secret-protection.md) for stdin migration, protected enrollment files, and node-compromise response.
