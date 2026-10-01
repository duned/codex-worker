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
```

`status` checks the configured local database through the Server health service. It reports control-plane persistence readiness and explicitly reports process health as `not-observed`: a separate offline command cannot establish whether the web process is running. `diagnostics` summarizes registered Worker availability and lifecycle, reported capacity, and project count from the local registry. It does not probe node capabilities, and Worker availability does not establish project eligibility.

`config show` displays effective non-secret settings. Data-directory and database paths are redacted. `config validate` checks Server settings and path resolution without opening or creating the database. Registration, management, and credential-encryption secrets are not included in either output. JSON documents include `contractVersion: 1` for callers that need a versioned output contract.

Worker commands use the configured Server database directly and do not contact the running Server. `worker show` emits the Worker registry projection and the separate credential-delivery authorization status. `enable`, `drain`, and `disable` change only the Server-owned scheduling policy. Enabled Workers may receive assignments; Draining and Disabled Workers receive none. Both policies preserve already assigned or running executions and their leases. The active assignment count makes a drain visible until it reaches zero; the stored policy remains Draining until an operator enables or disables scheduling. Reported heartbeat lifecycle and readiness remain Worker observations.

`worker revoke-token` revokes the generated per-Worker API token and records its revocation time. This blocks new calls authenticated by that token. It does not directly delete active assignment leases; without authorization, the Worker cannot renew or report them, so the existing lease expiry and recovery path applies. `worker revoke-delivery-token` revokes the independent Worker credential-delivery token. A Worker API-token revocation does not revoke delivery authorization, and delivery-token revocation blocks future retrieval without revoking the Worker API token or erasing secrets already delivered. The one-use bootstrap authorization is consumed at registration. The shared `CODEX_SERVER_REGISTRATION_TOKEN` remains a server-wide fallback for Worker API endpoints; a per-Worker token revocation does not revoke that fallback. Operators who need to remove all access through the fallback must rotate or remove the Server configuration value.

`projects` reads and changes central projects through the same registry contracts used by the management API. It opens only the configured local SQLite database and does not require the web Server to be running. Create/update read a JSON `CentralProjectDefinition` with `name`, `repository`, `defaultBranch`, `description`, and optional structured `requirements`. `list`, `show`, and mutations support `--json`. Every definition update, enable/disable transition, and delete requires the current revision; stale revisions return exit code `3` with the current revision. Disabled projects retain queued requests and active executions: the requests remain queued until re-enabled, and assigned/running executions keep their current lease and may finish. Deletion returns exit code `3` and state counts while queued, assigned, or running requests still refer to the project. Missing projects return exit code `4`.

These operations inspect local configuration or state only. They do not call `/livez`, `/readyz`, `/health`, or any other loopback endpoint; status and diagnostics JSON include `loopbackContacted: false`. The read-only status and diagnostic queries can run while the Server is active. Restore remains an offline backup operation and requires the Server to be stopped.

## Configuration resolution

The commands use the same configuration builder and precedence as the Server host: the published application's `appsettings.json` and environment-specific settings, environment variables, then command-line configuration options. The application content root is the installed executable directory. `Server:DataDirectory` defaults to the current user's home data directory, and a relative `Server:DatabasePath` is resolved from that data directory. `--Server:<setting>=<value>` options can be supplied to an administration command for a one-off override.

On Linux installations, invoke these commands through the installed helper with `sudo`. The helper runs them as the `codex-server` service account, from the published application directory, with `/etc/codex-server/server.env` loaded by systemd. This selects the same data directory and database as the service without printing values from the environment file. Direct executable invocations use the caller's environment and should only be used when it matches the service configuration or when the intended configuration is supplied explicitly.

## Output and exit codes

Human-readable output is intended for operators. Use `--json` for structured output; diagnostics remain concise and exclude Worker identities and secret payloads. Invalid command syntax returns `2`, unhealthy or invalid local state returns `1`, and Ctrl+C returns `130`. Local status and diagnostics have a 15-second command deadline; a timeout returns `1` with recovery guidance.

Configuration validation prints actionable setting-level errors without echoing supplied values. Check the installed environment file and application configuration for the named setting, then rerun `codex-server config validate`. A status or diagnostics failure directs the operator to check the configured data directory, database, and filesystem permissions.
