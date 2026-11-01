# Managed Claude Code provisioning

`claude-cli` is an optional capability in the existing node inventory and provisioning
command API. Management can request Detect, Install, Update (also repair by reinstall),
CheckAuthentication, and Login. Command authorization, operation IDs, deadlines, cancellation,
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
secret payloads nor import login sessions. Managed Login launches the original native
CLI; subscription login brokering and remote browser-code submission are not exposed.
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

## Managed native Login

An authorized operator can submit the existing typed provisioning request with
`CapabilityId: claude-cli` and `Action: Login` for an online Worker. The capability
catalog advertises `login`. In the dashboard, open the Worker's node provisioning
panel and select **Sign in to Claude**. The dialog explains the required local
terminal completion and links to vendor guidance; it does not accept browser codes.
Node-local `enabled`,
`allowCredentials`, and `allowNonPrivileged` must all permit it; denied action
`authentication:claude-cli:login` takes precedence. Login never invokes sudo.
The managed operation uses the existing ID, deadline (5–600 seconds), audit,
at-most-once dispatch, capability concurrency lock and inventory refresh lifecycle.
An expired or interrupted operation is not replayed on reconnect.

The unmodified Linux v2.1.309 binary was checked in an empty temporary HOME during
implementation. With redirected stdin/stdout (no PTY required), `auth login` prints
an HTTPS `claude.com/cai/oauth/authorize` URL, including `code`, `state`,
`code_challenge`, and redirect parameters, followed by `Paste code here`.
This is a browser-returned code flow, **not a pre-issued device code**.
See the [official remote login instructions](https://code.claude.com/docs/en/troubleshoot-install)
and [vendor terms](https://code.claude.com/docs/en/legal-and-compliance).

The managed implementation deliberately uses the manual fallback. After observing
that bounded, allowlisted native challenge, it reports typed transient
`ClaudeLogin` metadata with provider `anthropic`, state
`manual-vendor-cli-completion-required`, and the fixed vendor documentation URL.
The enclosing operation supplies the node, operation ID and deadline. It then
terminates and joins its native CLI, rechecks `claude auth status`, refreshes
inventory, and returns Failed/ProcessFailed with failure code
`ManualVendorCliCompletionRequired`. This means local completion is necessary,
not that browser-only completion succeeded. Even an already authenticated account
does not turn this fallback into a successful Login operation. If the native CLI
finishes without requesting paste-code input, success still requires an observed
trusted challenge, native exit zero and a valid authenticated `auth status` result.

To finish, open a secure terminal **as the Worker service user**, with that user's
configured HOME, and run `~/.local/bin/claude auth login`. Open the URL printed in
that terminal, follow the browser instructions, and paste any returned code into
that **same local CLI**. The stopped managed CLI's challenge cannot be reused.
Never paste the URL or browser code into Server/dashboard/API requests, Issues or
logs. Then request managed `CheckAuthentication` and refresh capabilities. CCC
availability still requires its separate live headless readiness observation.

There is no management code-input endpoint, custom OAuth exchange, token capture,
or credential export. Complete authorization URLs and raw stdout/stderr remain
node-local transient buffers; none enters progress, database, history or backups.
Only the fixed documentation URL leaves the node. Transient progress disappears
on deadline, terminal completion or Server restart; the durable failure carries
only fixed manual-completion instructions. Wrong HOME (compared with the effective
Linux service account's passwd entry), configuration-directory overrides, missing
CLI, absent/malformed challenge, output overflow, process failure, timeout and
cancellation fail closed. Custom service HOME overrides require local manual
completion rather than guessing which account owns credentials.

## Terminal operations and action availability

A Failed Login with `ManualVendorCliCompletionRequired` is terminal. Its retained
instructions do not hold an operation lock. CheckAuthentication, re-detect and retry
remain available according to the advertised actions and node-local policy, even
when Claude health is Error. A terminal command supersedes an older running
capability observation when projecting action availability. Pending or Running
commands (including expired operations awaiting reconciliation) still block another
command for that same node capability. Other advertised capabilities remain usable;
the aggregate node `busy` summary is not a node-wide action lock. An uncertain
HTTP submission retains the dashboard's existing reconciliation fence: use
**Refresh authoritative operation state** before retrying; refresh never resubmits.

The Server store already releases its capability lock on terminal reports. The
dashboard previously applied a broader node-wide gate for any active command or
aggregate busy state. A terminal Claude failure alone in an otherwise ready node
was not sufficient to trigger that gate; unrelated active history or an uncertain
request must also be checked when diagnosing the recorded VM2 report.

The vendor authentication and troubleshooting guidance reviewed for this change
supports pasting a browser-returned code into the original CLI terminal. It does
not clearly authorize collecting or relaying that authorization code through a
third-party management UI. The legal/compliance page requires an unmodified binary
and each user's own authentication. We therefore retain the secure terminal
fallback rather than infer permission for a code relay. This is a conservative
product boundary, not a claim that the vendor explicitly prohibits every relay.
References: [authentication](https://code.claude.com/docs/en/authentication),
[troubleshooting](https://code.claude.com/docs/en/troubleshoot-install), and
[legal/compliance](https://code.claude.com/docs/en/legal-and-compliance).
No installed Claude binary was available in this development checkout; the recorded
2.1.309 inspection above and deterministic fake-process tests inform the fallback.

## VM2 operational verification

This is an optional operator-run acceptance procedure, not an automated test or
evidence that VM2 has passed. Obtain operator approval for service changes and
separate consent for external provider usage before the readiness/execution steps.
Use the account's supported authentication method and applicable
[Anthropic authentication guidance](https://code.claude.com/docs/en/authentication).

The official 2.1.309 binary's unauthenticated challenge was verified in isolation;
VM2 service identity, authentication and readiness still require node-local verification. Do not use an
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
5. In the protected dashboard, open the online Worker's provisioning panel. Detect
   the installation (or install with local policy permission), then select **Sign in
   to Claude**. Confirm the manual-completion guidance remains available after the
   command finishes. Refreshing alone must not claim login success.
6. Open a secure terminal as the Worker service user and start a **new**
   `~/.local/bin/claude auth login`. Follow its provider browser flow and return any
   vendor-required code to that same terminal. The dashboard operation's stopped
   session cannot receive it. Never record the URL, code, account details or tokens.
7. Request **CheckAuthentication** and refresh inventory. Require a completed
   command and a fresh Authentication Satisfied observation. With explicit consent
   for provider usage, observe the Worker's separate live CCC readiness check;
   require a fresh ready observation and `agent-provider: ccc` before queueing work.
   Authentication alone is insufficient; investigate unavailable readiness rather
   than editing capabilities or bypassing assignment/lease checks.
8. Separately obtain consent to queue a disposable real Issue in an approved
   managed project with `## Worker` followed by `engine: ccc`. Confirm assignment,
   isolated worktree execution, configured validation and terminal Server history.
   Use a harmless file-only task and the project's normal integration policy.
   Record only operation/execution IDs, sanitized states, version and timestamps.
   Stop on uncertain state and use existing recovery; do not automatically retry
   provider operations. No real Issue or paid call belongs in automated validation.

## Deterministic integration coverage

`ClaudeProvisioningTests` covers structured authenticated/unauthenticated/error
classification. `ClaudeNativeLoginTests` covers native challenge handling, local
policy, cancellation/deadlines, transient progress, Server restart and late reports
without durable challenge retention. Dashboard contract coverage is in
`node-provisioning-claude-login.test.mjs`. `WorkerV011Tests` includes
`QueuedServerWorkRunsThroughWorkerPipelineAndReportsTerminalState` with `ccc`:
a fake native provider runs Server queue/assignment, Worker execution and validation
repair, and terminal reporting without external usage. These tests complement the
optional live procedure; they do not establish VM2 authentication or readiness.
