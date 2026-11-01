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
Authentication status uses the default JSON from `claude auth status`, as documented in
[the official CLI reference](https://code.claude.com/docs/en/cli-reference).
Exit 0 with `loggedIn: true` means Authentication Satisfied; exit 1 with
`loggedIn: false` means Authentication Required. Both are completed read-only
inspections. If present, `authMethod` must agree with that observation. Malformed or
contradictory output, unsupported exits, process failures and missing/unhealthy or
nonmatching refreshed inventory fail the command. Authentication Satisfied alone
does not advertise `agent-provider: ccc`: the separate live headless readiness
probe must still succeed, and managed scheduling still requires fresh observations. Raw output, account
identifiers and credentials are never persisted in inventory or operation reports.

Installed version, authentication and capability readiness are separate observations.
These probes do not run a Claude execution preflight. Claude's absence or authentication
failure does not block global Codex execution readiness. Inventory is refreshed after
operations, including bounded refresh following interrupted installers; offline/stale
Workers retain the existing management restrictions.

## VM2 operational verification

This change was developed without access to VM2 or its installed 2.1.309 binary;
that version's live behavior still requires node-local verification. Do not use an
administrator's interactive HOME as evidence of Worker authentication.

1. Inspect the Worker's service unit locally to confirm its configured User and
   HOME (and any deliberate `CLAUDE_CONFIG_DIR` override). Do not copy environment
   contents, credentials or auth files into reports. Correct a wrong service
   identity/HOME before testing; the control plane cannot infer the intended account.
2. Under that exact service identity and environment, run
   `"$HOME/.local/bin/claude" --version`, then
   `"$HOME/.local/bin/claude" auth status`. Inspect JSON locally, without logging or
   pasting it. Record only version, exit code and the normalized authenticated boolean.
   For 2.1.309, verify the documented JSON/exit pair and configuration directory
   against the service configuration. Do not use `--text` or grep account details.
3. Submit the typed `CheckAuthentication` operation for `claude-cli` and inspect its
   terminal report plus refreshed inventory timestamp. Logged out should now be
   Succeeded/Completed with Authentication Required. Login should yield Satisfied,
   while CCC stays unavailable until its live readiness probe succeeds.
4. For Failed, inspect the sanitized failure code: verify the native binary,
   service-account execute/read permissions, HOME, deadline and fresh inventory.
   VerificationFailed indicates invalid/inconsistent structured status or inventory;
   it must not be worked around by marking authentication satisfied. Denied requires
   reviewing node-local provisioning policy; remote requests cannot override it.
