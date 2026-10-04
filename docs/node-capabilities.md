# Node provisioning inventory

`GET /api/v1/nodes` (management bearer authentication) returns the Server node
with ID `server` and every registered Worker. Each capability includes a stable
identifier, display metadata, observed state, and currently available actions.
Identifiers are `git`, `github-cli`, `codex-cli`, `dotnet-sdk`,
`dotnet-runtime`, and `docker`. The shared
CodexProvisioning assembly owns this contract and catalog; add future capability
providers there rather than encoding tool-specific states in node lifecycle.

Installation, update status, authentication, configuration, capability health,
and operation state are independent dimensions. `null` authentication or
configuration means that dimension does not apply. `Unknown` means detection has
not established the fact. Git configuration detects a nonempty user.name and
user.email in the service environment; their values are never returned. CLI
authentication uses `gh auth status` and `codex login status` in the node's service
environment. This is distinct from repository-scoped credentials and project
execution eligibility. Update detection compares the installed and candidate
package versions from the local apt index for apt-managed tools, and compares Codex's
detected version with the official npm stable channel. An unavailable index,
registry or version probe leaves update status `Unknown`, without invalidating
installation or authentication observations. Apt observations reflect the last
index refresh, not a live distribution release lookup.

Worker registration and heartbeats carry an optional `capabilityInventory` in
managed contract v2. Older clients remain accepted and appear with unknown
provisioning facts. The scheduler continues to use its existing project-specific
capability requirements. Node execution readiness is a conservative inventory
summary, not a replacement for project eligibility, capacity, or lease decisions.
Each capability also exposes `readiness.available` and bounded `readiness.blockingReasons`.
Readiness is derived from the provider definition: installation, required typed
authentication and local configuration, successful observation, and idle operation
must all be satisfied. Providers with no credential dependency, such as .NET,
can become available immediately after installation. Installed Codex or GitHub CLI
without authentication remains installed and reports `authentication-required`.
Worker lifecycle/status diagnostics qualify missing prerequisites with the provider
ID, for example `github-cli:authentication-required`. Tool health is separate from
Worker process health; missing tools leave a connected Worker alive and provisionable.

Worker scheduling requires all catalog providers marked `requiredForExecution`,
plus a successful agent execution preflight and existing project preparation.
Optional workload providers do not block generic execution readiness. The Server
also rejects new assignments when a reported inventory lacks execution prerequisites;
clients without an inventory retain the existing requirement checks. A task requiring
an inventoried tool also requires that provider's authentication and configuration;
for example, a Docker task is ineligible until daemon access is satisfied. Repository
access, task requirements, capacity and lease authority remain separate checks.
Cached observations are periodically refreshed even while tasks are active; detected
authentication loss stops new scheduling without cancelling existing executions.
A failed agent preflight is retried only after changed agent observations or explicit
provisioning, rather than a service retry loop.

Server execution readiness is `not-applicable`. Connectivity and provisioning
readiness describe whether the control plane can interact with the node, even
when its tools are missing or authentication is required.

Observations are detected locally and cached in memory for five minutes. Worker
heartbeats send the latest observations. Successful capability changes and every
terminal managed provisioning report (including failure) trigger re-detection;
the next heartbeat publishes the refreshed facts. Existing Worker
`refresh-capabilities` plans trigger immediate re-detection and publication.
`POST /api/v1/nodes/server/capabilities/refresh` re-detects Server-local facts.
Install/update/uninstall/logout executors use the same
re-detection boundary after both successful and failed changes. Never infer
success from an operation result alone.

Worker operation state is projected from the latest applicable legacy plan or
typed command, including its source, action and bounded diagnostic code. Running
operations and disconnected Workers expose no available actions. The initial
catalog advertises `refresh`, `install`, `update`, and `uninstall` for all catalog
tools, plus `checkconfiguration` for Docker, `checkconfiguration` and SSH key/access actions for Git,
`prepareauthentication`/`checkauthentication`/`logout` for GitHub CLI, and
`login`/`checkauthentication`/`logout` for Codex CLI. These indicate registered operations, not permission to mutate a node:
local policy and platform support are checked at execution.

The registry retains the latest Worker report in its existing registration and
heartbeat JSON, along with existing durable plan history. This is a last-seen
snapshot, not authoritative machine configuration; disconnected snapshots and observations older than six minutes are
marked stale. Server facts are never persisted. Detection drains bounded process
output but exports only parsed numeric versions and fixed diagnostic codes.
Inventory input validation rejects arbitrary diagnostic strings and unknown IDs;
credentials, private keys, tokens, user identities, and raw command output are not
API metadata. Validated public SSH identities are returned only by explicit
generate/inspect commands.

