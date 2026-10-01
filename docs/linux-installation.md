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

Run `codex-server --help` (or `/opt/codex-server/current/CodexServer --help`) for CLI usage, and `codex-server --version` for the version. Both exit without starting a web host, reading service secrets, or creating persistent state, and work without sudo while the service is running. Invalid commands fail without starting a second Server.

Use `sudo codex-server status` and `sudo codex-server diagnostics` to inspect local control-plane persistence, `sudo codex-server provision list` to inspect typed command history, or `sudo codex-server config show` / `sudo codex-server config validate` to inspect the effective configuration. These commands run as the Server service account with `/etc/codex-server/server.env`, use the configured data directory and database, and do not contact loopback or start another Server process. Status reports persistence readiness; it cannot observe the separate service process. Paths and credentials are redacted. See the [local administration guide](server-local-administration.md) for command lifecycle, JSON output and exit codes.

Create a single-use Worker registration token using the installed operator helper; it expires after 15 minutes and can be revoked before use:

```sh
sudo codex-server worker-token create
```

Inspect and administer enrolled Workers with `sudo codex-server worker show <worker-id>`, `worker enable`, `worker drain`, and `worker disable`. These update the Server's scheduling policy and preserve active assignment leases. Use `worker revoke-token` for the generated Worker API token and `worker revoke-delivery-token` for the separate credential-delivery token. The local administration guide defines the drain, revocation, and shared registration-token fallback behavior.

The installed `codex-server` helper runs token commands as the Server service account using the same `/etc/codex-server/server.env` and published application configuration as the service. It requires systemd and root (`sudo`); no binary or database path is needed. Token creation writes only the token to stdout; lifetime and the selected database path go to stderr. Copy only the token value. Surrounding spaces and CRLF line endings are accepted by Worker registration; embedded whitespace is rejected before staging credentials.

Token commands can run while the service is running. Direct binary invocations require a configured `Server__DataDirectory` / `Server__DatabasePath` or an explicit database path; they fail rather than silently selecting the caller's home directory. The helper explicitly permits the service-account default for retained installations without overriding data paths in the service environment file or published application configuration.

On the Worker, use the URL and identity path from its managed configuration. The registration command verifies the Server response and stores the durable Worker credential with owner-only permissions beside the identity file. It stores the Server URL there too, so the managed YAML URL can remain at its installer placeholder. Run the command as the `codex-worker` service account so it can write its identity state, supplying the token through standard input from a protected secret source:

```sh
sudo -u codex-worker /opt/codex-worker/CodexWorker register \
  --server https://server.example \
  --token-stdin \
  --identity-file /var/lib/codex-worker/.codex-worker/worker-id
sudo systemctl enable --now codex-worker
```

The one-time token is accepted only for registration. Subsequent Worker requests use its durable per-Worker credential; the Server stores only hashes. Revoke an unused bootstrap token with `sudo codex-server worker-token revoke <token>`, or revoke a Worker credential with `sudo codex-server worker-token revoke-worker <worker-id>`. A new bootstrap registration for that same identity rotates its credential. Do not put either credential in service configuration or logs.

Fresh installations explicitly set `Server__DataDirectory=/var/lib/codex-server`. Upgrades preserve the existing environment file and database selection. Older installations with no data-directory override use the service account's `/var/lib/codex-server/.local/share/codex-server/codex-server.db`; an explicit token command targeting `/var/lib/codex-server/codex-server.db` writes to a different database and cannot authorize that service. Use the helper to generate a fresh token against the configured database, then rerun Worker installation/registration with the same identity directory. Do not delete or relocate either database as a registration repair. Failed registration retains staged local credentials for safe retry, creates no Server Worker, and prevents `--start` from starting a fresh service.

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

On a clean interactive install, the installer can ask for the Server URL, Worker capacity, whether to register, and whether to enable and start the service. When registration is selected and no token source is supplied, it prompts for the one-time bootstrap token without echoing it. The prompt reads from the controlling terminal, so it works with the piped `curl | sudo bash` installation pattern without consuming the script from standard input. The token is passed to Worker registration over standard input and is not saved to disk or included in process arguments or installer output.

For automation, pass the same installation choices as options and provide the token with either `CODEX_WORKER_BOOTSTRAP_TOKEN` or a root-readable mode-0600 file specified by `--token-file PATH`. Tokens are never accepted as command-line values. The following example installs latest; for a pinned release use its `vVERSION` script URL and matching `--version VERSION`. Prepare the token file using your secret-management process, then run:

