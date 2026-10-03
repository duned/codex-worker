# Worker administration integration review (19.10)

Review date: 2026-10-03. Scope: local Worker administration, shared provisioning
and credentials, registration, readiness and their existing execution/recovery
boundaries. This closes the source integration review following the
[19.1 audit](worker-administration-runtime-audit-19.1.md) and
[19.9 isolated campaign](worker-local-e2e-19.9.md). Roadmap numbering does not
change the product version.

## Architecture and observable contracts

| Area | Review conclusion and evidence |
| --- | --- |
| CLI conventions | `WorkerAdministrationCli`, `WorkerRegistrationCli`, `ProvisioningCli` and `WorkerCredentialCli` adapt application services or typed shared handlers. Help bypasses configuration and host startup. Local administration uses versioned JSON, bounded/redacted errors, human diagnostics, cancellation and stable exit codes. `WorkerAdministrationCliTests`, `WorkerRegistrationCliTests`, `ProvisioningCliTests` and `WorkerCredentialCliTests` cover adapter contracts. |
| Shared implementation | Server and Worker use `CodexProvisioning`'s `CapabilityCatalog`, `NodeCapabilityDiscovery`, `NodeProvisioningCommandExecutor`, fixed tool providers and node login handlers. `NodeCredentialAdministration` composes these for local Worker credential presentation. Centrally stored/assigned credentials remain Server-owned; `GitHubTokenAuthentication` and the shared delivery contract provide the common token authentication boundary. `SharedCredentialServicesTests` exercises storage/assignment/delivery/authentication composition and revocation without leaking payloads. Central encrypted storage and node-private CLI state intentionally have distinct ownership. |
| Provider dependencies | Authentication requirements are typed catalog metadata. Git, .NET SDK/runtime and Docker have no CLI authentication dependency. Git requires identity configuration; Docker requires daemon access as the effective service account. GitHub/Codex require their respective local CLI login. `ExpandedToolProvisioningTests` verifies component versions, provided tools and configuration independently of authentication. |
| State transitions | Installation, update availability, authentication, configuration, operation state and capability readiness remain separate. Candidate lookup failure does not invalidate installed/authenticated state. `WorkerAdministrationIntegrationTests` compares capability, provisioning and credential views through missing, installed with dependencies missing, healthy, dependency loss, removal and restoration for all six providers. Existing lifecycle suites cover repeats, partial failure, timeout, cancellation, service-path verification and reinstall. |
| Execution readiness and liveness | Capability readiness is a prerequisite, not proof of Codex execution or repository authorization. `ManagedCodexReadiness` runs the real provider preflight and does not retry unchanged failures. Repository API/write and Git remote checks remain project-scoped. `ManagedWorkerReadinessTests` and `AuthenticationReadinessTests` cover degraded online control loops, dependency loss, failed preflight and restoration. Local `status` explicitly reports observations only; it does not inspect the running service or claim authenticated execution readiness. |
| Identity and registration | Stable identity, Worker/API credentials, Server association, Server acceptance and execution readiness are distinct. Read-only observation does not create identity. Endpoints are validated before authenticated requests, enrollment failures retain recovery material, and local status leaves Server acceptance unverified. `WorkerRegistrationTests`, `WorkerRegistrationCliTests`, `WorkerStatusTests` and the isolated executable fixture exercise these contracts. Existing versioned registration/heartbeat and typed request/report contracts can be carried by later transport without granting new authority. |
| Policy and recovery | Node-local deny/allow/elevation policy gates typed operations; remote callers cannot provide shell text, packages or executable paths. Host-delivered mutations wait for idle/non-draining execution state and revoke readiness before revalidation. Cancellation/timeout preserves terminal outcomes and performs bounded redetection. Uncertain integration and infrastructure failures retain state and stop scheduling; safe task failures release capacity. Existing `WorkerV011Tests`, `GitWorktreeTests`, `ProvisioningCommandTests` and history/recovery tests preserve these boundaries. |

## Integration gaps corrected

- Human provisioning and credential failures now include the shared safe failure
  description, including a process exit code when supplied. JSON retains the
  same typed detail. No raw provider output is added. Regression coverage checks
  package unavailability, elevation denial and process failure presentation.