## Managed tool lifecycle policy

The shared node command executor serves both Server-local and managed Worker
commands. The initial target is packaged Ubuntu 24.04 LTS or newer with apt,
distribution Node.js/npm and network access to configured apt sources and
`https://registry.npmjs.org`. Other Debian-family nodes must provide compatible
distribution packages; unsupported platforms reject mutation. No shell profile,
nvm or interactive operator environment is consulted. The packaged services
include `/usr/local/bin` and `/usr/bin` in PATH; custom service deployments must
keep those paths visible. Detection and final verification use the effective
service environment, so a shadowing or inaccessible executable cannot be assumed
to have been replaced successfully.

Git and GitHub CLI use the configured distribution apt candidates (`git` and
`gh`). The product does not add third-party repositories or select upstream
latest versions. Install and update refresh apt indexes and ensure the package,
including reinstallation after removal or interrupted installation. Codex uses
the [official npm install/update mechanism](https://developers.openai.com/cookbook/examples/codex/using_goals_in_codex),
with the stable `@openai/codex@latest` channel from the explicit official npm
registry, distribution `nodejs`/`npm`, and system prefix `/usr/local`. Alpha
channels, arbitrary package names and caller-supplied versions are not accepted.
Its npm mutations ignore user/global npmrc files, use a product cache under
`/var/cache/codex-provisioning/npm`, and make the installed package readable and
executable by the service account despite service umask 077. No force-overwrite
option is used: unrelated executable conflicts fail safely.

Uninstall uses apt remove or npm uninstall for the selected package only. It does
not purge, autoremove dependencies, delete HOME/configuration/cache/history or
log out. Node.js/npm remain after Codex removal. Existing unmanaged installations
outside these providers are observed but not deleted; if one remains visible
after uninstall the command fails verification rather than deleting its files.
Repeated operations converge through the package manager and final re-detection.

Every mutation requires explicit node-local elevation authorization (including
when already root). Only effective UID zero skips elevation; usernames and
inherited environment values do not establish filesystem privileges. Fixed apt,
Codex npm system-prefix mutations (install, update and uninstall), and managed
permission steps use absolute `/usr/bin/sudo -n` when elevation is needed.
Capability detection and authentication run separately without elevation.
Package mutations share a local executor gate; apt also retains its
native interprocess locks. A failed step stops the sequence. Deadlines/shutdown
cancel the child process tree; a separate bounded refresh records whatever was
actually changed. Terminal reports contain fixed diagnostic codes, never process
output. Inspect refreshed installation/version facts after `ProcessFailed`,
`Cancelled`, `TimedOut` or reconciled `Interrupted`, resolve package locks/network
or local authorization failures, and explicitly retry install/update/uninstall.
Codex npm permission failures identify the managed prefix/cache and the failed
provider step, with guidance to check the fixed-command sudo authorization and
filesystem permissions; raw npm output is never returned.
The command store's existing acknowledgement/reconciliation rules prevent
automatic replay of an uncertain operation.

## .NET and Docker providers

Provider definitions and local Worker inventory expose typed `provides`,
`authenticationDependencies`, `configurationDependency`, `requiredMajorVersion`,
and `requiredForExecution` metadata. GitHub CLI and Codex require their respective
node-local CLI login independently of installation. .NET and Docker declare no
authentication dependency. Workload tools are optional for the generic node
execution-readiness summary; project capability requirements and execution
preflight remain authoritative for scheduling.

`dotnet-sdk` manages `dotnet-sdk-10.0` and provides the SDK, .NET runtime and
ASP.NET Core runtime. `dotnet-runtime` manages `aspnetcore-runtime-10.0`, covering
both runtimes needed by framework-dependent Worker/Server deployments, without
requiring an SDK. Package names follow the
[.NET Ubuntu installation guidance](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install).
Detection uses `dotnet --list-sdks` / `--list-runtimes`, selecting the newest
stable 10.0 component rather than the SDK selected by a working directory's
`global.json`. Runtime readiness requires both `Microsoft.NETCore.App` and
`Microsoft.AspNetCore.App` 10.0; another major version or preview does not satisfy
these providers. Final verification checks the managed `/usr/bin/dotnet` path.
Compatible apt sources must already be configured for the node's distribution;
the product does not add feeds, import keys or execute downloaded scripts.
`PackageUnavailable` directs operators to configure compatible local sources.

`docker` manages the distribution `docker.io` package, as permitted by the
[Docker installation overview](https://docs.docker.com/engine/install/).
The CLI version establishes installation. `docker info --format
'{{.ServerVersion}}'` checks daemon access in the service account's Docker context;
failed access reports configuration required, not missing authentication.
`CheckConfiguration` repeats this read-only check without sudo. Operators must
provide a running daemon and appropriate service-account access locally. The
provider does not change group membership, socket permissions or daemon
configuration, and never runs a container as a readiness probe. Package-manager
installation success is independent of daemon readiness.

All new providers use the existing bounded executor, local elevation policy,
package mutation gate, failure refresh and cancellation semantics. Uninstall
removes the selected package without purge/autoremove or explicit data deletion;
Docker images/volumes, configuration and unrelated .NET versions are retained.
Package-manager dependency rules still apply (for example removing a runtime can
remove packages that depend on it). Review local dependencies before uninstall.

## Server dashboard

Open the Server dashboard and connect with its management token, then select the
Server or any registered Worker under **Node provisioning**. The view shows
connectivity and service health separately from execution and provisioning
readiness, including stale observations, installation/version, update,
authentication/configuration, and sanitized diagnostic codes. Refresh view reads
the latest API snapshot; **Refresh / Re-detect** queues node-local detection.

Actions come from each capability's API `availableActions`. Tool mutations require
an explicit elevation checkbox; local policy still decides whether execution is
permitted. Uninstall and logout require confirmation. Actions for the selected
node are disabled during submission or pending/running operations, and when
refresh fails. Polling every five seconds restores operation state after reload
and displays queued, started, deadline and completion timestamps. Failed, timed
out and cancelled commands offer an explicit retry only while the original action
remains available. The history panel presents legacy plans and typed commands
with distinct labels. Queued typed commands can be cancelled. A running command
can be reconciled only after its deadline and an operator confirms node
quiescence. Running commands are never automatically replayed, including when a
deadline passes or an acknowledgement is lost. Provisioning readiness describes
whether node actions can be dispatched; Worker diagnostics keep it separate from
project eligibility and execution readiness.

## Legacy plan compatibility

`/api/v1/provisioning` and the Worker plan executor remain supported for existing
clients and persisted history. Plan creation is deprecated for new operator
integrations; use `/api/v1/provisioning/commands` or the Server's local
`codex-server provision` commands for new work. Plans and typed commands retain
separate contracts and storage because accepted/running plan replay and typed
command at-most-once reconciliation have different safety semantics. There is
no automatic conversion or merged lifecycle. Server backups retain plan history
and include typed command history when that table is present; older backups
without typed command history remain restorable.

GitHub login preparation directs operators to complete browser/device authorization
in a terminal on the node as the service account, then check authentication.
The dashboard does not collect credentials or display raw process logs, tokens or private keys.
The same node-local operations are available through the Worker CLI as
`codex-worker provision prepare-authentication github-cli`,
`codex-worker provision check-authentication github-cli`, and
`codex-worker provision logout github-cli`. Preparation and status do not prove
provider-side scopes or repository read/write authorization. Logout removes the
product's local login state; it does not revoke the provider grant.

Codex CLI also advertises **Sign in with device code** (`Login`). With local
provisioning enabled and Worker credential/nonprivileged actions authorized,
the node runs the [supported device authentication flow](https://developers.openai.com/codex/auth)
(`codex login --device-auth`) as the actual service account, without elevation.
Enable device code login in ChatGPT security settings or workspace permissions.
The management-authenticated dashboard shows the fixed OpenAI verification URL
and one-time user code while the command is running. Approve only the login you
started for the selected node. This flow does not collect passwords or copy
another user's credentials. Unsupported or disabled device authentication fails
with the existing sanitized diagnostic; it does not fall back to credential copying.

The dashboard gives login a ten-minute deadline. Queued/running commands represent
pending authentication; successful login must also pass `codex login status`.
Failed, cancelled and timed-out logins clear instructions; timeout represents an
expired attempt and requires an explicit new login. Instructions exist only in
Server memory, never in SQLite command history, inventory or logs, and are hidden
at the deadline. After a Server restart the operation is not replayed and its
instructions are unavailable; use the existing quiescence/reconciliation process
for unacknowledged operations. No raw CLI output or access/refresh tokens cross
the provisioning API. Credentials remain in the service's local Codex storage.

Detection, login, logout and execution share `CODEX_HOME` (or the service account's
`.codex` directory) and the absolute `CODEX_WORKER_CODEX_EXECUTABLE` override when
configured. Packaged service PATH includes the system npm prefix. Custom
installations must keep the executable and home accessible to their service user.
Logout uses Codex's own logout operation in that identity; uninstall retains the
local auth/configuration as described above.
The Worker CLI exposes the same Codex lifecycle through
`codex-worker provision login codex-cli`,
`codex-worker provision check-authentication codex-cli`, and
`codex-worker provision logout codex-cli`. Human-readable provisioning status
labels this as node authentication and states that it is separate from Worker
registration, Server credential delivery, provider-side permissions and project
scheduling. A successful CLI login alone is not project readiness.

A managed Worker with missing tools, unknown/probe errors or failed Codex
CLI/authentication execution preflight remains registered and heartbeating in
`not-ready` state. Configuration synchronization and typed provisioning commands
and plans continue. It cannot request execution assignments until real Codex
execution preflight passes; project-scoped readiness is required separately.
Before capability mutations on an idle Worker, advertised readiness is withdrawn.
Provisioning refreshes observations and rechecks readiness before scheduling resumes.
A failed execution preflight is not retried against unchanged observations; changed
observations or an explicit provisioning request allow a new readiness check.
Task-time Codex infrastructure failures still stop scheduling and preserve recovery
state. Standalone startup retains its existing ownership and failure behavior.

The same bounded `NodeProvisioningCommandExecutor` handlers serve Server commands
and the local Worker CLI, under the same Worker provisioning policy:

```sh
/opt/codex-worker/CodexWorker provision /etc/codex-worker/worker.yml codex-cli detect
/opt/codex-worker/CodexWorker provision /etc/codex-worker/worker.yml codex-cli install --allow-elevation
```

Drain and stop the service before local mutations. Run local operations as the
Worker service account with its service environment;
installation requires the configured elevation allowlist and node-local permission.
The CLI accepts catalog capability IDs and typed actions, never shell text or
arbitrary executable/package paths. Installation and authentication remain separate
operations. Server control transport is not required for a local operation.

Local administration commands are adapters over Worker application services.
Status, configuration show/validate/set, capability list/refresh, and typed
provisioning expose versionable result records with bounded diagnostic codes and
messages. The CLI owns argument parsing and human/JSON formatting; it does not
maintain separate status, configuration, discovery, or provisioning behavior.
Configuration validation/update diagnostics use fixed codes such as
`configuration-invalid`, `configuration-not-found`,
`configuration-setting-rejected`, and `configuration-update-failed`; provisioning
reports reuse the shared typed status and diagnostic enums.
Provisioning authorization is evaluated by the Worker from its node-local
configuration before the shared `NodeProvisioningCommandExecutor` runs. These
contracts provide a reusable boundary for a future secure Server transport
adapter, but this design does not add that transport or remote command execution.

Dashboard behavior checks run without external services:
`node --test --test-isolation=none tests/dashboard/node-provisioning.test.cjs tests/dashboard/server-provisioning-admin.test.cjs`.

## Git/GitHub node setup

The Server dashboard's **Node provisioning** panel uses the existing
management-authenticated command queue for both Server and Worker nodes. It shows
installation/version/update, authentication, Git identity and operation states.
Tool removal retains authentication, configuration and repositories. Git install
also ensures the distribution `openssh-client` package; uninstall leaves it in
place. Git identity (`user.name`/`user.email`) remains node-local configuration,
separate from installation and repository access.

### GitHub CLI authentication

`github-cli:PrepareAuthentication` creates a mode-0700 product directory under
the effective service account's `$HOME/.local/share/codex-provisioning/github`.
An ownership marker distinguishes this directory from unrelated authentication;
preexisting unowned directories and linked paths are rejected. The dashboard
then directs the operator to a terminal on that node **as the service account**:

```sh
env -u GH_TOKEN -u GITHUB_TOKEN \
  GH_CONFIG_DIR="$HOME/.local/share/codex-provisioning/github" \
  gh auth login --hostname github.com --git-protocol ssh --web \
    --skip-ssh-key
```

The browser/device flow runs entirely on the node. Neither device codes nor
login output nor tokens traverse Server reports or the UI. gh uses its default
credential storage; configure a secure node-local credential store for the service
account. Setup refuses existing unrelated gh authentication configuration to avoid
sharing or overwriting operator keyring credentials. Node provisioning requires
Linux; the interactive flow requires a local terminal and a compatible gh. Choose **Check authentication** after completion:
`gh auth status --hostname github.com` verifies live CLI authentication, and
inventory is refreshed. Once prepared, trusted Worker gh calls and local
capability probes select this product directory; existing operator gh config is
untouched. Existing environment credentials can still satisfy CLI authentication
and are not removed or revoked by product logout. Codex's child environment
continues to exclude GitHub authentication.

`github-cli:Logout` invokes gh logout for github.com within the verified product
context, then checks that authentication is no longer satisfied. It preserves
unrelated accounts and refuses unrelated gh authentication configurations. Log out
before uninstalling gh (or reinstall gh to log out). It removes cached
authentication, not the GitHub-side authorization;
revoke that separately when needed. The existing assigned-credential delivery
flow retains its existing authorization boundary and uses the prepared product
context when present. Trusted Git calls pass that context to gh credential helpers.
This setup adds no central secret storage.

### Dedicated repository SSH identity

Git supports `GenerateSshKey`, `InspectSshKey`, `RemoveSshKey`, and
`VerifyRepositoryAccess`. Generation explicitly creates an unencrypted Ed25519
service key at `$HOME/.local/share/codex-provisioning/ssh/github_ed25519`, owned by
the node service account, with mode 0600 in a mode-0700 directory. Private keys
never enter the registry, API, dashboard or reports. Generate/inspect reports
contain only a validated public key and its SHA256 fingerprint. Register the
public key as an appropriately scoped GitHub account or deploy key.

Generation refuses any existing key or public-key file. Inspection/removal
requires the product ownership comment, safe permissions, and a matching public
key derived from the private key. Linked paths are rejected. Replacement is an
explicit remove followed by generate; remove the old GitHub registration
separately. Partial or invalid keypairs left after interruption are preserved for
node-local inspection/reconciliation, never automatically overwritten/deleted.
Existing `~/.ssh` identities and SSH configuration are untouched.

Verification accepts only a bounded `owner/repository` identifier. It runs a
read-only `git ls-remote` against fixed `github.com`, using the dedicated key,
`IdentitiesOnly=yes`, `BatchMode=yes`, `StrictHostKeyChecking=yes` and no SSH
configuration file. Establish a trusted GitHub host key in the service account's
known_hosts through your normal node administration process first. There is no
trust-on-first-use bypass or automatic key scan. A successful read does not
establish push permission or Worker execution readiness. To select this key for
execution, configure the canonical checkout's repository-local `core.sshCommand`
as that service account, with the same SSH options and absolute key path; keep
this configuration outside task worktrees. No remote-supplied filesystem path,
host, URL, executable, package, or shell command is accepted.

Worker authentication mutations require provisioning enabled, `allow_credentials`
and `allow_non_privileged`; denied action keys use
`authentication:<capability>:<action>` in lowercase. Inspection and verification
are read-only actions (still subject to denied-action policy). Server mutations
require `enable_local_provisioning`; package changes additionally require local
elevation permission. Deadlines, cancellation, at-most-once dispatch and
uncertain-operation reconciliation follow the existing provisioning contracts.

## Shared local implementation and host boundaries

`src/Shared` (`CodexProvisioning`) contains the local capability discovery,
allowlisted tool plans, provisioning command executor, GitHub/SSH setup, and
Codex/GitHub device-login handlers used by both hosts. A provisioning operation
executes under the account and paths of the host receiving it. Shared command
reports carry normal completion, bounded failure details, and validated
`LoginInstructions` (verification URL and one-time device code); they never carry
raw authentication process output. The executor refreshes local discovery after
operations, so subsequent inventory reflects authentication changes.

The encrypted `SqliteCredentialStore`, secret inputs, redaction, and
`CredentialDeliveryResponse` also live in `CodexProvisioning`. The store takes
its database path, encryption key, and optional clock from its composing host;
it does not read Server configuration or environment variables. Server wiring
continues to supply `CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY`. Existing database
schemas, encryption format, and credential delivery JSON remain unchanged.
Sharing the implementation does not transfer ownership of the central credential
registry to Workers: Server retains credential metadata, assignment checks,
delivery authorization, and lifecycle administration. Workers retrieve only
authorized assigned secrets; node-private CLI credentials and SSH keys remain
on the node.

`GitHubTokenAuthentication` owns validation and the product-defined token login
command for either host. Its process adapter supplies the local execution
context, enforces the supplied timeout, passes the token through standard input,
and returns only an exit code. The Worker authentication adapter retains
repository selection, credential retrieval, repository readiness checks, and
repository-local Git mutation through `GitRepository`. Token login and device
login are distinct credential inputs; both use the same node-local GitHub
context, rather than introducing another host-specific authentication store.
Standalone Worker calls retain their existing CLI context when managed setup
has not been prepared; Server GitHub administration requires managed setup.
These differences are intentional local-context and ownership boundaries.

Server command persistence/API/CLI and Worker local policy/CLI/reporting remain
host adapters around these shared services. No additional Server-to-Worker
transport or permission bypass is introduced by this sharing. A future typed
remote administration adapter can invoke the same local executor and return its
reports, including device-login progress, without relocating authentication or
private material to Server.