```sh
curl -fsSL https://raw.githubusercontent.com/duned/codex-worker/main/packaging/linux/install-worker.sh | sudo bash -s -- \
  --server https://server.example --capacity 2 --register --start --token-file /run/secrets/worker-bootstrap-token
```

The installer pipes the token to the registration command over standard input. It does not print the token. A non-interactive `--register` invocation must use one of these unattended sources and fails early with guidance if neither is supplied. `--server` and `--capacity` update only those fields in the Worker YAML; on upgrade, existing YAML and environment settings are preserved unless those options are supplied. Registration requires a Server URL and token source. `--start` enables and starts the systemd service after installation and any requested registration. The final output states which actions completed.

Registration and an executable check run as the service account from a staged release before the runtime and systemd unit are activated. A failed clean installation stops/disables and removes the Worker unit and runtime, while retaining the system account, configuration, identity, and durable recovery credentials. It does not start an empty rollback directory. Retry the same installer command with `--register --start`, the same Server URL and identity, and a protected valid bootstrap token (create a fresh token if the previous one was rejected). Do not delete the staged identity or credentials: they allow recovery if the Server accepted registration but its response was lost. The installer reports retained paths and this retry procedure. Failed upgrades restore the previous binaries, unit, and YAML and only restart a previously active service.

This installs the Worker process. Git, GitHub CLI (`gh`), Codex CLI and its service-account authentication, and outbound HTTPS access are needed for normal task execution. Tools such as Node.js, Docker, PostgreSQL, and project-specific .NET SDKs are not installer prerequisites. The local administration CLI can inspect and provision its catalogued tools under Worker-local policy; project-specific SDKs remain outside its supported operations.

Installation and registration succeed on an **unprovisioned** node without .NET, GitHub CLI, Codex, Node.js or project runtimes. The released Worker is self-contained. Registration establishes node identity and Server authentication; capability detection is observational and unavailable execution tools do not fail registration. Managed startup keeps the service, local API, heartbeats, configuration synchronization and typed provisioning paths online while reporting `not-ready`. Missing Codex, missing authentication, probe errors and failed execution preflight withhold `agent-provider/codex` and prevent Issue assignments. Project Git/GitHub failures withhold scoped readiness. Provisioning refreshes observations and readiness without reinstalling or re-registering. No tools or authentication are installed merely to make bootstrap succeed. Standalone ownership retains its startup-failure behavior.

The packaged unit uses `HOME=/var/lib/codex-worker`, `TMPDIR=/run/codex-worker` (created by systemd for the service lifetime), and `PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin`. The protected `/etc/codex-worker/worker.env` can override these values. Unix executable resolution uses only absolute entries in the effective child PATH, without consulting interactive shell profiles or searching private nvm/npm locations. Empty and relative PATH entries do not enable current-directory fallback. For an existing CLI outside that PATH, set `CODEX_WORKER_CODEX_EXECUTABLE=/absolute/path/to/codex` in `worker.env`. This selects the same executable for preflight and tasks on each cold start. Its interpreter/runtime must also be accessible to `codex-worker`. Existing service-account Codex authentication is retained through `CODEX_HOME`; credentials are never copied from an interactive user.

After the external capability/environment has been corrected, restart with `sudo systemctl restart codex-worker`, then inspect `systemctl status codex-worker` and `journalctl -u codex-worker`. The installer uses `Type=exec`: a completed service-start request means the Worker binary launched, and readiness must be checked separately. Preflight diagnostics distinguish a missing executable, an unusable executable or interpreter/loader, directory/access failures, timeout, and a process that exits unsuccessfully. Preflight creates and probes its temporary directory before launch, retains it until the subprocess exits, and cleans it afterward. Failed-process output and unexpected responses are omitted to protect authentication material.

To check Codex with the service account/environment independently of registration or projects:

```sh
sudo systemd-run --wait --collect --service-type=exec \
  -p User=codex-worker -p Group=codex-worker \
  -p WorkingDirectory=/var/lib/codex-worker \
  -p 'Environment=HOME=/var/lib/codex-worker PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin' \
  -p EnvironmentFile=/etc/codex-worker/worker.env -p UMask=0077 \
  -p RuntimeMaxSec=90 /opt/codex-worker/CodexWorker --codex-preflight
```

This standalone check uses the system temporary directory unless `worker.env` overrides it. If it sets `TMPDIR=/run/codex-worker`, the persistent service must be running so that directory exists. The check performs the existing authenticated Codex request; it does not install or log in to Codex, load projects, or contact the Server.

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

### Local administration CLI

