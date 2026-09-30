# Linux installation reference

Start with the [A–B–C clean-machine installation](../README.md#install-server--worker). This reference covers automation, manual enrollment, configuration, updates, removal, and troubleshooting.

## Server installation and operations

On Ubuntu 24.04 x64, install the self-contained release without cloning the repository or installing .NET:

```sh
curl -fsSL https://raw.githubusercontent.com/duned/codex-worker/main/packaging/linux/install-server.sh | sudo bash
```

For a specific published release, pin both the script tag and `--version` to the same version (replace `0.15.0` in both places as needed):

```sh
curl -fsSL https://raw.githubusercontent.com/duned/codex-worker/v0.15.0/packaging/linux/install-server.sh | sudo bash -s -- --version 0.15.0
```

The installer resolves the latest GitHub Release by default, downloads the matching Linux x64 Server archive and `checksums.txt`, verifies SHA-256 before extraction, and installs the self-contained files. It creates the `codex-server` system account, enables and starts the systemd service, and preserves `/etc/codex-server/server.env` on repeat installs. An already installed version remains selected if download or checksum verification fails. For a local build, the existing `packaging/linux/install-server.sh PUBLISHED_DIRECTORY` form remains available.

Executables are versioned under `/opt/codex-server/releases` and `/opt/codex-server/current` selects the active release. Configuration and secrets are in `/etc/codex-server/server.env`; SQLite state is in `/var/lib/codex-server`; the service log directory is `/var/log/codex-server` and service output is available in the systemd journal. The environment file is root-owned and readable by the Server account (mode `0640`); keep it out of source control. Set `ASPNETCORE_ENVIRONMENT=Production`, `Server__ListenUrl=http://127.0.0.1:5090`, and `Server__DataDirectory=/var/lib/codex-server` there, plus the registration and management tokens described below. Add `CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY` when using Server-managed credentials. Temporary files use `/run/codex-server` and are not durable state.

Create a single-use Worker registration token as the Server account; it expires after 15 minutes and can be revoked before use. Pass the database path when the Server uses a non-default data directory:

```sh
sudo -u codex-server /opt/codex-server/current/CodexServer worker-token create /var/lib/codex-server/codex-server.db
```

On the Worker, use the URL and identity path from its managed configuration. The registration command verifies the Server response and stores the durable Worker credential with owner-only permissions beside the identity file. It stores the Server URL there too, so the managed YAML URL can remain at its installer placeholder. Run the command as the `codex-worker` service account so it can write its identity state, supplying the token through standard input from a protected secret source:

```sh
sudo -u codex-worker /opt/codex-worker/CodexWorker register \
  --server https://server.example \
  --token-stdin \
  --identity-file /var/lib/codex-worker/.codex-worker/worker-id
sudo systemctl enable --now codex-worker
```

The one-time token is accepted only for registration. Subsequent Worker requests use its durable per-Worker credential; the Server stores only hashes. Revoke an unused bootstrap token with `worker-token revoke <token> [database-path]`, or revoke a Worker credential with `worker-token revoke-worker <worker-id> [database-path]`. A new bootstrap registration for that same identity rotates its credential. Do not put either credential in service configuration or logs.

Use `codex-worker --help` for top-level usage and `codex-worker register --help` for registration options. Help exits successfully without creating identity files or contacting the Server. Invalid registration arguments print the command usage and fail before network activity. Worker↔Server failures include the operation, HTTP status, a bounded structured `error` message when available, and the Server request ID. Unstructured or oversized response bodies are omitted; request credentials and recognizable credential patterns are redacted. Registration rejections return `{ error, code, requestId }`, with a safe reason and stable code such as `invalid_worker_registration` or `invalid_bootstrap_token`. Server responses also carry `X-Codex-Request-Id`. Routine rejection logs contain only that request ID and reason code, without tokens or request payloads. Match the request ID in Worker output to Server logs for deeper diagnosis. Use `--token-stdin` to keep the registration token out of process arguments; `--capacity` accepts 1 through 8 (default 1).

Start and inspect the service with:

```sh
sudo systemctl enable --now codex-server
sudo systemctl status codex-server
sudo journalctl -u codex-server
```

Use the supported uninstaller to remove the service and installed releases while keeping configuration, credentials, and the database for a reinstall:

```sh
curl -fsSL https://raw.githubusercontent.com/duned/codex-worker/main/packaging/linux/uninstall-server.sh | sudo bash
```

To deliberately remove all Server resources owned by the installer, including its environment-file secrets and database, use `--purge`:

```sh
curl -fsSL https://raw.githubusercontent.com/duned/codex-worker/main/packaging/linux/uninstall-server.sh | sudo bash -s -- --purge
```

Purge removes `/opt/codex-server`, `/etc/codex-server`, `/var/lib/codex-server`, `/var/log/codex-server`, the service unit, and the dedicated service account/group when they match the installer's standard identity. Verify cleanup with `systemctl show codex-server.service` (it should report `LoadState=not-found`) and confirm those paths are absent. It does not remove external dependencies or operator-managed backups.

If installation fails, check that the host reports Ubuntu 24.04 and `x86_64`, that GitHub Releases are reachable, and that the selected version has both the Server archive and `checksums.txt`. Check `systemctl status codex-server` and `journalctl -u codex-server` for startup errors. The default listener is loopback only; configure a TLS terminating proxy and firewall before remote access.

Startup journal output includes version, runtime mode, endpoint, and state directory, but no credentials. The unit is in [`packaging/linux/codex-server.service`](../packaging/linux/codex-server.service). When exposing the service remotely, configure a TLS-terminating proxy and firewall rules at the network boundary.

## Worker installation and operations

The supported installer downloads a checksum-verified, self-contained Linux x64 Worker release and installs its systemd unit. It supports Ubuntu 24.04 x86_64 and does not need a source checkout, .NET SDK, or .NET runtime. Install the latest release or pin an explicit version:

```sh
curl -fsSL https://raw.githubusercontent.com/duned/codex-worker/main/packaging/linux/install-worker.sh | sudo bash
# Or pin a release:
curl -fsSL https://raw.githubusercontent.com/duned/codex-worker/v0.15.0/packaging/linux/install-worker.sh | sudo bash -s -- --version 0.15.0
```

Both commands run the installer from standard input; it also supports downloading the script and running it directly with `sudo bash install-worker.sh`. The installer reports success only after the verified binaries, configuration permissions, systemd unit, and required service operations have completed.

On a clean interactive install, the installer can ask for the Server URL, Worker capacity, whether to register, and whether to enable and start the service. For automation, pass the same choices as options. The following example installs latest; for a pinned release use its `vVERSION` script URL and matching `--version VERSION`. Supply the one-time bootstrap token through a root-readable protected file or `CODEX_WORKER_BOOTSTRAP_TOKEN`; it is never accepted as a command-line argument. For example, prepare a mode-0600 token file using your secret-management process, then run:

```sh
curl -fsSL https://raw.githubusercontent.com/duned/codex-worker/main/packaging/linux/install-worker.sh | sudo bash -s -- \
  --server https://server.example --capacity 2 --register --start --token-file /run/secrets/worker-bootstrap-token
```

The installer pipes the token to the registration command over standard input. It does not print the token. `--server` and `--capacity` update only those fields in the Worker YAML; on upgrade, existing YAML and environment settings are preserved unless those options are supplied. Registration requires a Server URL and token source. `--start` enables and starts the systemd service after installation and any requested registration. The final output states which actions completed.

Registration and an executable check run as the service account from a staged release before the runtime and systemd unit are activated. A failed clean installation stops/disables and removes the Worker unit and runtime, while retaining the system account, configuration, identity, and durable recovery credentials. It does not start an empty rollback directory. Retry the same installer command with `--register --start`, the same Server URL and identity, and a protected valid bootstrap token (create a fresh token if the previous one was rejected). Do not delete the staged identity or credentials: they allow recovery if the Server accepted registration but its response was lost. The installer reports retained paths and this retry procedure. Failed upgrades restore the previous binaries, unit, and YAML and only restart a previously active service.

This installs only the Worker process. Git, GitHub CLI (`gh`), Codex CLI and its service-account authentication, and outbound HTTPS access are needed for normal task execution. Tools such as Node.js, Docker, PostgreSQL, and project-specific .NET SDKs are discovered and can be handled through Server provisioning policy; they are not installer prerequisites.

The installer is repeatable and preserves existing configuration and environment files. It creates the `codex-worker` system account and uses these locations:

| Purpose | Location | Owner |
| --- | --- | --- |
| Executable and published binaries | `/opt/codex-worker` | root |
| Global Worker configuration | `/etc/codex-worker/worker.yml` | root, group-readable by Worker |
| Secret environment variables | `/etc/codex-worker/worker.env` | root, group-readable by Worker |
| Persistent identity, SQLite execution history, and runtime state | `/var/lib/codex-worker/.codex-worker` | codex-worker |
| Project configuration and configured checkouts | `/var/lib/codex-worker/projects` or operator-selected paths | codex-worker |
| Execution worktrees | `/var/lib/codex-worker/.codex-worker/worktrees` | codex-worker |

Edit `/etc/codex-worker/worker.yml`: set `server.url`, retain the persistent `server.identityFile`, and add local project YAML files under `projects.directory`. Each project configuration must identify its checkout and credentials through the supported local authentication setup. Managed enrollment requires `projects.ownership: managed` and `server.enabled: true`; the installed starter configuration selects those values. Keep `/etc/codex-worker/worker.env` root-owned with mode `0640` and put any required service environment values there. Do not put credentials in YAML or persistent service configuration.

Create a one-time registration token on the Server, then bootstrap the Worker before starting its service. Use `--token-stdin` and supply the token through standard input from a protected secret source; do not capture it in shell history, tracing, or logs. It stores a stable identity, Server URL, and separate durable Worker credential beside the identity file with owner-only permissions before contacting the Server. This local staging makes a lost response recoverable: retry with the same identity file, and the Worker will authenticate with its durable credential if the Server already committed registration. An invalid, expired, or reused bootstrap token leaves staged credentials in place but creates no Server Worker; create a fresh token and rerun the command with the same identity file. Server bootstrap commits token consumption, durable authentication, and Worker visibility atomically. After successful bootstrap, the Worker uses the durable credential, so `CODEX_SERVER_REGISTRATION_TOKEN` does not need to be set in `worker.env`.

```sh
sudo -u codex-worker /opt/codex-worker/CodexWorker register \
  --server https://server.example \
  --token-stdin \
  --identity-file /var/lib/codex-worker/.codex-worker/worker-id
```

Start and enable the service after bootstrap and project configuration:

```sh
sudo systemctl enable --now codex-worker
sudo systemctl status codex-worker
sudo journalctl -u codex-worker
```

On startup, the Worker loads or atomically creates its random identity at the configured persistent path, then sends an idempotent registration keyed by that ID. Repeated restarts or rerunning registration update that Server Worker record instead of creating another identity. It then fetches the versioned managed project snapshot and applies it before startup readiness checks. If registration or snapshot retrieval fails, a previously validated snapshot remains available and is selected for local configuration; without one, managed startup stops. The Worker never falls back to standalone mode, and Server-dependent assignment and provisioning remain unavailable during an outage. The Worker refreshes the snapshot while idle. The local API exposes applied and desired versions, synchronization state, last successful update time, and a bounded error through `/api/configuration-sync`. After configuration, the Worker reports discovered capabilities in heartbeats, then reports project-scoped GitHub/Git readiness and the authenticated Codex provider after startup preflight. The Server dashboard shows the Worker and current capabilities; provisioning can fill supported gaps according to Server plans and local Worker policy.

The unit runs as the unprivileged `codex-worker` account, starts after network availability, starts on reboot when enabled, restarts after runtime failures, and maps SIGTERM to graceful shutdown. Exit status 2 prevents a deterministic configuration or startup failure from entering a restart loop. Back up `/var/lib/codex-worker` to preserve identity, history, and recoverable execution state across host replacement; restoring the identity reconnects the replacement to the same Server Worker record.

For a controlled Linux Worker update, fetch the installer from the matching `vVERSION` tag and pass `--version VERSION`; it verifies the new release before switching binaries and preserves the previous release under `/opt/codex-worker.previous.*`. The local [`packaging/linux/update-worker.sh`](../packaging/linux/update-worker.sh) remains available for operators publishing from a local build; it stages and checks the new apphost, requests the loopback API to stop new claims, waits up to five minutes for active executions to finish, and preserves the old binaries if readiness fails.

```sh
dotnet publish src/CodexWorker/CodexWorker.csproj -c Release -r linux-x64 --self-contained true -o publish
sudo packaging/linux/update-worker.sh publish
```

Use the supported uninstaller to remove the service and installed release directories while retaining the configuration, credentials, Worker identity, execution history, and worktrees for a future reinstall:

```sh
curl -fsSL https://raw.githubusercontent.com/duned/codex-worker/main/packaging/linux/uninstall-worker.sh | sudo bash
```

For an E2E clean-install cycle, explicitly purge those resources:

```sh
curl -fsSL https://raw.githubusercontent.com/duned/codex-worker/main/packaging/linux/uninstall-worker.sh | sudo bash -s -- --purge
```

Purge removes the installer's `/opt/codex-worker` releases and upgrade backups, `/etc/codex-worker`, `/var/lib/codex-worker` (including identity, credentials, project checkouts, and worktrees), `/var/log/codex-worker`, the service unit, and the dedicated service account/group when they match the installer's standard identity. Verify cleanup with `systemctl show codex-worker.service` (it should report `LoadState=not-found`) and confirm those paths and `/opt/codex-worker.previous.*` directories are absent. It does not remove external tools such as Git, `gh`, Codex CLI, or other operator-installed dependencies.

For troubleshooting, inspect `systemctl status codex-worker` and `journalctl -u codex-worker`. Installer failures identify the unsupported OS/architecture, missing utility, download, checksum, or extraction step. A failed update preserves the previous binaries and attempts to restore the prior service. Check release availability and outbound HTTPS access if downloads fail.


## Manual local-build installation

For an operator-built self-contained Worker publish directory, use the local installer from the same checkout:

```sh
dotnet publish src/CodexWorker/CodexWorker.csproj -c Release -r linux-x64 --self-contained true -o publish
sudo packaging/linux/install.sh publish
```

The release installers (`install-server.sh` and `install-worker.sh`) and both uninstallers support the piped `bash` commands above as well as direct file execution, with Bash strict mode enabled. `install.sh` and `update-worker.sh` are local build tools: run them from files in the checkout; `install.sh` requires its sibling configuration and systemd unit.

This path installs the binaries and starter configuration without enrolling or starting the Worker. Complete the manual enrollment and service steps above. For Server, use `sudo packaging/linux/install-server.sh PUBLISHED_DIRECTORY`; its local publish mode enables and starts the Server service. These are advanced build-based paths; use published releases for the normal clean-machine flow.
