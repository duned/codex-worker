# Node provisioning inventory

`GET /api/v1/nodes` (management bearer authentication) returns the Server node
with ID `server` and every registered Worker. Each capability includes a stable
identifier, display metadata, observed state, and currently available actions.
Initial identifiers are `git`, `github-cli`, and `codex-cli`. The shared
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
package versions from the local apt index for Git/GitHub CLI, and compares Codex's
detected version with the official npm stable channel. An unavailable index,
registry or version probe leaves update status `Unknown`, without invalidating
installation or authentication observations. Apt observations reflect the last
index refresh, not a live distribution release lookup.

Worker registration and heartbeats carry an optional `capabilityInventory` in
managed contract v2. Older clients remain accepted and appear with unknown
provisioning facts. The scheduler continues to use its existing project-specific
capability requirements. Node execution readiness is a conservative inventory
summary, not a replacement for project eligibility, capacity, or lease decisions.
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

Worker operation state is projected from the latest applicable provisioning
plan, including the current action and a fixed failure diagnostic code. Running
operations and disconnected Workers expose no available actions. The initial
catalog advertises `refresh`, `install`, `update`, and `uninstall` for all three
tools, plus `checkconfiguration` and SSH key/access actions for Git,
`prepareauthentication`/`checkauthentication`/`logout` for GitHub CLI, and
`checkauthentication`/`logout` for Codex CLI. These indicate registered operations, not permission to mutate a node:
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
when already root). Non-root commands use absolute `/usr/bin/sudo -n` with fixed
arguments. Package mutations share a local executor gate; apt also retains its
native interprocess locks. A failed step stops the sequence. Deadlines/shutdown
cancel the child process tree; a separate bounded refresh records whatever was
actually changed. Terminal reports contain fixed diagnostic codes, never process
output. Inspect refreshed installation/version facts after `ProcessFailed`,
`Cancelled`, `TimedOut` or reconciled `Interrupted`, resolve package locks/network
or local authorization failures, and explicitly retry install/update/uninstall.
The command store's existing acknowledgement/reconciliation rules prevent
automatic replay of an uncertain operation.

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
remains available. Running commands are never automatically replayed, including
when a deadline passes or an acknowledgement is lost. Existing API reconciliation
still requires verified node quiescence.

GitHub login preparation directs operators to complete browser/device authorization
in a terminal on the node as the service account, then check authentication.
Codex authentication is completed in the service account environment and checked
afterward. The dashboard does not collect credentials, fetch secret-delivery
endpoints, or display raw process logs, tokens or private keys. Operation status
and fixed diagnostic codes are the supported sanitized progress/error record.

Dashboard behavior checks run without external services:
`node --test --test-isolation=none tests/dashboard/node-provisioning.test.cjs`.

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