The installed executable reads `/etc/codex-worker/worker.yml` by default. Administration commands accept `--config <path>` to inspect or operate on another Worker configuration. Help is available without contacting the Server:

```sh
/opt/codex-worker/CodexWorker --help
/opt/codex-worker/CodexWorker status --help
/opt/codex-worker/CodexWorker config --help
/opt/codex-worker/CodexWorker capabilities --help
/opt/codex-worker/CodexWorker provision --help
```

Inspect status, validate or update configuration, and list or refresh capability observations with:

```sh
/opt/codex-worker/CodexWorker status
/opt/codex-worker/CodexWorker config show
/opt/codex-worker/CodexWorker config validate
/opt/codex-worker/CodexWorker capabilities list
/opt/codex-worker/CodexWorker capabilities refresh --json

# An explicit configuration path works on every administration command.
/opt/codex-worker/CodexWorker status --config /srv/codex-worker/worker.yml --json
/opt/codex-worker/CodexWorker config show --config /srv/codex-worker/worker.yml
```

`config set` updates only its documented settings and validates the complete YAML before replacing it. Provisioning mutations require both an enabled local policy, an allowlisted action key, and an explicit `--allow-elevation`; on Linux, a non-root invocation also needs non-interactive sudo permission for the product-owned package commands. For example, as an administrator:

```sh
sudo /opt/codex-worker/CodexWorker config set worker.provisioning.enabled true
sudo /opt/codex-worker/CodexWorker config set worker.provisioning.allowedPrivilegedActions 'tool:git:install,tool:git:update,tool:git:uninstall'
sudo /opt/codex-worker/CodexWorker config validate
sudo /opt/codex-worker/CodexWorker provision status --json
sudo /opt/codex-worker/CodexWorker provision install git --allow-elevation --json
sudo /opt/codex-worker/CodexWorker provision upgrade git --allow-elevation
sudo /opt/codex-worker/CodexWorker provision uninstall git --allow-elevation
```

Supported typed actions include capability detection, package install/upgrade/uninstall, Git configuration checks, GitHub CLI authentication setup and checks, and bounded SSH key/repository-access operations. Package operations are available on Debian-based Linux systems; this installer supports Ubuntu 24.04 x86_64. A denied operation reports the controlling Worker policy and a remediation. `--json` writes versioned status, configuration, capability-inventory, and provisioning contracts to standard output; progress for interactive authentication remains separate from the final JSON result. The commands do not expose shell text or caller-selected packages.

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

For a controlled Linux Worker update, fetch the installer from the matching `vVERSION` tag and pass `--version VERSION`; it verifies the new release before switching binaries, updates the service to `CodexWorker run --config /etc/codex-worker/worker.yml`, and preserves the previous release under `/opt/codex-worker.previous.*`. It preserves the existing Worker configuration and identity. The local [`packaging/linux/update-worker.sh`](../packaging/linux/update-worker.sh) remains available for operators publishing from a local build; it stages and checks the new apphost, requests the loopback API to stop new claims, waits up to five minutes for active executions to finish, migrates the previous positional `ExecStart` to the supported command form, and preserves the old binaries if readiness fails.

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


Both uninstallers can be rerun after an interrupted removal or against an already-uninstalled machine. They stop cached/running services even if the unit file is missing, clear retained failures before removing the unit, remove enablement links, reload systemd, and verify that the unit is absent and inactive. Missing files, directories, accounts, and groups are reported as already absent; unexpected systemd, filesystem, or account lookup/removal errors still fail the command. Normal uninstall removes logs and runtime files but preserves configuration, persistent data, and service identities. Purge also removes persistent data and installer-standard service identities; an account with a nonstandard home or primary group and its group are retained together. Worker release backups are removed in both modes.

## Manual local-build installation

For an operator-built self-contained Worker publish directory, use the local installer from the same checkout:

```sh
dotnet publish src/CodexWorker/CodexWorker.csproj -c Release -r linux-x64 --self-contained true -o publish
sudo packaging/linux/install.sh publish
```

The release installers (`install-server.sh` and `install-worker.sh`) and both uninstallers support the piped `bash` commands above as well as direct file execution, with Bash strict mode enabled. `install.sh` and `update-worker.sh` are local build tools: run them from files in the checkout; `install.sh` requires its sibling configuration and systemd unit.

This path installs the binaries and starter configuration without enrolling or starting the Worker. Complete the manual enrollment and service steps above. For Server, use `sudo packaging/linux/install-server.sh PUBLISHED_DIRECTORY`; its local publish mode enables and starts the Server service. These are advanced build-based paths; use published releases for the normal clean-machine flow.
