# E2E Test Plan v1

**Target:** Codex Worker + Codex Server release under test (record exact versions)
**Campaign:** v0.15 hardening checkpoint; execution scenarios require an already-provisioned host

**Document owner:** update during execution; do not put credentials or secret values in this file.

## Purpose and scope

This is a step-by-step manual plan for exercising a small, real deployment across the Server, a clean Worker host, Codex, Git, and GitHub. Run scenarios in order where later steps depend on earlier setup, and record each scenario independently. Use released self-contained artifacts on clean Ubuntu 24.04 x86_64 hosts. Source builds are for release production only; they are not part of the E2E host setup. This plan does not prescribe fixes. For v0.15, the clean-node campaign ends at successful installation/registration and accurate missing-capability diagnosis. A managed unprovisioned node must remain online, heartbeating and reporting accurate `not-ready` capability diagnostics, even with zero projects. Do not manually install tools or copy/login credentials to turn that campaign into an execution test. Run execution scenarios separately on an already-provisioned host; capability inventory and Server-driven tool/authentication provisioning are v0.16 work. See the [review checkpoint](v0.15-release-readiness.md).

The primary target is the safe test repository `duned/codex-worker-test` (or another explicitly approved disposable test repository). Do not use Finance or this Worker repository for destructive execution tests. Use a dedicated GitHub account or test organization with no production credentials or data.

The current release documents managed Worker registration and heartbeat, project snapshots and requirements, Worker capability reporting, provisioning plans subject to Worker-local policy, Server assignment/execution records, isolated Worker Git worktrees, validation and repair, retry/resume, worker/project scheduling limits, drain, and SQLite persistence. Treat behavior beyond those documented contracts—especially crash recovery and transactional boundaries—as an observation to verify. Do not interpret a future hardening expectation in this plan as a guarantee.

## Environment and topology

Prefer separate persistent Server and Worker VMs so network, registration, heartbeats, host bootstrap, and restart behavior are exercised:

```text
Clean Server VM (Ubuntu 24.04 x86_64, systemd, persistent disk)
     │ install released self-contained Codex Server
     │ create one-time Worker registration authorization
     │
     │ HTTPS through a trusted network/TLS endpoint
     ▼
Clean Worker VM (Ubuntu 24.04 x86_64, systemd)
     ├── install released self-contained Codex Worker (no .NET SDK/runtime)
     ├── register/bootstrap against Server; persist Worker identity/token
     ├── diagnose missing execution capabilities; retain registered identity
     ├── v0.15 clean-node boundary (Server-driven provisioning is v0.16)
     └── GitHub ── duned/codex-worker-test
```

Use a TLS-terminated trusted endpoint for Server-to-Worker traffic across hosts. The Server permits plain HTTP only on loopback. Do not expose the Server or the Worker local API directly to an untrusted network. The Worker API defaults to loopback `http://127.0.0.1:5080`; reach it remotely only through an approved SSH tunnel. The Server defaults to `http://127.0.0.1:5090` when run locally; a remote deployment needs the configured HTTPS endpoint.

Record hostnames, OS/image version, architecture, Worker and Server versions, deployment date, Server base URL, Worker API tunnel/local URL, test repository, and relevant non-secret configuration. Never record tokens, credential values, private keys, or environment-file contents.

## Prerequisites

- Approval to create test Issues, branches, labels, and commits in the designated test repository. Confirm it can be safely modified and reset by the campaign operator.
- Ubuntu 24.04 x86_64 Server and Worker VMs. Before each clean-install campaign, use the documented `uninstall-server.sh --purge` and `uninstall-worker.sh --purge` commands to remove installer-owned state on the respective hosts; snapshot or rebuild only if the VM itself is no longer usable.
- On both service VMs: systemd and the installer utilities `curl`, `tar`, and `sha256sum`; the installer must run as root. Network access to GitHub releases and raw GitHub content is required. The release installers install self-contained binaries; **do not install .NET SDK or runtime** on either host. Explicitly record that `dotnet --info` is unavailable on the clean Worker before and after Worker installation.
- Keep project/runtime dependencies absent at the start of the Worker campaign wherever possible. In particular, do not preinstall Git, `gh`, Codex CLI, or project-specific runtimes before baseline capability discovery. The V0.12 Worker provisioning implementation supports a limited set of fixed package mappings; it does not provision .NET, `gh`, or Codex CLI. Do not fill those gaps to pass the v0.15 clean-node checkpoint. Use a separate already-provisioned environment for execution regression coverage.
- Execution tools and `gh`/Codex authentication available to the `codex-worker` service account for execution scenarios. Managed projects are defined only on Server; begin with zero local project YAML and zero checkouts and let assignment materialize them. Standalone execution instead requires local project YAML and a dedicated clean checkout on the configured base branch.
- Distinct Server management credentials and a short-lived, single-use Worker bootstrap authorization created by the Server. Bootstrap generates and stores a persistent Worker identity and authentication token locally; protect the identity directory. If testing Server-managed credential delivery, also configure a distinct Worker credential-delivery token and Server encryption key. Store long-lived service secrets in protected environment files or a secret manager. Enter the one-time bootstrap authorization only at the CLI step below; avoid shell tracing/history and do not retain its output or process arguments. Do not record secret values or copy them into this plan/result record.
- Network access from Worker to Server, GitHub API, configured Git remote, and the Codex service. Synchronized clocks are useful for correlating logs.
- A designated operator able to inspect systemd journals, Server dashboard/API, Worker dashboard/API, Worker filesystem, and GitHub state.

