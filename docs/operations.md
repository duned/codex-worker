# Operator guide

## Choose an ownership mode

| Mode | Configuration and work discovery | Execution ownership |
| --- | --- | --- |
| Standalone Worker | Local Worker YAML and one project YAML per repository. Worker polls GitHub for ready Issues and checks native blocked-by dependencies. No Server required. | Worker controls local capacity, isolated worktrees, validation, Git integration and Issue reporting. |
| Server-managed Worker | Server stores revisioned project definitions, enabled state, requirements, project concurrency and explicit or opt-in automatic Issue enqueue. Worker registers and requests assignments. | Server owns reservations, assignments and generation-fenced leases. Worker executes and retains local history and recovery resources. |
| Server local administration | Installed `sudo codex-server` commands administer local SQLite state and configuration without contacting the HTTP API. Server can run without enrolled Workers for administration. | This is a local control-plane path, not a standalone execution agent. Restore requires the Server stopped. |

Standalone is useful for one independently owned execution host. Managed mode supports central administration and a fleet. Choose explicitly: `projects.ownership` defaults to `standalone`; managed requires `server.enabled: true`. Server registration alone does not change project ownership.

## Configure

Use the checked-in examples as the configuration source; example values are choices, not necessarily runtime defaults:

- [Standalone Worker](../config/worker.example.yml) and [project](../config/project.example.yml): project directory, repository/base branch, labels, Codex model/effort, sequential validation commands, dotenv path, concurrency and recovery policy.
- [Packaged managed Worker](../packaging/linux/worker.managed.example.yml): Server connection, identity, checkout root and Worker-local runtime defaults.
- [Central project](../config/managed-project.example.json): Server identity, repository, requirements, discovery and fleet-wide project cap. No local checkout paths or credentials.
- [Server configuration contract](../src/CodexServer/ServerConfiguration.cs) and [installer](../packaging/linux/install-server.sh): Server uses application configuration, environment variables and command-line overrides, rather than Worker YAML. Installed settings live in `/etc/codex-server/server.env`; use `Server__Setting` environment keys.

Server defaults to loopback `http://127.0.0.1:5090`, 90-second Worker staleness, a 900-second execution lease and 60-second renewal. Local provisioning and elevation default off. Installed state is under `/var/lib/codex-server`; direct uninstalled execution defaults to the caller's home data directory. Use the installed helper to select the service database consistently.

