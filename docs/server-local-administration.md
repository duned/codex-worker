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
```

`status` checks the configured local database through the Server health service. It reports control-plane persistence readiness and explicitly reports process health as `not-observed`: a separate offline command cannot establish whether the web process is running. `diagnostics` summarizes registered Worker availability and lifecycle, reported capacity, and project count from the local registry. It does not probe node capabilities, and Worker availability does not establish project eligibility.

`config show` displays effective non-secret settings. Data-directory and database paths are redacted. `config validate` checks Server settings and path resolution without opening or creating the database. Registration, management, and credential-encryption secrets are not included in either output. JSON documents include `contractVersion: 1` for callers that need a versioned output contract.

All four operations inspect local configuration or state only. They do not call `/livez`, `/readyz`, `/health`, or any other loopback endpoint; status and diagnostics JSON include `loopbackContacted: false`. The read-only status and diagnostic queries can run while the Server is active. Restore remains an offline backup operation and requires the Server to be stopped.

## Configuration resolution

The commands use the same configuration builder and precedence as the Server host: the published application's `appsettings.json` and environment-specific settings, environment variables, then command-line configuration options. The application content root is the installed executable directory. `Server:DataDirectory` defaults to the current user's home data directory, and a relative `Server:DatabasePath` is resolved from that data directory. `--Server:<setting>=<value>` options can be supplied to an administration command for a one-off override.

On Linux installations, invoke these commands through the installed helper with `sudo`. The helper runs them as the `codex-server` service account, from the published application directory, with `/etc/codex-server/server.env` loaded by systemd. This selects the same data directory and database as the service without printing values from the environment file. Direct executable invocations use the caller's environment and should only be used when it matches the service configuration or when the intended configuration is supplied explicitly.

## Output and exit codes

Human-readable output is intended for operators. Use `--json` for structured output; diagnostics remain concise and exclude Worker identities and secret payloads. Invalid command syntax returns `2`, unhealthy or invalid local state returns `1`, and Ctrl+C returns `130`. Local status and diagnostics have a 15-second command deadline; a timeout returns `1` with recovery guidance.

Configuration validation prints actionable setting-level errors without echoing supplied values. Check the installed environment file and application configuration for the named setting, then rerun `codex-server config validate`. A status or diagnostics failure directs the operator to check the configured data directory, database, and filesystem permissions.
