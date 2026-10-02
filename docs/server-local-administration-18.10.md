# Server local administration readiness campaign (18.10)

This campaign records deterministic local evidence for the completed Server control-plane administration contracts. It uses temporary SQLite databases, local loopback hosts, fake GitHub services, and dashboard JavaScript harnesses. It does not require GitHub credentials, a real Worker node, or a deployed Server.

## Reproduce

From the repository root, run the automated suite once after changes:

```sh
dotnet test CodexWorker.sln -m:1
node --test tests/dashboard/*.test.cjs
```

The .NET suite exercises the local CLI adapters, authenticated management API routes, persistence/restart behavior, and Server application startup. The dashboard tests load the checked-in scripts with mocked API responses; they do not start a browser or call an external service.

## Coverage and expected behavior

| Area | Test coverage | Expected behavior |
| --- | --- | --- |
| Readiness and configuration | `ServerAdministrationTests` | `status`, `diagnostics`, and config show/validate use local configuration and SQLite only; output contracts are versioned, paths/secrets are redacted, and missing/invalid state has stable failure codes. |
| Projects and Workers | `ServerAdministrationTests`, `CodexServerTests` | Project CRUD and enable/disable use revision checks; Worker enable/drain/disable and independent API/delivery token revocation persist across restart without dropping active leases. |
| Executions | `ServerAdministrationTests`, `CodexServerTests`, `server-execution-admin.test.cjs` | Lists are bounded and filterable; queued work can be canceled; uncertain attempts need explicit evidence and preserve lineage; stale revisions, invalid transitions and unsafe reconciliation are rejected. |
| Provisioning | `ServerProvisioningAdministrationTests`, `CodexServerTests`, `ServerAdministrationTests`, `server-provisioning-admin.test.cjs` | Legacy plans and typed commands remain separate durable histories; typed dispatch is at-most-once, cancellation is queued-only, reconciliation requires verified quiescence, and history reads return at most 100 newest records with a bounded offset. |
| GitHub read and write administration | `ServerGitHubAdministrationTests`, `server-github-admin.test.cjs` | Project-scoped reads and explicit enqueue are tested with fakes. Existing write administration is covered through preview, scope, idempotency, validation, safe failure, and partial-batch cases; no live GitHub mutation is performed. |
| Credential and authentication administration | `CredentialAdministrationTests`, `CodexServerTests`, `server-credential-admin.test.cjs` | CLI/API/dashboard views expose metadata only; create/replace input is stdin/API-body scoped, assignment and revoke persist, delivery authorization stays separate from Worker API authentication, and failure output does not reveal secret input. |
| Backup and restore | `CodexServerTests` | Backup round-trip retains control-plane history and credential/token metadata needed for recovery, scrubs secret payloads and active authorizations, validates typed command rows when present, rejects incompatible state before replacement, and refuses restore while a Server instance owns the database lock. |
| Dashboard administration | all `tests/dashboard/*.test.cjs` | Project, Worker, execution, provisioning, GitHub, and credential views render existing API contracts and expose only the supported operator actions. |

## Issue #140 acceptance map

The local control-plane criteria are covered by existing Server contracts and the campaign above. This map distinguishes what can be inspected or administered locally from the separate evidence required before remote exposure.