The release installers install the released self-contained app, service account, directories, and systemd unit; they do **not** install .NET, Git, `gh`, Codex CLI, or project dependencies. Installation prerequisites (Ubuntu 24.04 x86_64, root, systemd, curl, tar, sha256sum, outbound HTTPS) are distinct from Worker project requirements. Record any undocumented manual intervention as a finding.

## Publish and deployment references

Use published GitHub Release assets for the campaign. Select one released version and use it for both hosts; record it. Pin both the installer tag and `--version` to the same release to make repeat runs reproducible. These are the actual public installer entry points:

```sh
RELEASE_VERSION=0.15.0 # replace with the exact released version selected for this campaign
curl -fsSL "https://raw.githubusercontent.com/duned/codex-worker/v${RELEASE_VERSION}/packaging/linux/install-server.sh" | sudo bash -s -- --version "$RELEASE_VERSION"
```

On the Worker VM:

```sh
curl -fsSL "https://raw.githubusercontent.com/duned/codex-worker/v${RELEASE_VERSION}/packaging/linux/install-worker.sh" | sudo bash -s -- --version "$RELEASE_VERSION"
```

Run the Server command on the Server VM and the Worker command on the Worker VM, setting `RELEASE_VERSION` in each terminal to the same released version. The release must contain `codex-server-VERSION-linux-x64.tar.gz`, `codex-worker-VERSION-linux-x64.tar.gz`, and `checksums.txt`. Installers verify the archive checksum. Do not use local `dotnet publish` or checkout-based installer modes on either E2E host. Server paths: `/opt/codex-server/current`, `/etc/codex-server/server.env`, `/var/lib/codex-server`. Worker paths: `/opt/codex-worker/CodexWorker`, `/opt/codex-worker/VERSION`, `/etc/codex-worker/worker.yml`, `/etc/codex-worker/worker.env`, `/var/lib/codex-worker`, and `/etc/systemd/system/codex-worker.service`. Confirm ownership/modes and record installed versions. Installer reruns preserve Worker configuration and persistent identity/state.

