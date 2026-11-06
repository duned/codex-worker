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

## Optional standalone reporting

Reporting is **off by default**. A standalone Worker can opt in to read-only observations in a Server dashboard while keeping `projects.ownership: standalone` and its local project YAML, scheduling, capacity and execution ownership. Do not enable `server.enabled`, register a managed Worker, or change ownership to enable this feature. Server does not provision, assign work to, start, stop, drain or otherwise control observer execution.

The shortest supported flow for an existing standalone Worker is:

1. Sign in to the Server dashboard and choose **Add standalone Worker** in the standalone Workers view. Verify that Server has the exact public HTTPS `AdministrationOrigin` configured, with a certificate trusted by the Worker OS.
2. In an interactive terminal on the Worker host, using the Worker service account and its configuration, run:

   ```sh
   sudo codex-worker reporting connect --server https://server.example
   ```

   Packaged Linux `sudo codex-worker` uses the service account. For another installation, run as the account that runs Worker and append `--config /path/to/worker.yml` when needed. Copy the complete **public observer request** printed by the CLI into the dashboard. Choose **Authorize pairing**, then paste the displayed one-time authorization into the CLI's hidden prompt. Never use the management token here or put authorization in command arguments, YAML, scripts or logs.
3. After CLI acknowledgement, refresh the standalone view. **Paired · awaiting first report** confirms redemption only. Confirm **Connected** and the first **status snapshot received** timestamp. A running Worker applies pairing within `reporting.intervalSeconds` (default 30 seconds); no restart is required. If stopped, start it (`sudo systemctl start codex-worker` on packaged Linux). Reporting does not prove authenticated execution readiness.

The one-time authorization expires after **five minutes** and is single-use, bound to the observer identity and exact Server origin. If it expires before redemption, rerun connect on the same host with the same configuration and Server, authorize its public request again, and enter the fresh authorization. Closing/reloading the dashboard loses the displayed authorization. Check Server status; if pairing is still pending and the value is lost, wait for its displayed expiry before requesting another authorization. If redemption may have succeeded but its response was lost, rerun connect against the same Server and press Enter at the hidden prompt: the retained credential checks its registration without replaying authorization. Repeated recovery uses the same identity. Preserve pending local state; disconnect is an explicit abandonment, not an outage repair. A different retained Server is refused until disconnect.

Inspect or disable locally:

```sh
sudo codex-worker reporting status
sudo codex-worker reporting disconnect
```

`status` reads local state only, including the last result and last successful report time; it does not probe Server or the running Worker. `disconnect` (alias `disable`) waits for any in-flight reporting exchange, deletes the local observer credential, and prevents further reports without restarting or stopping execution. Server history remains. In the dashboard, **Revoke reporting** invalidates both pending pairing and accepted reporting credentials; confirm the refreshed revocation state. Revocation retains history and does not stop the standalone host. The local status records `unauthorized` after a rejected report. To opt in again after revocation, disconnect locally and pair a new identity.

Reporting uses outbound HTTPS only, with certificate/hostname checks and no authenticated redirects; no inbound Worker port is needed. The node generates and retains its reporting credential in `reporting.credentialFile` (default `~/.codex-worker/observer.json`), separate from managed enrollment state. The file is atomically replaced, rejects symbolic links and is owner-only on Unix. Protect the service account and this file; do not copy it into task content or source control. Server stores credential/authorization hashes, not the durable credential. The dashboard shows authorization transiently; it cannot retrieve it later.

Snapshots contain allowlisted lifecycle, product version, capacity, active counts and bounded current execution summaries. Execution observations include local execution ID, project/repository, Issue number/title, stage, outcome, validation state, timing and retry/resume lineage. Repository names and Issue titles are visible to Server administrators. Prompts, Issue bodies, raw process output, failure diagnostics, paths, environment contents and provider credentials are excluded. Optional protocol name/readiness fields can be absent; absence is unknown, not ready. This is a bounded latest-summary feed, not a complete execution log: the Worker selects at most 256 local standalone history entries, sends up to 32 events per interval and up to 32 current executions per snapshot, within 64 KiB payloads. Managed execution history is excluded.

A Server outage or reporting rejection never changes local scheduling, execution capacity or outcomes. The reporter tries again on later intervals; pending summaries survive restart and uncertain event acknowledgements are replayed idempotently. The dashboard uses Server snapshot receipt time for freshness: stale means no recent snapshot, not proof the host stopped. Correct DNS/TLS/network or Server availability, then allow reporting to resume; pairing again is unnecessary. Managed heartbeats, leases and stage/final reporting keep their existing separate contracts and outage rules below.

The optional `reporting` block in [worker.example.yml](../config/worker.example.yml) configures only interval and credential location; adding it alone does not enable reporting. Changing those YAML settings requires the usual Worker restart. Connect/disconnect does not.

### Authorized standalone branch maintenance