| Issue #140 area | Local administration boundary and evidence | Result |
| --- | --- | --- |
| Projects and requirements | The Server registry owns project definitions, revision-checked lifecycle, requirements, and eligibility policy. The CLI and dashboard use Server-owned contracts; project deletion is refused while queued or active work refers to the project. | Locally inspectable and manageable. |
| GitHub Issues and workflow | The Server provides project-scoped Issue reads, relationship inspection, bounded metadata/relationship writes, eligibility refresh, and explicit enqueue. Worker orchestration retains execution claiming, lifecycle labels/comments, closing, and Git integration. | Locally administrable within the documented ownership boundary; live provider authorization depends on the Server service account. |
| Scheduling and assignment | SQLite-backed queue uniqueness, eligibility checks, capacity reservations, leases, generation-fenced reports, and uncertain-attempt reconciliation are exercised through Server tests. | Deterministic local contracts; cluster-wide limits and remote transport security are outside this campaign. |
| Worker registration and lifecycle | Registration, heartbeat observations, capability/readiness reporting, scheduling enable/drain/disable, and independent API/delivery-token revocation are visible through Server administration. | Locally inspectable and manageable; a disabled or revoked Worker’s active lease still follows the documented recovery path. |
| Global state and diagnostics | `status`, `diagnostics`, and `config show|validate` inspect the configured local Server state with bounded, redacted output. Health/readiness and Worker/project eligibility remain distinct signals. | Locally diagnosable; this does not provide a durable unified event log, metrics service, or remote process health probe. |
| Provisioning and authentication | Local operators can inspect typed provisioning history and invoke supported Server-local actions. Server-held provider credentials expose metadata and controlled secret input/delivery; node login state remains node-local and uses typed flows. | Safely inspectable within existing allowlists and authorization boundaries; no arbitrary remote shell or private-key transfer. |
| CLI, HTTP API, and dashboard consistency | The local CLI adapts Server application/registry services, HTTP exposes the management contracts, and dashboard scripts consume those contracts. The .NET and dashboard suites provide the campaign checks listed above. | Design boundary is shared; fresh local evidence is the command result below. |

This acceptance map establishes local operability and recoverable control-plane behavior. It does not establish that a Server exposed to the Internet is ready for deployment; the transport, TLS, network, identity, secret-delivery, remote enrollment, and remote recovery controls in the final paragraph remain prerequisites for that separate phase.

PASS means the deterministic contract tests pass. FAIL means an assertion or command fails. UNAVAILABLE means an environment prerequisite prevented the suite from reaching its assertions. Intentionally unsupported behavior includes live provider/node authorization, remote Server-to-Worker security, remote shell/control, and remote deployment readiness; these are not simulated as passing.

## Backup recovery boundary

Archives contain Workers and scheduling policy, project definitions/revisions, execution metadata/history/queue/leases, legacy plans, typed commands when that table exists, credential metadata, and revoked Worker API-token metadata. Validation requires the current registry schema plus current Worker API-token, bootstrap-token and credential metadata columns. The optional typed command table is validated against its current columns and stored request shape when present, preserving support for earlier format-1 archives that predate typed commands.

Archives deliberately exclude encrypted credential values, Worker credential-delivery authorization, usable Worker API/bootstrap tokens, Server configuration and management/registration tokens, node-local Codex/GitHub login state and SSH private keys, Worker identity/checkouts/workspaces, and external secret-provider contents. After restore, recover configuration from its protected deployment source, restart and inspect `status`/`diagnostics`, create fresh Worker API and credential-delivery authorization, re-enter or fetch credential values and assign them, and restore lost node-local authentication or SSH setup. Review recovered queue/lease state before resuming operations.

The running Server holds `<database-path>.access-lock`; offline restore must acquire it before extraction or replacement. A running Server or another restore therefore causes restore to fail without replacing the target. The stable empty lock file remains in the database directory. Stop the Server service for restore, preserve the target database separately, validate the archive, restore, then restart and verify local readiness.

## Observed local result

| Check | Observed result |
| --- | --- |
| `dotnet test CodexWorker.sln -m:1` | UNAVAILABLE: restore failed with `NU1301`; this environment denied access to `https://api.nuget.org/v3/index.json`, so the suite did not reach compilation or test assertions. |
| `node --test tests/dashboard/*.test.cjs` | PASS: 6 test files, 6 passed, 0 failed. |

The local campaign does not establish clean remote deployment readiness. Before remote exposure, deployment work still needs an approved transport/authentication design, TLS and network policy, least-privilege service identities and filesystem permissions, protected configuration/secret delivery, remote Worker enrollment and revocation procedures, and a separately scoped remote recovery exercise. Live GitHub access also still depends on service-account login and provider-side repository permissions.