Server configuration uses `Server__ListenUrl` and `Server__DataDirectory`; set these and `ASPNETCORE_ENVIRONMENT=Production` in `/etc/codex-server/server.env`. A fresh install generates `CODEX_SERVER_MANAGEMENT_TOKEN`; retrieve it with the command documented in the [installation quick path](../README.md#b-create-a-worker-bootstrap-token). Set `CODEX_SERVER_REGISTRATION_TOKEN` and `CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY` only when exercising those features. The default database is `/var/lib/codex-server/codex-server.db`. The packaged service is enabled and started by installation with the generated management credential available. Restart it after changing environment configuration with `sudo systemctl restart codex-server`. See [README Server deployment and API notes](../README.md#build-and-run).

On Worker 01, use the installed managed starter configuration as the basis. It sets managed project ownership, local project directory, Server URL placeholder, and persistent identity path. Set the actual HTTPS Server URL. Bootstrap is an explicit CLI operation: create a single-use token on the Server with `sudo codex-server worker-token create`, then run `sudo -u codex-worker /opt/codex-worker/CodexWorker register --server "$SERVER_BASE_URL" --token-stdin --identity-file /var/lib/codex-worker/.codex-worker/worker-id`. Supply the bootstrap token through standard input from a protected secret source. Avoid shell tracing/history and do not capture terminal output containing the token. The bootstrap command stores the stable Worker ID and generated per-Worker token alongside that identity. This generated token is what later managed heartbeats use; the one-time bootstrap token is consumed and is not the persistent Worker credential. See [release packaging](release-packaging.md) and the CLI usage in the installed application.

On the already-provisioned execution host, configure global `managedProjects` runtime defaults for GitHub, Codex, validation, and Worker execution settings. Assign work and verify that the Worker materializes the dedicated checkout beneath `managedProjects.checkoutDirectory` using the lowercase SHA-256 of the central project ID as the directory name. Managed mode ignores local project YAML. For the clean-node checkpoint, also test a nonempty Server snapshot without local YAML or a checkout: synchronization and remote eligibility checks must succeed without cloning; the first assignment must clone and prepare the project automatically. The Server definition owns identity, project name, repository, default branch, requirements, enabled state, and revision; runtime defaults and credentials remain node-local. The Worker provisioning policy defaults to disabled; enable only the specific safe action being exercised. Keep privileged provisioning disabled unless separately approved. Do not add `CODEX_SERVER_REGISTRATION_TOKEN` to `worker.env` when the bootstrap-generated token exists: the Worker reads its token from the protected identity token file.

The systemd commands below are the documented service controls. `curl` examples intentionally avoid passing secrets inline. For authenticated management API calls, use the dashboard token prompt or an approved secret-aware HTTP client; do not paste token values into terminal transcripts. The endpoints referenced below are documented in the README and implemented by the Server/Worker APIs.

## Evidence and result recording

Create one record per scenario using the template below. Redact authorization headers, environment assignments, URLs containing credentials, and any secret output before storing evidence. Prefer timestamped journal excerpts, dashboard screenshots with tokens hidden, API responses with sensitive fields removed, GitHub Issue/branch links, execution IDs, and checksums or paths for preserved workspaces. Store raw logs in an access-controlled campaign location, not necessarily in this repository; put only safe references here.

For every issue, record whether it is a current-release defect, an expected current limitation, a test-environment problem, or a future hardening opportunity. Record hardening regressions as focused 15.x follow-ups and provisioning features as v0.16 work; unresolved hardening blockers prevent closure. Do not silently change product code as part of executing this plan.

```text
Scenario:
Status: NOT TESTED | PASS | FAIL | PARTIAL
Date/time (UTC):
Environment / versions:
Issue / project / Worker / Execution IDs:
Steps actually run:
Expected result:
Observed result:
Evidence/log references (redacted):
Findings / follow-up Issue:
Operator:
```

## Managed cold-start regression

For releases containing the corrected managed lifecycle, run the [21.6 campaign](managed-worker-e2e.md) after clean installation/registration and approved execution-tool provisioning. Its opt-in harness covers Server-only definitions, eligibility without checkouts, first successful Issue execution, restart/reuse, authoritative revision consumption, incompatible work remaining queued, and standalone local discovery. Record each checkpoint separately; this extends the historical v0.15 registration checkpoint and does not claim a live run has passed.

## Ordered scenarios

### 1. Deploy Codex Server

**Steps**

1. Install Codex Server from the selected release using the release command above. Configure the persistent data directory and secrets in `/etc/codex-server/server.env`; verify ownership/mode without printing file contents. Confirm `/opt/codex-server/current` selects the requested version.
2. The installer enables and starts `codex-server`. After configuring its environment, run `sudo systemctl restart codex-server`. Record the deployed URL and data directory (not secrets).
3. Check process and endpoints:

   ```sh
   sudo systemctl status codex-server
   curl --fail --silent --show-error "$SERVER_BASE_URL/livez"
   curl --fail --silent --show-error "$SERVER_BASE_URL/readyz"
   curl --fail --silent --show-error "$SERVER_BASE_URL/api/version"
   ```

4. Open `$SERVER_BASE_URL/` and verify the dashboard shell loads. Authenticate in the dashboard using the management token prompt; confirm registry views load.
5. Check the configured persistent data directory and SQLite database path. Restart with `sudo systemctl restart codex-server`; repeat status, readiness, dashboard, and authenticated registry checks.
6. Record Server API/dashboard URLs, data/configuration locations, Server version, and any reverse proxy details.

**Expected:** service starts as its service account from the released self-contained binary; `/livez` and `/readyz` respond successfully; dashboard is available; registry state persists across restart; startup logs identify version/runtime/endpoint/data directory without logging credentials.

**Collect:** `systemctl status`, bounded `journalctl -u codex-server` excerpt, endpoint responses, dashboard evidence, persistent directory/database metadata.

**Status:** NOT TESTED

### Repeating a clean-install campaign on the same VMs

After collecting the campaign evidence and completing any required Server-side cleanup, run the matching `uninstall-server.sh --purge` and `uninstall-worker.sh --purge` commands as root. Each command removes the installer-owned unit, binaries/releases, configuration and secrets, persistent state, logs/runtime files, and standard service account/group where safe. Purge is destructive and erases the Worker identity and project checkouts as well as the Server database.

Verify each host is clear before reinstalling: `systemctl show codex-server.service` / `systemctl show codex-worker.service` should report `LoadState=not-found`; the corresponding `/opt`, `/etc`, `/var/lib`, `/var/log`, and `/run` Codex directories should be absent; and the `codex-server` / `codex-worker` accounts and groups should be absent when created by the installer. External tools and packages remain installed and are outside the cleanup guarantee. Reinstall and run the campaign from its first install step.

### 2. Install and bootstrap a clean Worker

**Steps**

1. Start from a clean Ubuntu 24.04 x86_64 VM. Record image, architecture, `command -v dotnet git gh codex` results and installed baseline. Do not preinstall .NET, Git, `gh`, Codex CLI, or test-project dependencies.
2. Install the selected released Worker using the command above. Confirm the requested version in `/opt/codex-worker/VERSION`; inspect `/opt/codex-worker/CodexWorker`, `/etc/codex-worker/worker.yml`, `/etc/codex-worker/worker.env`, `/var/lib/codex-worker`, and the systemd unit ownership/modes. Run `sudo -u codex-worker /opt/codex-worker/CodexWorker --help` to confirm the binary runs. Verify `dotnet --info` still fails because no SDK/runtime was installed; the Worker must run without a preinstalled .NET runtime.
3. Set `server.url` in `/etc/codex-worker/worker.yml` to `$SERVER_BASE_URL`. The installed starter already uses managed project ownership and stores identity under `/var/lib/codex-worker/.codex-worker`. Leave project dependencies absent. Do not start the service yet.
4. On Server, create a fresh bootstrap authorization as the Server service account: `sudo codex-server worker-token create`. Capture the generated token directly into a protected operator terminal/secret manager; it expires after 15 minutes and can be used once.
5. On Worker, bootstrap as the service account so identity/token files have correct ownership. In a protected terminal, run `sudo -u codex-worker /opt/codex-worker/CodexWorker register --server "$SERVER_BASE_URL" --token-stdin --identity-file /var/lib/codex-worker/.codex-worker/worker-id`. Do not enable shell tracing or history for secret-bearing input, capture process arguments, or retain token output. Confirm the identity file, adjacent `.token` and `.server` files exist with service-account ownership and private permissions; do not read or copy their contents. The bootstrap CLI creates a stable identity and per-Worker authentication token; the one-time registration authorization is not used for later heartbeats.
6. Start the service with `sudo systemctl enable --now codex-worker`. Inspect `sudo systemctl status codex-worker` and a bounded `sudo journalctl -u codex-worker`. When Codex is absent, verify `codex-worker.service` remains `active (running)`, the local API is available, and Server heartbeats continue with `not-ready` execution state and missing capability observations. Confirm the identity, credential, and Server URL files remain unchanged without printing their contents. Provision the required tools/authentication through supported operations, then verify refreshed readiness and eligibility without reinstalling or re-registering.
7. Confirm the Server dashboard/API lists this Worker. Record the stable Worker ID from the authorized registry view, not from a secret-bearing configuration dump. Verify no .NET SDK/runtime was installed to run the Worker.

**Negative bootstrap case:** on a separate disposable clean Worker or a fresh identity directory, try an invalid bootstrap token and confirm registration is rejected and no Worker appears in the Server registry. Then, with another fresh token/identity, successfully bootstrap once and retry that same token against a separate fresh identity; the second attempt must be rejected because the authorization is single-use. Record HTTP/journal evidence and confirm no token value is retained. An expired-token case can be run by waiting at least 15 minutes before use instead of the reused-token case. Do not reuse the valid campaign Worker identity for negative cases.

**Expected:** released installer verifies the archive checksum, creates the unprivileged service account, directories, managed starter config, and systemd unit, and installs a self-contained binary. No .NET SDK/runtime or project tool is required to run it. Bootstrap persists identity and a generated Worker credential, registers with the Server using a short-lived one-use authorization, and a bad/expired/reused authorization is rejected. A clean Worker registers successfully and stays active, online and heartbeating with `not-ready` execution state and missing capabilities reported, including with zero projects. Supported provisioning must refresh capabilities/readiness and allow compatible work without reinstalling or re-registering. Missing tools/authentication do not invalidate registration; no installer tool provisioning or credential copying is expected. Verify this lifecycle on Ubuntu 24.04 x86_64 with no preinstalled dotnet, gh, codex or node; do not preinstall tools to make registration or service startup pass.

**Collect:** clean-image and pre/post-install dependency inventory, released artifact version/checksum, installed paths/owners/modes, `dotnet --info` absence, service status/journal, bootstrap/negative-case outcome (redacted), Worker status/capability/configuration-sync responses, Server Worker detail.

**Status:** NOT TESTED

### 3. Worker registration and heartbeat

**Prerequisite for scenarios 3–5 and 7 onward:** use an already-provisioned Worker service account with authenticated Codex preflight available. Scenario 2 covers online node liveness before execution dependencies are provisioned; use supported provisioning to reach readiness before these execution scenarios.

**Steps**

1. In the Server dashboard, inspect Worker 01 immediately after registration. Record its identity, display name, version, platform, capacity, lifecycle/availability, capabilities, and last-seen time.
2. Wait at least two configured heartbeat intervals. Refresh Worker detail and confirm last-seen advances. Compare with local `GET /api/status` and `GET /api/capabilities`.
3. Restart Worker 01 with `sudo systemctl restart codex-worker`. Confirm the same Worker ID returns and heartbeats resume. Observe whether the Server shows a transient offline/starting state.

**Expected:** heartbeats update last-seen and report lifecycle, capacity, and capabilities; restart reuses the persisted identity and updates the same registry record. Do not expect execution resume solely from process restart.

**Collect:** before/after Server Worker details, local status/capability responses, restart timestamps and journal.

**Status:** NOT TESTED

### 4. Project registry and assignment

**Steps**

1. Through the Server dashboard or authenticated management API, create a central project for `duned/codex-worker-test` with its actual default branch and a description identifying this E2E campaign. Start with requirements that the Worker is known to satisfy. Do not put checkout paths or secrets in this definition.
2. Verify the project appears in Server project state/dashboard with an ID and revision. Confirm the corresponding local Worker project YAML exists and has the same project name, a dedicated checkout path, GitHub labels, Codex settings, and configured authoritative validation.
3. Inspect `GET /api/configuration-sync` locally and the Server's Worker configuration/diagnostics view. Confirm desired and applied managed project state is synchronized and the requirement list is visible.
4. Restart Server and Worker separately. Confirm the persisted project definition is still available and the Worker reconciles the desired snapshot.
5. If the Server UI/API exposes project eligibility, record it. Queue no work in this scenario; assignment is tested in scenario 8.

**Expected (current managed ownership):** Server stores central project definitions and revisions; Worker applies a versioned snapshot atomically without matching local project YAML. Missing checkouts are prepared on assignment without rejecting valid Server configuration or eagerly cloning the catalog. Invalid snapshots retain the last valid cache, which never grants offline scheduling authority.

**Collect:** project ID/revision and redacted definition, configuration-sync response, dashboard view, restart/reconciliation timestamps.

**Status:** NOT TESTED

### 5. Requirements and capability discovery

**Steps**

1. With the test project synchronized, compare its declared requirements to capabilities in Worker registration/heartbeat and the Server eligibility/diagnostics view. Include the V0.11–V0.12 capabilities actually implemented in this build: runtime/tool/service capabilities, GitHub and Git readiness, and authenticated AI-agent/Codex readiness as reported.
2. Add one deliberately unsatisfied, harmless requirement to the central project (for example a distinctive tool name that is not installed and is not selected for provisioning). Save and synchronize the new revision.
3. Verify the Server reports the Worker as incompatible or identifies the missing requirement. Queue a harmless test assignment only if needed to demonstrate that an incompatible Worker is not selected; confirm it remains pending/unassigned rather than running.
4. Remove the artificial requirement and verify the next revision restores eligibility.

**Expected (V0.13):** eligibility compares normalized requirement type/name and supported version constraints against current reported capabilities; a missing requirement prevents selection as a ready compatible Worker. Version constraints are exact numeric versions or minimums, not a dependency solver.

**Collect:** project revisions, capability list, eligibility/missing-requirement diagnostics, assignment state if exercised.

**Status:** NOT TESTED

### 6. Provisioning path

**Deferred from the v0.15 clean-node checkpoint:** the following historical limited-policy scenario is optional regression coverage on an already-provisioned host. It is not a workaround for missing Git, `gh`, Codex or authentication. General clean-node provisioning belongs to v0.16.

**Destructive / privileged:** provisioning runs an installer on Worker 01. Use only a safe, reversible dependency and an approved policy. Do not use credentials or privileged actions in the first provisioning exercise.

**Steps**

1. Select one dependency/capability supported by the current Worker provisioning installer and absent from Worker 01. Use a supported project dependency such as the PostgreSQL client on the separate regression host; do not use this scenario to fill the clean-node execution-tool gap. Confirm the installer's planned effects and removeability before execution; do not assume every requirement type has an installer.
2. Configure Worker-local provisioning policy for that non-privileged action. Worker global configuration controls whether provisioning is enabled and whether non-privileged actions are allowed; privileged and credential provisioning have separate controls/allowlists.
3. Through the supported Server management path, create a provisioning plan for Worker 01 with an `ensure` action for that dependency. Inspect the plan before allowing execution. Use current dashboard/API contract; provisioning API paths are `POST/GET /api/v1/provisioning` and `POST /api/v1/provisioning/{planId}/state`.
4. Observe Worker plan pickup and execution. Confirm the plan history reaches a terminal result; check capability refresh on Worker heartbeat and Server Worker detail.
5. Inspect Worker and Server logs, plan history, API/dashboard output for leaked values. No secrets should be part of this plan.

**Expected (V0.13):** plan generation and state are visible centrally; Worker policy decides whether the action is allowed; supported installer runs; Worker verifies the capability afterward and reports refreshed capabilities. Unsupported installers, disabled policy, failed install, or undetected result should yield a useful failed plan, not a false success. The current fixed installer mappings cover Git, Node.js, Docker, and the PostgreSQL client; .NET, `gh`, Codex CLI, and arbitrary package names are unsupported. Missing `gh`, Codex CLI, and their authentication remain outside the v0.15 clean-node acceptance boundary. Server-managed credential provisioning is a separate optional path requiring encryption and delivery-token setup; mark NOT TESTED unless explicitly configured.

**Collect:** redacted plan/action IDs, policy settings (no secrets), state transitions, installer and capability diagnostics, before/after capabilities, redacted journal.

**Status:** NOT TESTED

### 7. GitHub, Git and Codex authentication readiness

**Steps**

1. As the `codex-worker` service account, verify `gh auth status` and read-only access to the test repository. Verify the configured checkout's `origin`, branch, and clean status. Do not print token values or credential-helper contents.
2. Verify intended write readiness against the disposable test repository using an approved disposable branch or the later happy-path Issue. Do not push a test branch to a protected/production repository.
3. Confirm Codex CLI authentication readiness by observing Worker startup preflight and capability/readiness diagnostics. Do not expose authentication files or request verbose secret-bearing output.
4. On a disposable VM snapshot or after recording baseline, separately remove/invalidly configure one credential at a time (GitHub read, GitHub write, Codex auth). Restart/check readiness, restore the secret, and confirm a safe diagnostic without secret disclosure.
5. Confirm repository scoping: Worker can reach the configured `duned/codex-worker-test` checkout/remote and does not access another repository.

**Expected (V0.13):** startup/readiness checks distinguish GitHub/Git and Codex readiness; missing or invalid credentials produce bounded actionable diagnostics and prevent unsafe execution. Codex child processes do not receive GitHub auth or normal Git credential-helper configuration. Record whether each negative case is detected at startup or execution readiness.

**Collect:** redacted `gh auth status`, `git remote -v` with private URL credentials stripped, `git status --short --branch`, capability/readiness views, journal excerpts.

**Status:** NOT TESTED

### 8. First real Issue happy path

**Destructive:** the Worker will create a branch/worktree, edit files, run configured commands, integrate changes, and update GitHub Issue state/labels/comments. Use only the approved disposable test repository.

**Steps**

1. Create a small, deterministic Issue in `duned/codex-worker-test` whose change and authoritative test are clear. Ensure no open GitHub `blocked by` dependencies. Add the configured ready label (`codex-ready` by default, unless the local project YAML sets another name).
2. Confirm Server project and Worker are eligible and capacity is available. Watch the Server execution/assignment view and Worker execution view.
3. Follow the execution through claim/assignment, ExecutionId, Worker-created isolated Git worktree, Codex, sequential validation, integration, reporting, and final GitHub lifecycle. Do not manually edit the checkout during execution.
4. Verify final GitHub state, labels, comment/report, resulting base branch commit, Server execution history/dashboard, local Worker history, and removal of successful execution worktree/branch as specified by configured Git behavior.

**Expected (V0.13):** one eligible Issue is assigned once; a unique ExecutionId and isolated worktree are used; validation commands run sequentially; successful work integrates safely and is reported to GitHub and Server. Managed Server assignment records execution state/history; Worker retains its local execution history. Verify exact final labels/comment/state against current project configuration instead of assuming custom label names.

**Collect:** Issue URL/number, project/Worker/assignment/Execution IDs, timestamps, Server and Worker history, validation summary, commit/branch/worktree observations, final labels/comment/state.

**Status:** NOT TESTED

### 9. Validation failure and preserved workspace

**Destructive:** intentionally submit a test Issue whose configured validation fails. Do not create a failure against a non-test repository.

**Steps**

1. Create a deterministic change for which an authoritative validation command fails, or temporarily use a test-only validation condition that can be safely restored. Observe one or more repair attempts up to the configured bounded limit; do not raise the repair limit just to force repeated attempts.
2. Let the execution exhaust its configured repair attempts or safely fail. Record validation diagnostics and final GitHub outcome.
3. Inspect Worker execution history/dashboard for outcome, validation summaries, recovery state, branch and worktree path. Verify useful failed changes are preserved for recovery; if no useful changes exist, cleanup may occur. Confirm Server execution history reflects reported terminal state.
4. Confirm Worker remains healthy and can process an unrelated harmless Issue (or use the later independent concurrency test) after a safe task failure.

**Expected (V0.13):** validation is authoritative and sequential; repairs are bounded by project configuration. Exhausted validation is a safe task failure, normally reported using the configured failed label (`codex-failed` by default), and does not stop unrelated scheduling. Useful changes are retained as recoverable state; exact cleanup follows whether there are useful changes and repository state.

**Collect:** configured repair limit (non-secret), attempt/Execution IDs, validation summary, branch/worktree metadata and filesystem presence, final GitHub state, Worker health.

**Status:** NOT TESTED

### 10. Retry/resume failed execution

**Destructive:** retry creates a new attempt and may copy preserved changes. Verify target Issue and repository before making it ready again.

**Steps**

1. Set the failed Issue back to the configured ready state using the supported GitHub workflow. For resume behavior, configure that project's `worker.retryMode: resume`; ensure scenario 9 left valid recoverable state. Record the previous ExecutionId first.
2. Let a new attempt run. Verify it receives a new ExecutionId and history explicitly records the previous execution and attempt number.
3. Confirm resume/restart choice in Worker history/dashboard and that verified state is reused only according to the configured mode. Correct the issue/test condition so authoritative validation can pass.
4. On successful completion, verify obsolete recovery resources are cleaned only after the new worktree is created and successful completion is recorded. Confirm both old and new history records remain.

**Expected (V0.13):** each attempt gets a new ExecutionId/worktree; `resume` copies verified file state from prior recoverable execution, while default `restart` starts from current base. Invalid/missing resume state stops safely. Successful resume/clean restart removes prior recovery resources; history remains.

**Collect:** old/new ExecutionIds, attempt and retry relationship, retry mode, recovery state, Git/worktree checks before/after, final GitHub state.

**Status:** NOT TESTED

### 11. Real parallel execution

**Destructive:** two distinct Issues will edit and integrate into the same test repository. Use compatible changes and keep a way to inspect each branch/worktree.

**Steps**

1. Set global Worker `worker.maxParallelTasks: 2` and the test project's local `worker.maxParallelTasks: 2`. Restart/reload as the documented configuration path requires and verify effective capacity in Worker and Server views.
2. Queue at least three distinct eligible Issues. Make two changes independently runnable; make the third a visible harmless marker or delayed test Issue.
3. Observe that two distinct Issues become active concurrently. Record each ExecutionId, branch, worktree, Issue, and timestamps. Confirm the same Issue is not assigned twice.
4. While both slots are occupied, verify the third waits. Allow one execution to complete and check that exactly one slot becomes available and the waiting Issue can start.
5. Allow the second execution to integrate changes near/shared code or deliberately touch separate adjacent lines. Inspect integration results and base history for safety.

**Expected (V0.13):** global and project limits are both enforced (max 8, project limit cannot exceed global); two distinct executions can run concurrently in distinct worktrees/branches; a third waits at capacity; completion releases capacity; repository-scoped setup/integration is serialized and stale-base conflicts fail without overwriting newer base changes. This scenario specifically exercises post-V0.13 scheduler concurrency changes; record any scheduler defect for V0.14.

**Collect:** effective config, queue/assignment timeline, IDs/branches/worktree paths, active counts, slot release timing, base commit history and any conflict diagnostics.

**Status:** NOT TESTED

### 12. Drain and lifecycle control

**Steps**

1. Start a sufficiently long safe test execution. From Worker local API, request `POST /api/worker/drain`; query `GET /api/worker/drain` until `drainComplete` is true or inspect Worker status.
2. While the first execution is active, queue another eligible Issue. Verify no new claim starts while draining and observe the existing execution's documented shutdown/drain behavior.
3. Once no execution is active, cancel drain using `POST /api/worker/drain/cancel`. Confirm state returns to ready/running and scheduling resumes.
4. Exercise project disable/drain and re-enable through the Worker local project lifecycle route `POST /api/projects/{name}/lifecycle`. Confirm disabled/draining project receives no new assignments while existing work follows current lifecycle behavior. The Server central project API documented in V0.13 provides project CRUD and assignment/queue management; it does not document a corresponding project lifecycle control, so record this as a current control-plane limitation if central lifecycle control is needed.

Example local API calls (loopback or an SSH tunnel only):

```sh
curl --fail --silent --show-error -X POST http://127.0.0.1:5080/api/worker/drain
curl --fail --silent --show-error http://127.0.0.1:5080/api/worker/drain
curl --fail --silent --show-error -X POST http://127.0.0.1:5080/api/worker/drain/cancel
```

**Expected (V0.13):** Worker drain stops new claims and allows active work to finish; drain cancellation is allowed only when no execution is active and resumes scheduling. Local project disabled/draining semantics apply to new assignments. Central Server project CRUD and assignment APIs are available; a Server-managed project lifecycle control is not documented in V0.13.

**Collect:** drain/status responses, active IDs, queued Issue timeline, project status before/after, Worker and Server events.

**Status:** NOT TESTED

### 13. Worker disappearance and recovery

**Destructive / interruption:** perform only on the test Worker and during a disposable test execution. A kill can leave incomplete execution state by design.

**Steps**

1. Start a long-running safe test execution and record assignment, Issue, ExecutionId, lease/heartbeat state, branch, and worktree path.
2. Stop or power off Worker 01 during Codex execution. Do not manually alter GitHub or workspace state while observing.
3. Wait beyond the Server's stale heartbeat threshold. Observe Worker availability, lease/execution state, dashboard diagnostics, and GitHub state. Record whether assignment can be safely reissued or remains reserved.
4. Restart Worker 01. Observe stable identity registration, local incomplete history, workspace preservation, configuration sync, and whether execution resumes automatically (README says an execution without terminal state remains incomplete and is not automatically resumed).
5. Determine the documented manual recovery/retry path. Record problems before intervening. If continuing requires repair, preserve VM/workspace evidence first, then use the approved retry flow and record actions.

**Expected (V0.13):** Server detects stale Worker heartbeat/lease according to configured timeout and does not silently report success. Local interrupted execution remains incomplete and is not auto-resumed. Whether/when an assignment can be retried and how GitHub state is reconciled is a key E2E observation; do not assume automatic recovery.

**Collect:** stop/restart timestamps, Server Worker/assignment/execution state, GitHub labels/state, local history, worktree and branch metadata, recovery actions.

**Status:** NOT TESTED

### 14. Server restart and temporary network failure

Run the three cases separately; restore normal service between them.

**A. Server restart while Worker idle**

1. Confirm no active execution. Restart Server with `sudo systemctl restart codex-server`.
2. Observe Worker heartbeats/configuration sync during outage and after Server readiness returns. Verify Server registry/project state persisted and Worker reconnects without duplicate identity.

**B. Server restart while Worker has work**

1. Start a safe long-running test execution. Restart Server during execution.
2. Observe lease renewal, local task progress, reporting behavior, and post-restart reconciliation. Do not assume work continues successfully if authoritative ownership cannot be renewed.

**C. Temporary Worker ↔ Server network loss**

1. With Worker idle, block only the test Worker-to-Server network path briefly, then restore it. Observe heartbeat and managed configuration behavior.
2. Repeat during a test execution if the idle case is safe. Record assignment/lease/report state, duplicate-work behavior, and Worker recovery.

**Expected (V0.13):** Server SQLite project and registry state survives restart. Managed Worker can use its last-known-good applied project snapshot through a temporary outage, but Server-dependent assignment/provisioning is unavailable. Registration/heartbeat and configuration sync recover when connectivity returns. During work, record lease/report behavior and any uncertainty; duplicate work or misleading terminal state is a finding.

**Collect:** outage windows, service journals, Worker sync/heartbeat diagnostics, Server registry and execution history, GitHub state, active worktree details.

**Status:** NOT TESTED

### 15. Integration conflict / concurrent repository change

**Destructive:** modify the test repository base while a Worker execution is in flight. Keep the external change controlled and reversible.

**Steps**

1. Start a safe Issue whose change touches a known test file/area. Record its base commit and ExecutionId.
2. Before Worker integration, use a separate authorized operator checkout to commit and push a controlled change to the same lines or otherwise advance the configured base. Do not force-push.
3. Allow execution to reach integration. Observe whether it integrates against the newer base or fails with a conflict. Do not resolve the conflict automatically.
4. Inspect `main` and verify it contains no partial/corrupt integration. Inspect Worker branch/worktree and history; verify diagnostics identify Issue/ExecutionId/branch and explain the conflict/manual path.
5. Confirm unrelated project execution and Worker scheduling remain safe. Resolve manually only after evidence is captured and according to the test repository owner's process.

**Expected (V0.13):** integration fetches current base and either safely integrates or fails without overwriting newer work. Conflict or uncertain Git state remains inspectable; no force-push or automatic conflict resolution occurs. Worker infrastructure/task outcome and unrelated execution behavior should be recorded as observed.

**Collect:** before/after base commits, external commit reference, ExecutionId/branch/worktree, integration diagnostics, repository status/log, unrelated execution state.

**Status:** NOT TESTED

## Failure injection checkpoints

Execute after the initial happy path and only against the disposable test project. These checkpoints target recovery boundaries. The table states what to observe, not a guaranteed V0.13 automatic recovery contract.

| Interruption point | Injection | Verify after restart/reconnect | Campaign status |
| --- | --- | --- | --- |
| Issue claimed | Stop Worker after claim/assignment is visible | Server lease/assignment expiry, GitHub readiness state, duplicate claim prevention, incomplete local record | NOT TESTED |
| Codex running | Kill Worker process during Codex | child termination, lease expiry, retained worktree, no false success | NOT TESTED |
| Codex finished, before validation | Interrupt between implementation and configured validation | incomplete execution/history and retained workspace; no presumed resume | NOT TESTED |
| Validation passed, before integration | Interrupt at a controlled pause before Git integration | validated changes retained, base unchanged, understandable retry/recovery | NOT TESTED |
| Integration started | Interrupt during fetch/merge/integration on disposable repository | base consistency, Git state certainty, preserved branch/worktree, safe Worker state | NOT TESTED |
| Execution completed, before reporting | Interrupt Worker-to-Server connectivity after local completion but before report | GitHub and Server execution state reconciliation, no duplicate work or false terminal status | NOT TESTED |

Use logs and timestamps to establish the actual checkpoint; do not claim precise boundary coverage if the implementation offers no controllable pause. Mark NOT TESTED and file a hardening finding if a boundary cannot be injected reproducibly.

## Findings and campaign summary

Keep findings in chronological order. Link evidence only from access-controlled, redacted locations. Product defects should normally become separate `V0.14 · E2E Hardening` / `Extra` Issues. Clearly distinguish issues in V0.13 from planned hardening work.

```text
Finding ID:
Scenario/checkpoint:
Date:
Classification: V0.13 defect | expected limitation | environment issue | future hardening
Summary:
Reproduction:
Expected / observed:
Impact:
Evidence (redacted reference):
Follow-up Issue:
Status:
```

| Scenario | Status | Follow-up / notes |
| --- | --- | --- |
| 1. Deploy Codex Server | NOT TESTED | |
| 2. Install and bootstrap a clean Worker | NOT TESTED | |
| 3. Registration and heartbeat | NOT TESTED | |
| 4. Project registry and assignment | NOT TESTED | |
| 5. Requirements and capability discovery | NOT TESTED | |
| 6. Provisioning path | NOT TESTED | |
| 7. Authentication readiness | NOT TESTED | |
| 8. First real Issue happy path | NOT TESTED | |
| 9. Validation failure and preserved workspace | NOT TESTED | |
| 10. Retry/resume | NOT TESTED | |
| 11. Real parallel execution | NOT TESTED | |
| 12. Drain and lifecycle control | NOT TESTED | |
| 13. Worker disappearance and recovery | NOT TESTED | |
| 14. Server restart and network failure | NOT TESTED | |
| 15. Integration conflict | NOT TESTED | |

**Campaign dates:**  
**Operators:**  
**Overall notes for V0.14:**

## Restart during active execution

Run on an already-provisioned test Worker, using an Issue with enough implementation work to observe Codex running. Repeat with two active Issues, during validation, and during integration/rebase. Record execution IDs, feature branches, worktree paths and current commits before restarting.

1. Wait for the execution history stage to show implementation (or the stage under test), then deliberately run `sudo systemctl restart codex-worker`.
2. Inspect `journalctl -u codex-worker` and the service exit status. The old process should log `Worker shutdown requested`, followed by `Execution interrupted by Worker shutdown` for interrupted attempts, and exit successfully. There should be no generic infrastructure failure for this interruption. A genuine concurrent failure should still fail the Worker.
3. Verify the old Codex process and its descendants have exited. The packaged unit sends SIGTERM to the Worker first (`KillMode=mixed`); the Worker gives each isolated Linux child session up to three seconds to stop before killing remaining descendants. systemd retains a final group kill if the service stop timeout expires.
4. Verify useful files, commits and branches remain in each interrupted worktree. History records `Cancelled`, the shutdown reason and `uncertain` recovery state, retaining earlier validation/integration metadata. Do not delete the preserved workspace to make restart succeed.
5. Verify the restarted Worker reaches readiness with fresh scheduler capacity and does not execute a duplicate owned Issue. In standalone mode, the working label is preserved: inspect and reconcile repository/GitHub state before explicitly applying the ready label for a fresh attempt. Shutdown workspaces are uncertain, so they are not automatically resumed or removed by retention cleanup.
6. In managed mode, wait for the existing ownership lease to expire. Shutdown records must not be replayed as terminal failed Server reports on startup: the last confirmed Server stage determines existing lease-expiry reconciliation. Pre-integration expiry can create a fenced, linked fresh attempt; claiming, integration and reporting uncertainty remains for operator review. Confirm stale ownership cannot report or integrate, and the old workspace remains available for inspection.
7. For an integration/rebase interruption, reconcile remote refs, local commits, rebase state and Issue reporting before retrying. Do not assume cancellation means that a push or GitHub mutation did not complete.

Local deterministic regressions cover child graceful termination and forced escalation, one/two controlled execution interruptions, validation and wrapped integration/Issue-mutation cancellation, genuine concurrent failures, preserved real-worktree contents, and a fresh retry after restart. They use local processes and repositories, without systemd or external services; record the deployed systemd exercise separately.
