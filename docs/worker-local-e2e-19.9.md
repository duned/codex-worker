# Worker operational recovery and local E2E campaign (19.9)

Date: 2026-10-03. Environment: Ubuntu 24.04 x64, .NET SDK 10.0.112.

The self-contained Release Linux Worker built from this checkout passed an
isolated local campaign. This is executable-level recovery evidence, **not clean
host installation acceptance**. The production installed service and host
credentials were not changed. Remote Server administration was not added, and
product version metadata was not changed.

The separate [managed cold-start campaign (21.6)](managed-worker-e2e.md) exercises
a real test Server and authenticated Issue execution from zero project state.
The isolated campaign below uses a transport fixture and does not establish that
managed first-execution acceptance.

## Reproducible executable campaign

Build the release apphost, then run:

```sh
dotnet publish src/CodexWorker/CodexWorker.csproj -c Release -r linux-x64 --self-contained true -o /tmp/worker-e2e-artifact
python3 tests/worker-local-e2e.py /tmp/worker-e2e-artifact/CodexWorker
```

The harness requires Python 3, Linux signals and loopback sockets. It runs the
actual Worker apphost with an empty PATH, fresh HOME/Codex/cache directories and
no inherited authentication or Git configuration. All configuration, identity,
logs and execution history are temporary. Package operations remain denied by
local policy. Enrollment uses a local HTTP fixture, never a production Server.
The self-contained apphost starts without a .NET runtime or SDK on PATH; this
does not prove absence of runtime packages from the host filesystem.

| Result | Evidence |
| --- | --- |
| PASS | `status` and `diagnostics`, human and JSON: valid empty standalone configuration, missing tools, not-ready, unknown external service lifecycle. |
| PASS | `config show/validate`, human and JSON; valid `set` requires restart; rejected capacity preserves the original file. |
| PASS | `capabilities list/refresh`, human and JSON; all six typed providers are missing with no execution tools on PATH. |
| PASS | `provision status` and `credential status`, human and JSON, without creating identity state. Invalid identity contents do not prevent provisioning observations or get overwritten. |
| PASS | GitHub CLI, .NET SDK/runtime, Codex and Docker install/upgrade/uninstall safely denied; credential login safely denied. No package manager mutations. |
| PASS | Real registration HTTP request and token stdin against local fixture; success does not claim readiness. HTTP 403 retry retains identity/credential/association files. Status keeps Server acceptance unverified. |
| PASS | Empty standalone Worker starts degraded with control API online, reports not-ready and zero active execution, stops gracefully on SIGTERM, and restarts with retained local state. |

## Regressions fixed

1. `provision status` previously loaded/created durable Worker identity. On a fresh
   node it mutated registration state; invalid or unwritable identity state could
   prevent recovery observations. Status now uses a request-free observation
   context without loading or writing identity. Mutating operations retain their
   existing durable identity behavior. The executable harness checks both absent
   and invalid identity state.
2. `status` rejected an empty standalone project directory although config
   validation and startup permit it while preparing execution dependencies.
   Status now accepts that configuration, retaining missing-tool diagnostics and
   not-ready state. A focused deterministic status regression test covers it.

A new credential lifecycle regression test uses the real shared executor and
administration service with fake process seams. It first caches installed but
unauthenticated state, then verifies login and logout update both credential
readiness and capability inventory immediately, without restart. Existing
provider lifecycle and managed readiness tests exercise installation changes,
interruption, failed preflight, and recovery without external services.

## Local validation and remaining host acceptance

The solution built with zero warnings/errors. NuGet.org restore was unavailable;
restore succeeded using the existing local package cache copied into a temporary
package directory. The initial sandbox denied loopback sockets to both VSTest
and the HTTP fixture; both ran successfully with local socket access enabled.
All 1,036 .NET tests passed, including the new status and credential lifecycle
regressions. Python syntax validation and the final whitespace/diff review passed.
The relocated installer lifecycle, installer/uninstaller, and `cw` suites passed;
these use test fixtures and do not certify a live systemd installation/update.

Before declaring clean-host acceptance, run the following on a disposable
Debian-based VM using an approved release artifact and compatible apt sources:

- Install through `packaging/linux/install-worker.sh`; observe the real service
  with execution dependencies absent, using the service account's environment.
- Enable only the required local provisioning actions; install, upgrade and
  uninstall GitHub CLI, .NET and Docker. Verify component versions, Docker daemon
  access and capability/readiness after each operation. Install/uninstall Codex
  using its product-owned npm provider. Restore restrictive policy afterward.
- Complete GitHub and Codex guided authentication as the service account. Check
  installed-but-unauthenticated, login, logout and failed authentication states;
  verify running readiness and bounded preflight diagnostics. Actual agent
  preflight remains separate from tool installation/login and is not retried
  unchanged automatically.
- Enroll against a test Server, restart/update through the installed lifecycle,
  and confirm identity/credential preservation and continued lack of offline
  managed scheduling authority. Retain interrupted or uncertain resources.

These live package, account, credential and service operations were not run in
this shared restricted environment. Their acceptance result remains **not run**;
no real clean host or authenticated service campaign is claimed by this report.
