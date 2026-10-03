# Worker local administration

Use `codex-worker <command> --help` or nested help such as
`codex-worker config validate --help` and `codex-worker provision login --help`.
Help does not load configuration, create identity files or perform provisioning.
The existing root help alias `h` remains supported.

| Command | Local behavior |
| --- | --- |
| `status`, `diagnostics` | The same versioned configuration, registration metadata and tool-version snapshot, with actionable human diagnostics. These observations do not contact Server, check authenticated execution readiness, or inspect the running service. Lifecycle and active capacity remain unknown. Missing configuration/tools are reported as state, with exit 0 when collection completes. |
| `config [show]` | Inspect redacted Worker configuration. `config --json` is equivalent to `config show --json`. |
| `config validate` | Validate Worker and project configuration without starting execution; invalid configuration exits 2. |
| `config set <setting> <value>` | Atomically update an existing allowlisted setting after complete validation. Protected installed configuration requires appropriate local permissions. Restart the service to apply the change. |
| `capabilities [list]`, `capabilities refresh` | Observe typed local capability inventory or force fresh detection. Missing capabilities are inventory state. |
| `register` | Enroll using `--token-stdin` or the existing `--token` option. Explicit `--server`, `--identity-file` and `--capacity` override installed configuration defaults. Registration does not establish execution readiness. |
| `provision status` | Inspect capability state, typed authentication dependencies and local action policy. |
| `provision <operation> <capability-id>` | Run an allowlisted typed operation under node-local provisioning policy. Package changes require `--allow-elevation` as well as policy permission. `verify-repository-access` requires `--repository owner/repository`. |

These commands accept `--json` for automation. Successful observations and
operation results retain their existing versioned contracts. Adapter errors emit
one JSON object with `contractVersion: 1`, `status: "failed"` and a bounded,
redacted `diagnostic` containing `code` and `message`. Provisioning progress is
written separately to stderr. No registration token is returned in output.

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

The local status contract includes the validated stable `registration.workerId`,
`credentialState` (`persisted`, `environment`, `missing`, `invalid`, `unreadable`,
or `not-applicable`), validated `serverUrl`, and `associationSource`. Identity
state distinguishes `local-identity-present`, `local-identity-missing`,
`local-identity-invalid`, and `local-identity-unreadable`. Status never creates or
repairs identity or credential files and never returns their secret contents.
The `runtime` object reports the .NET framework and process/OS architectures;
Worker version, observed capabilities and local readiness remain separate fields.

`serverAcceptance` remains `unverified` for a configured Server: local identity,
credentials and a persisted association do not prove successful enrollment or
detect remote revocation. A `.server` file can describe an attempted enrollment
whose response was rejected or lost. Restart preserves that identity and token;
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
access (`checkconfiguration`), and requires no CLI login. Inventory also exposes
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
standalone checkout remains a startup configuration error. Provision tools and
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