Worker API defaults to loopback `http://127.0.0.1:5080`; it has no authentication and must remain behind the local boundary (use an SSH tunnel for operator access). Worker polling defaults to 60 seconds, heartbeat to 20 seconds and global capacity to one (supported range 1–8). Managed project concurrency is central; null means Automatic. Worker slots can be centrally set, applied when idle and confirmed by heartbeat; assignment limit is a separate Server admission control. [Upgrade compatibility](linux-installation.md#managed-project-concurrency-upgrade) covers snapshot contracts and obsolete local cap settings.

Worker-local settings retain checkout/instruction paths, labels, dotenv files, Codex settings, validation commands, recovery policy and provisioning authorization. YAML rejects duplicate/unknown keys and invalid values. Run `sudo codex-worker config validate` and `sudo codex-server config validate` before restarting after configuration changes. Use each command's `--help` for supported settings and options.

## Install and start

Supported packaged hosts are Ubuntu 24.04 x86_64 with systemd, sudo, curl, tar and sha256sum; Server also requires OpenSSL. Archives include .NET. Select a published release and use its matching installer tag and `--version` on both hosts. See [Linux installation](linux-installation.md) for exact commands, automation and local-build alternatives.

For managed deployment:

1. Install Server; the installer creates its service account, protected environment file, management token and encryption key, and starts the service.
2. For separate hosts, configure [HTTPS and firewall restrictions](remote-https-deployment.md) before enrollment. Set the exact `AdministrationOrigin` for browser administration. Open the dashboard and enter the management token through a protected operator session; the installation guide explains retrieval.
3. Create a one-use, 15-minute authorization with `sudo codex-server worker-token create`.
4. Install Worker with `--server https://server.example --capacity 1 --register --start`. The interactive prompt hides the token; unattended installs use a protected token file, never a token argument. Existing identities require operation-bound rotation/recovery/association authorization, not generic enrollment.
5. Verify the Worker on Server and inspect readiness. Installation and registration do not establish authenticated execution readiness.

For standalone deployment, install Worker without registration or start, replace the managed starter with the standalone example, and create project YAML under its configured directory. Prepare each dedicated canonical checkout clean on its base branch with repository origin, Git identity and required instruction file. Configure service-account tool/authentication access and project validation, validate configuration, then `sudo systemctl enable --now codex-worker`. Do not share canonical checkouts between Workers.

The services run as separate `codex-server` and `codex-worker` accounts. Worker HOME is `/var/lib/codex-worker`; configuration is `/etc/codex-worker/worker.yml` and protected `worker.env`. Git, GitHub CLI, Codex CLI, repository read/write and Issue permissions, authenticated Codex preflight and project runtimes are execution prerequisites. Authenticate in the service context using [Worker administration](worker-local-administration.md), not root's or an interactive user's login. Server GitHub read access for discovery is separate from Worker Git/GitHub execution access. Server-managed credentials are optional and require protected storage and per-Worker assignment/delivery authorization; private node keys and CLI logins stay on the node. Never place secrets in project YAML, task prompts or logs.

## Communication and connectivity loss

Workers initiate outbound HTTP requests to Server: enrollment/registration, heartbeats with capabilities/readiness/capacity, validated versioned configuration snapshots, assignment requests, lease renewals, stage/final execution reports and idle provisioning polling/reporting. Server does not need an inbound listener on the Worker. Both nodes need outbound access to their providers (GitHub, Codex and enabled package sources).

Bootstrap authorization enrolls one identity; subsequent requests use its durable per-Worker credential, with only hashes stored on Server. Management authentication and credential-delivery authorization are separate. Use HTTPS except explicit loopback connections. Rotation/revocation and migration are described in [Worker enrollment](worker-local-administration.md#enrollment-rotation-and-server-migration) and [secret protection](secret-protection.md).

A Worker validates snapshots atomically and retains its last valid cache when updates fail. During temporary Server loss it can retain applied configuration and local evidence, but **the cache never authorizes standalone scheduling or new managed work**. Active work remains subject to its current lease: rejected renewal or inability to confirm ownership before the last known expiry cancels the attempt; integration requires current authority. Expiry before integration may create a distinct retry; unknown or possible integration requires reconciliation. Never infer permission from an old assignment, GitHub label or completed HTTP registration.

Provisioning uses fixed typed actions, deadlines and node-local policy, not remote shell commands. Mutations are disabled by default; Server requests cannot override Worker denial or elevation policy. Uncertain typed operations are inspected and reconciled after node quiescence, not blindly replayed. See [node capabilities](node-capabilities.md).

## Inspect, update and recover

Use `sudo systemctl status codex-server` / `codex-worker` and `sudo journalctl -u codex-server` / `codex-worker` for live service state. `sudo codex-server status` and `sudo codex-worker diagnostics` inspect local state; they do not prove the other process or remote service is healthy. Start/stop/restart through systemd. Configuration changes need a restart.

Run `sudo codex-server update` and `sudo codex-worker update` from a separate operator session. `--check` inspects; `--yes` applies without prompting; `--json` supports automation. Follow [update contracts](self-update.md), back up first and drain active work before changing compatible Server/Worker builds. Ordinary installers preserve configuration and persistent state; inspect service state after interruption before retrying. Uninstall preserves configuration/data by default; `--purge` is destructive and is not a repair procedure.

For Server backup/restore and credential reauthorization, follow [local administration](server-local-administration.md). Backups scrub secrets and authorization material; restore does not restore node credentials or grant execution readiness. Stop Server before restore and restore protected configuration separately.

For Worker maintenance, request local drain and wait for completion before cleanup. Server scheduling drain is distinct from local runtime drain. Inspect history, registered worktrees and authoritative integration evidence; age or Issue closure never proves deletion safe. Preserve uncertain resources and never force-push or automatically resolve conflicts. Use [execution maintenance](execution-maintenance.md), [record resolution](execution-record-administration.md), [Codex interruption recovery](codex-interruption-recovery.md), [managed retries](managed-interrupted-retry.md) and [completion reconciliation](managed-completion-reconciliation.md).

## Troubleshoot

| Symptom | First checks |
| --- | --- |
| Installation/download failure | Supported OS/architecture, published component archive/checksum and outbound HTTPS; preserve retained identity on retry. |
| Service will not start | Journal, configuration validation, file ownership and service executable/environment. Standalone deterministic startup failures exit 2 and are not restarted by systemd. |
| Online but not ready | Missing tools, service PATH/CODEX_HOME, service-account authentication and scoped repository permissions; registration is not preflight. Managed control/heartbeat can remain online while execution is unavailable. |
| `server-unavailable` | Server systemd state, DNS, TLS trust, firewall/proxy and reachable configured URL. |
| 401/403 or `server-unauthorized` | Existing identity, Server association and revocation state. Do not delete identity or enroll anew as a connectivity fix. |
| `managed-contract-invalid` | Installed build compatibility and snapshot contract/hash; retrieval succeeded. Do not bypass validation or remove cache/recovery state. |
| Queued work stays pending | Project/Worker enablement, readiness, requirements, slots/capacity, Issue labels/blockers and authoritative lease/reconciliation state. |
| Uncertain completion/update | Inspect service/history and remote Git evidence before retrying; do not rerun integrated implementation. |

Managed assignment admission checks the Worker's advertised project capacities, current
readiness and scheduling policy, unresolved update fences, slot synchronization,
unacknowledged assignments, project requirements, Issue reservations and fleet-wide
project concurrency before creating a generation-fenced lease. GitHub eligibility is
rechecked before returning the assignment. A running Worker with spare heartbeat
capacity can still be waiting on one of these checks. Queued execution pending reasons
distinguish compatibility, readiness/policy, project capacity and capacity available
while awaiting assignment. Recent project-capacity advertisements are explanatory
observations only, expire with heartbeat freshness and are lost on Server restart.
The structured Server `assignment` decision reports a bounded reason code when a
Worker's no-assignment reason changes; unchanged polling does not repeat the event.
Idle `no-eligible-work` is Debug; admission blockers are Information.

A reproduced reservation failure involved a retained `LeaseExpiredUncertain`
execution followed by a project repository edit and an eligible same-numbered Issue
in the new repository. Reservation scanning previously used only project/type/number,
while candidate admission checked the repository URL. The old reservation prevented
the new Issue from being assigned despite spare capacity. Reservation scanning now
uses the same repository validation as admission; the uncertain execution and its
reconciliation requirement remain intact. The regression
`UncertainIssueFromPreviousRepositoryDoesNotReserveCurrentRepositoryIssue` covers this
sequence and verifies the new lease. This reproduces queued work with spare capacity;
it does not establish that the historical incident involved a repository edit.

Use bounded request/execution IDs to correlate journals. Keep tokens, environment contents and raw process output out of incident reports. Detailed safe diagnostics are in [Worker administration](worker-local-administration.md), [Server administration](server-local-administration.md) and [HTTPS recovery](remote-https-deployment.md#upgrade-recovery-and-bounded-rejection-diagnostics). Optional features: [Telegram](worker-telegram-settings.md), [guided enrollment](worker-onboarding.md), [dashboard Worker updates](worker-dashboard-updates.md), and [standalone planning CLI](work-execution-toolbox.md).
