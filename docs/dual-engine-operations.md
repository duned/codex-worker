# Dual-engine operator runbook

Codex is the default engine. CCC selects the optional Claude Code adapter. Both
use Worker-owned isolated worktrees, validation, bounded repair, integration and
reporting. Keep Codex installed and authenticated: global Codex startup readiness
is still required even when a project has CCC Issues. Missing Claude must not
prevent legacy Codex Issues from running. A selected or preserved CCC execution
never falls back to Codex.

## Install under the service identity

Use the supported Linux native provisioning flow described in
[node capabilities](node-capabilities.md#standalone-claude-code-cli) and
[managed provisioning](claude-provisioning.md). The Worker downloads the fixed
Anthropic native installer, selecting stable; it never runs that installer with
sudo. Enable local provisioning and `worker.provisioning.allowNonPrivileged`;
explicit denied actions still apply. Server authorization cannot override local
policy. Managed Detect, Install, Update and CheckAuthentication use the existing
provisioning command API, operation IDs, deadlines and inventory refresh.

Run these commands in the actual service account environment (packaged
administration performs the established identity transition):

```sh
codex-worker provision detect claude-cli
codex-worker provision install claude-cli
codex-worker provision upgrade claude-cli
codex-worker provision check-authentication claude-cli
codex-worker capabilities refresh --json
codex-worker credential status --json
codex-worker diagnostics --json
```

For custom units, set an absolute HOME belonging to the service user and include
that HOME's `.local/bin` in PATH alongside the existing Git, GitHub and Codex
entries. The packaged identity uses HOME `/var/lib/codex-worker`. The adapter
executes `$HOME/.local/bin/claude`, not an administrator's PATH installation.
Protect the HOME and credential files from other users. Restart the service after
environment changes; a successful interactive-shell command does not establish
service readiness. Inspect interrupted installations before explicitly retrying.
Native updates remain vendor-controlled; after any update repeat readiness checks.
See Anthropic's [installation/update documentation](https://code.claude.com/docs/en/setup).

## Credentials and commercial use

Hosted preinstallation/running requires Anthropic's Commercial Terms and hosted
service conditions. Preserve the unmodified binary and built-in authentication
methods; each end user supplies and pays for their own API, subscription or
supported inference-provider credentials. Do not resell or intermediate usage.
Review the current [vendor legal and compliance terms](https://code.claude.com/docs/en/legal-and-compliance)
before deployment.

Operators own authentication under the service identity: use `ANTHROPIC_API_KEY`
or vendor-supported provider environment/settings through a protected service
environment or secret manager, or perform the official `claude auth login` flow
manually as that identity. Startup does not initiate login. Do not put credentials
in YAML, Issue text, command arguments, provisioning requests or logs. This Worker
does not broker subscription login. Centrally managed credential assignment and
delivery remain subject to the existing Server authorization boundary; installing
Claude does not grant access to any centrally stored secret.

## Startup and unattended policy

Installation, authentication and execution readiness are separate checks. Managed
startup observes CLI version, authentication, required help flags and a bounded,
tool-free live response. Only ready CCC is advertised as `agent-provider: ccc`;
`agent-readiness: ccc` carries the diagnostic state and observation time. Server
assignment also requires compatible capabilities, repository authentication,
fresh Worker state and capacity. Offline/stale inventory or cached configuration
does not authorize execution or integration. Standalone execution checks the CLI
boundary when invoking CCC. Keep the normal GitHub/Codex preflight enabled.

The adapter requires version 2.1.259 or later **and** installed help confirming
all required flags; version alone is insufficient. It requires restricted file
tools, safe mode, `dontAsk`, no permission prompts, disabled hooks, strict empty
MCP configuration and no session persistence. Shell, network and subagent tools
are not granted. Git metadata writes are denied. Worker runs YAML validation
commands sequentially and sends failures back to the same engine within the
configured repair bound. Do not resolve incompatibility by adding a permission
bypass or shell tools. See [the adapter boundary](claude-headless-execution.md)
and the vendor [CLI reference](https://code.claude.com/docs/en/cli-reference).

## Select an engine

Use one level-two section in an Issue:

```markdown
## Worker
engine: ccc
model: sonnet
effort: low

## Task
Implement the requested change.
```

CCC accepts `sonnet`, `opus`, `haiku` and efforts `low`, `medium`, `high`.
Omitted CCC effort resolves to `medium`; omitted model is stored as CLI-selected
(the current adapter supplies `sonnet`). Project Codex defaults do not flow into
CCC. Omit metadata or retain `## Codex` for legacy Codex behavior. Unknown keys,
duplicate sections/keys, incompatible settings and conflicting Codex/Worker
sections are rejected. See [the complete syntax](issue-execution-metadata.md).
Metadata cannot change credentials, permissions, commands or paths.

## Troubleshoot and recover

| Observation | Operator action |
| --- | --- |
| CLI absent or installed with required configuration | Verify service HOME, native executable ownership and PATH; detect/refresh after installation. |
| `incompatible` | Inspect installed version/help under the service identity; update through supported provisioning. Keep CCC unavailable if required confinement flags are missing. |
| `unauthenticated` or credential delivery denied | Verify the service user's operator-owned login or authorized credential assignment. Do not copy an administrator's login or widen credential delivery permissions. |
| `service-unavailable` | Check network/provider/model access and billing under the service identity; auth status alone is insufficient. No automatic Codex service retry is added. |
| CCC waits for assignment | Inspect provider advertisement, readiness timestamp, Worker heartbeat, project revision, repository authentication, capacity and lease. Refresh through normal control-plane operations. |
| Invalid Issue metadata | Correct the syntax and explicitly retry through the existing lifecycle. |
| Permission denial, malformed output, auth failure or interrupted process | Treat as infrastructure uncertainty. Preserve the execution/worktree; inspect bounded diagnostics and history before recovery. Do not repeatedly replay implementation. |
| Cancellation, lost lease or uncertain integration | Stop mutation; reconcile with current ownership/generation and verified branch/worktree/base identity. Never force-push or delete useful state to clear the queue. |

History freezes engine/model/effort for recovery; editing an Issue cannot switch
a preserved CCC attempt to Codex. Legacy history defaults to Codex. Use existing
[execution maintenance](execution-maintenance.md),
[managed interrupted retry](managed-interrupted-retry.md) and
[completion reconciliation](managed-completion-reconciliation.md) workflows.
Codex session reconstruction applies only to Codex; CCC has no persisted vendor
session to resume. Retain logs/history with execution IDs and redact secrets;
never attach raw CLI output or authentication files to an Issue.

## Automated evidence and optional live acceptance

The deterministic suite uses local SQLite, fake Git/GitHub boundaries and fake
CLI child processes. It needs no Anthropic subscription/API key or live GitHub.
See [dual-engine validation](dual-engine-validation.md) for coverage and commands.
Fake help/output deliberately implements the adapter contract; it does **not**
prove a released vendor binary supports that contract.

Live checks require an operator-approved disposable project, service identity,
credentials and spending authorization. They are not automated prerequisites:

1. Install/update the native CLI under the service identity and record only
   version, diagnostic state and observation time. Verify actual required flags;
   stop if incompatible.
2. Authenticate using the operator's supported method; run startup diagnostics
   and inspect managed heartbeat/provider readiness. The live readiness probe
   makes a provider request and may incur usage.
3. Submit one default Codex Issue and one explicit CCC Issue in standalone mode;
   repeat with managed assignment. Confirm authoritative validation, engine in
   reporting/history, Worker-owned integration and terminal Server state.
4. In the disposable environment, check revoked credential access, missing CLI,
   cancellation and interrupted-worktree reconciliation. Restore availability
   explicitly; verify secrets are absent from retained diagnostics.
5. Record outcomes and retained execution IDs for parent final integration.
   Never describe fake-based results as a successful real-node campaign.

## Managed Claude native sign-in

The typed `claude-cli Login` action uses the original vendor binary under the
Worker service identity and the existing provisioning authorization, deadline and
at-most-once lifecycle. Enable node-local provisioning, credentials and
non-privileged operations; explicit denied actions still take precedence.

On the verified 2.1.309 flow, the vendor asks for a browser-returned code in CLI
stdin. Managed Login therefore reports `manual-vendor-cli-completion-required`
(`ManualVendorCliCompletionRequired` failure detail), stops its CLI and directs
the operator to run `~/.local/bin/claude auth login` in a secure terminal as the
Worker service user with its configured HOME. Complete browser and paste-code
steps in that same terminal. No browser-code or OAuth URL handoff to management
is provided. Browser-only completion is not claimed. The managed operation
refreshes inventory but does not certify CCC readiness.

After local completion, request `CheckAuthentication` and refresh inventory.
Authentication, installation and live `agent-provider: ccc` readiness remain
separate observations; scheduling still needs fresh managed observations and
lease ownership. See [Claude provisioning](claude-provisioning.md#managed-native-login)
for sanitized progress, service HOME checks and restart/reconnect behavior.