Observers remain read-only by default. To enable only completed feature branch maintenance, set
`reporting.allowBranchMaintenance: true` locally and restart the standalone Worker. This advertises
up to 100 configured project/repository pairs in snapshots; it does not enable managed scheduling.
A Server administrator must separately `PUT /api/v1/observers/{observerId}/branch-maintenance` with
`{"project":"<configured project name>","repository":"<owner/repository>"}` using management authentication.
Each observer has one grant; another PUT replaces it. DELETE on that route revokes the grant.
Pairing and status reporting alone never grant maintenance authority.

Use the existing `/api/v1/maintenance/executions` contract with `workerId` set to the exact observer ID,
`action: "archived-branches"`, matching `project` and `repository`, `olderThanDays`, and bounded
`limit`/`offset` to inventory/preview. Apply requires explicit `branches` entries containing `branch`
and the preview's `expectedTip`; only the existing `done/feature/` and `complete/feature/` classification
is supported. Locally drain the Worker and wait for zero active executions before requesting apply.
The Server requires a fresh capability snapshot, and the Worker checks its current configuration,
local drain and repository gate before confirming Server authorization. Other maintenance actions
remain unavailable to observers. No local paths or Git credentials are part of this contract.

Inspect the retained operation after cancellation, timeout or lost responses. Undispatched requests
can be cancelled through the existing cancel route. Dispatched effects are never automatically replayed;
the Worker durably retains and resends its report, including per-branch partial results. Interrupted
operations require inspection of current refs before a new preview/apply. An outage never reroutes the
operation to another Worker or repository. Disabling the local opt-in stops polling; retain local
history and reconnect to deliver outstanding receipts before abandoning an observer identity.

## Communication and connectivity loss

Workers initiate outbound HTTP requests to Server: enrollment/registration, heartbeats with capabilities/readiness/capacity, validated versioned configuration snapshots, assignment requests, lease renewals, stage/final execution reports and idle provisioning polling/reporting. Server does not need an inbound listener on the Worker. Both nodes need outbound access to their providers (GitHub, Codex and enabled package sources).

Bootstrap authorization enrolls one identity; subsequent requests use its durable per-Worker credential, with only hashes stored on Server. Management authentication and credential-delivery authorization are separate. Use HTTPS except explicit loopback connections. Rotation/revocation and migration are described in [Worker enrollment](worker-local-administration.md#enrollment-rotation-and-server-migration) and [secret protection](secret-protection.md).

A Worker validates snapshots atomically and retains its last valid cache when updates fail. During temporary Server loss it can retain applied configuration and local evidence, but **the cache never authorizes standalone scheduling or new managed work**. Active work remains subject to its current lease: rejected renewal or inability to confirm ownership before the last known expiry cancels the attempt; integration requires current authority. Expiry before integration may create a distinct retry; unknown or possible integration requires reconciliation. Never infer permission from an old assignment, GitHub label or completed HTTP registration.

Provisioning uses fixed typed actions, deadlines and node-local policy, not remote shell commands. Mutations are disabled by default; Server requests cannot override Worker denial or elevation policy. Uncertain typed operations are inspected and reconciled after node quiescence, not blindly replayed. See [node capabilities](node-capabilities.md).

## Inspect, update and recover

Use `sudo systemctl status codex-server` / `codex-worker` and `sudo journalctl -u codex-server` / `codex-worker` for live service state. `sudo codex-server status` and `sudo codex-worker diagnostics` inspect local state; they do not prove the other process or remote service is healthy. Start/stop/restart through systemd. Configuration changes need a restart.

Run `sudo codex-server update` and `sudo codex-worker update` from a separate operator session. `--check` inspects; `--yes` applies without prompting; `--json` supports automation. Follow [update contracts](self-update.md), back up first and drain active work before changing compatible Server/Worker builds. Ordinary installers preserve configuration and persistent state; inspect service state after interruption before retrying. Uninstall preserves configuration/data by default; `--purge` is destructive and is not a repair procedure.

For Server backup/restore and credential reauthorization, follow [local administration](server-local-administration.md). Backups scrub secrets and authorization material; restore does not restore node credentials or grant execution readiness. Stop Server before restore and restore protected configuration separately.

For Worker maintenance, request local drain and wait for completion before cleanup. Server scheduling drain is distinct from local runtime drain. Inspect history, registered worktrees and authoritative integration evidence; age or Issue closure never proves deletion safe. Preserve uncertain resources and never force-push. Verified standalone integration conflicts can use the bounded Worker-assisted recovery path; ambiguous and managed conflicts remain fenced for reconciliation. Use [execution maintenance](execution-maintenance.md), [record resolution](execution-record-administration.md), [standalone conflict recovery](standalone-conflict-resolution.md), [Codex interruption recovery](codex-interruption-recovery.md), [managed retries](managed-interrupted-retry.md) and [completion reconciliation](managed-completion-reconciliation.md).

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

For optional Claude Code setup alongside Codex, see the [dual-engine operator runbook](dual-engine-operations.md).
