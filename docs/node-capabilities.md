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
tools, plus `checkconfiguration` for Git and `checkauthentication`/`logout` for
GitHub CLI and Codex CLI. These indicate registered operations, not permission to mutate a node:
local policy and platform support are checked at execution.

The registry retains the latest Worker report in its existing registration and
heartbeat JSON, along with existing durable plan history. This is a last-seen
snapshot, not authoritative machine configuration; disconnected snapshots and observations older than six minutes are
marked stale. Server facts are never persisted. Detection drains bounded process
output but exports only parsed numeric versions and fixed diagnostic codes.
Inventory input validation rejects arbitrary diagnostic strings and unknown IDs;
credentials, keys, tokens, user identities, and raw command output are not API
metadata.

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

The command API currently supports checking authentication and logout, but does
not expose remote login/device authorization. The dashboard states that limitation
and directs operators to authenticate in the service account environment and
re-detect afterward. It does not collect credentials, fetch secret-delivery
endpoints, or display raw process logs, tokens or private keys. Operation status
and fixed diagnostic codes are the supported sanitized progress/error record.

Dashboard behavior checks run without external services:
`node --test --test-isolation=none tests/dashboard/node-provisioning.test.cjs`.
