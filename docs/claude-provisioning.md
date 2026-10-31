# Managed Claude Code provisioning

`claude-cli` is an optional capability in the existing node inventory and provisioning
command API. Management can request Detect, Install, Update (also repair by reinstall),
and CheckAuthentication. Command authorization, operation IDs, deadlines, cancellation,
history and inventory refresh use the same protocol as other managed tools.

The Linux provider downloads Anthropic's fixed HTTPS native installer from
`https://claude.ai/install.sh` and selects the stable channel under the service user's
HOME. It does not require Node.js or npm and never invokes sudo. Enable local
provisioning and `allowNonPrivileged` to permit install/update; explicit denied
actions still win. No package name, URL or command is accepted from the request.
See the [standalone provisioning guide](node-capabilities.md#standalone-claude-code-cli)
for service PATH, operator authentication and Anthropic terms.

Detection and auth checks use `$HOME/.local/bin/claude` under the running Worker
service identity. Configure HOME in the service environment, not an administrator's
interactive shell. Local CLI administration observes the invoking user's environment;
run it under the Worker service account to inspect that account's installation.

User-owned `ANTHROPIC_API_KEY` and supported provider environment/settings remain
node-local, under that service identity. Do not place keys in project YAML or command
requests. Existing Server credential metadata/assignment/delivery authorization remains
the boundary for centrally managed secrets; these provisioning commands neither accept
secret payloads nor import login sessions. Subscription login brokering is not exposed.
Authentication status uses `claude auth status` exit status only. Raw output, account
identifiers and credentials are never persisted in inventory or operation reports.

Installed version, authentication and capability readiness are separate observations.
These probes do not run a Claude execution preflight. Claude's absence or authentication
failure does not block global Codex execution readiness. Inventory is refreshed after
operations, including bounded refresh following interrupted installers; offline/stale
Workers retain the existing management restrictions.
