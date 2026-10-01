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
execution eligibility. No release lookup is implemented, so update status is
`Unknown`; absence of an update check must never be presented as `Current`.

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
Future install/update/uninstall/login/logout executors must use the same
re-detection boundary after both successful and failed changes. Never infer
success from an operation result alone.

Worker operation state is projected from the latest applicable provisioning
plan, including the current action and a fixed failure diagnostic code. Running
operations and disconnected Workers expose no available actions. The initial
catalog advertises only `refresh`, the common implemented action. Tool mutation
or interactive login/logout actions require registered executors and policy
checks before being advertised; the model supports these operations without
claiming they are currently implemented for every node.

The registry retains the latest Worker report in its existing registration and
heartbeat JSON, along with existing durable plan history. This is a last-seen
snapshot, not authoritative machine configuration; disconnected snapshots and observations older than six minutes are
marked stale. Server facts are never persisted. Detection drains bounded process
output but exports only parsed numeric versions and fixed diagnostic codes.
Inventory input validation rejects arbitrary diagnostic strings and unknown IDs;
credentials, keys, tokens, user identities, and raw command output are not API
metadata.