- Successful GitHub preparation now directs operators to the supported typed
  device-login command as the Worker service account. It retains the separate
  node authentication check and repository authorization caveat.
- Provisioning presentation no longer starts uncancelled discovery after request
  cancellation. The executor retains responsibility for bounded reconciliation;
  the terminal report is returned with no optional capability snapshot on
  shutdown. A focused cancellation regression covers this behavior.

## Validation and next transport boundary

Local validation used .NET SDK 10.0.112. The baseline complete solution suite
passed all 1,036 tests. The updated complete suite passed all 1,048 tests with
zero compiler/analyzer warnings, including cross-provider and presentation/
cancellation regressions. The initial sandbox denied MSBuild IPC; a single-node
attempt then found missing restore assets. Restore and test execution succeeded
with the permitted external execution context. These are Codex self-validation
results, not the Worker's separately configured authoritative checks.

The four relevant dashboard suites, Worker installer lifecycle suite and `cw`
suite passed. The isolated Python CLI campaign passed against the framework-
dependent Debug apphost built from this checkout, using temporary files,
stubbed/missing tools and a local enrollment fixture. This supplements the
historical self-contained Release campaign; it does not certify clean-host
installation, systemd activation or live authenticated provider access.

Later secure Server ↔ Worker administration should preserve these boundaries:

- Authenticate and authorize the Worker, delivery and administration requests
  independently. A credential reference, local identity or cached configuration
  must not grant scheduling or integration authority.
- Carry the existing bounded typed requests/results and safe failure details;
  keep login challenges transient, tokens/private keys node-local where required,
  and command deadline/reconciliation semantics explicit. Do not replay uncertain
  mutations after a lost response.
- Keep node-local policy authoritative and execution leases fenced. Server owns
  registry, managed revisions, assignments and central credential delivery;
  Worker owns execution, local configuration, node authentication and recovery.
- A separate local CLI process does not share the running host's active-work
  gate. Quiesce the service before mutating installed tools or login state. Remote
  administration must enter through host coordination rather than spawning a
  parallel mutation path. Transport security does not establish provider or
  repository permissions.

The disposable-host package/login/service acceptance items in the 19.9 report
remain unrun here. No new transport, credential store, scheduling mechanism,
automatic recovery replay or product version is introduced by this review.

## Parent integration review (Issue #176)

The final parent review checked the completed local administration adapters,
configuration and identity observation, shared provisioning/credential handlers,
provider dependency metadata, host readiness gates and execution/recovery
boundaries against the conclusions above. Shared core behavior remains in
`CodexProvisioning`; Server retains registry and central credential ownership,
and Worker retains node-local policy, authentication and execution ownership.
Local CLI observations do not grant execution or managed scheduling authority.

One further readiness inconsistency was corrected: an update candidate lookup
that throws `UnauthorizedAccessException` previously marked the entire capability
as unhealthy, even after installation and authentication/configuration probes
succeeded. The shared discovery now reports update availability as unknown and
preserves those successful observations, as it already does for other expected
candidate lookup failures. Caller cancellation still propagates. Six focused
cross-provider regressions verify capability inventory, provisioning status and
credential readiness agree for Git, GitHub CLI, Codex CLI, .NET SDK/runtime and
Docker. This does not weaken installation, login, configuration, repository access
or Codex execution preflight requirements.

Parent self-validation on .NET SDK 10.0.112:

- The complete solution compiled without compiler/analyzer warnings and passed
  all 1,054 tests, with no skips, including the six new regression cases.
- All six dashboard suites, installer/uninstaller checks, Worker installer
  lifecycle checks and the `cw` suite passed.
- The isolated Python campaign passed against this checkout's framework-dependent
  Debug apphost, including enrollment, degraded control API liveness, zero active
  execution, graceful shutdown and restart with retained local state.
- Initial restore could not reach NuGet.org; restore succeeded using a temporary
  copy of the existing local package cache. Initial VSTest and Python fixture
  runs could not open local sockets in the sandbox; both completed successfully
  with socket access enabled. Whitespace/diff review passed.

These are local self-validation results. The Worker will run its configured
authoritative checks separately. Live package installation, real provider login,
and clean-host systemd acceptance remain unrun as described in the 19.9 report;
the isolated campaign does not certify those operations.
