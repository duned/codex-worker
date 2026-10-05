# Managed Worker cold-start and on-demand project campaign (21.6)

Run this regression on a disposable deployment built from a release containing
the managed cold-start changes. Record the exact Server/Worker artifact versions
and checksums; the Issue series does not select or change the product version.
This runbook describes acceptance steps, not a completed live campaign.
See the [lifecycle integration review](managed-project-lifecycle-integration.md)
for ownership boundaries and deterministic regression coverage across the child work.

## Ownership and topology

The Server owns managed project definitions, revisions, requirements, enabled
state, assignments and leases. A managed Worker can register and remain alive
with **zero local project YAML files and zero checkouts**. Without execution
dependencies it remains online with accurate `not-ready` diagnostics; with the
required tools and authenticated Codex preflight it can become healthy and idle
before any project is materialized. Server eligibility uses reported capabilities
and repository access observations, without requiring local repository presence.

Managed runtime defaults and authentication remain node-local. The validated
snapshot cache and checkouts are derived state: first assignment clones the
repository beneath `managedProjects.checkoutDirectory` using the lowercase
SHA-256 of the Server project ID. No project YAML is generated. Restart verifies
and reuses the checkout; a cached snapshot never grants offline scheduling
authority. Standalone ownership discovers projects from local YAML and requires
each execution checkout to be prepared locally. An empty standalone catalog can
remain online while being configured; managed cached projects do not populate it.

Use the supported Ubuntu 24.04 x86_64 Server VM and separate Worker VM topology
in [the E2E plan](e2e-test-plan-v1.md#environment-and-topology). Connect to the
Server through HTTPS with trusted TLS. Loopback HTTP is suitable when testing
both processes on one isolated host. Follow [Linux installation](linux-installation.md)
for released self-contained artifacts, registration and service-account tools.

## Prepare the disposable deployment

1. Install the Server and a clean/rebuilt managed Worker. Before execution-tool
   provisioning, record that the Worker has no project YAML/checkouts and remains
   active, registered and heartbeating, with missing capabilities reported. Keep
   the installed starter configuration in managed ownership. Never create a
   project YAML or clone a project to make managed readiness pass.
2. Provision execution dependencies using approved local administration and
   authenticate GitHub/Codex as the Worker service account. Record inventory and
   the bounded actual Codex preflight. Installation/login alone is insufficient.
   Give the test Server its own GitHub API access: enqueue validates the Issue's
   eligibility on the Server. Do not copy private authentication state to Server.
3. In the Server dashboard create two enabled projects for two approved disposable
   repositories. Use a compatible project's actual default branch and requirements.
   Give the other project an unmet requirement, for example a runtime
   `node` version `>=999.0`. Confirm the requirement is absent from the Worker.
   Create two small, distinct compatible test Issues (first execution and restart)
   and one incompatible-project Issue. Each must meet configured ready-label and
   dependency eligibility. Keep implementation bounded, such as adding one text
   file; provide a short deterministic validation command appropriate to that
   repository. Approval to mutate those repositories/Issues is a campaign prerequisite.
4. Use a dedicated Server with no queued work and all other Workers drained or
   disabled. Stop the installed Worker service for the foreground harness. Run
   as `codex-worker` with its service-equivalent PATH, Git identity/authentication,
   `CODEX_HOME` and `GH_CONFIG_DIR`. Do not run a second Worker against the same
   checkout. The harness creates a new identity and fresh node-local state in a
   nonexistent directory; record this foreground identity separately from the
   installed service's identity. It exercises real Server/Worker/GitHub/Codex
   execution without mutating the installed configuration.
5. Create a short-lived single-use bootstrap authorization on Server. Supply
   `MANAGED_E2E_BOOTSTRAP_TOKEN` and `MANAGED_E2E_MANAGEMENT_TOKEN` to the harness
   from a protected secret source. Do not put secrets in CLI arguments, YAML,
   transcripts or shell tracing. They are removed from the Worker's environment;
   bootstrap is delivered through standard input. Configure the Server URL and
   test IDs through the arguments below.

Prepare a node-local YAML fragment containing only `managedProjects` runtime
defaults, without the enclosing key or `checkoutDirectory`. For example:

```yaml
codex:
  instructionsFile: AGENTS.md
  timeoutMinutes: 5
validation:
  timeoutSeconds: 60
  maxFixAttempts: 0
  commands:
    - test -f cold-start-proof.txt
worker:
  maxParallelTasks: 1
```

Adapt the validation to both compatible Issues and ensure repository instructions
exist. Git/GitHub defaults must match the test repository's intended integration
and labels; override them in the fragment when necessary. This is trusted Worker
configuration, not remotely supplied shell text. The harness isolates HOME/cache
but retains explicitly selected service-account authentication homes and the
service environment. Set an absolute `GIT_CONFIG_GLOBAL` to the approved Git
configuration if Git identity or credentials require it. No credentials belong
inside the fresh campaign directory's repository files.

## Run and retain evidence

From the Worker VM, with Python 3 and the environment above:

```sh
python3 tests/managed-worker-e2e.py \
  --worker /opt/codex-worker/CodexWorker \
  --server "$SERVER_BASE_URL" \
  --state /var/lib/codex-worker/managed-cold-start-campaign \
  --runtime /path/to/approved-runtime.yml \
  --project "$COMPATIBLE_PROJECT_ID" \
  --incompatible-project "$INCOMPATIBLE_PROJECT_ID" \
  --issue "$FIRST_ISSUE_NUMBER" \
  --restart-issue "$RESTART_ISSUE_NUMBER" \
  --incompatible-issue "$INCOMPATIBLE_ISSUE_NUMBER" \
  --timeout 900
```

`--state` must not exist and its parent must be writable by the service account.
The harness polls with bounded HTTP/process/scenario deadlines, fails on early
Worker exit, and preserves configuration, identity, cache, history, logs and
recovery resources on success or failure. It performs no Git/GitHub lifecycle
operations itself; the Worker executes, validates, integrates and reports the
Issues. It changes the compatible Server project's description using the current
revision, then waits for that revision to be reported before the second execution.

| Checkpoint | Required evidence |
| --- | --- |
| Cold registration/start | Fresh identity; no local YAML or checkout; Worker remains alive; two different heartbeat timestamps; capabilities and synchronized configuration. |
| Server-only catalog | Compatible project eligible and `not-materialized`; incompatible project ineligible with missing requirements and `blocked`; checkout root still absent. |
| First assignment | Real compatible Issue reaches `Completed` on this Worker; derived checkout exists and project observation reaches `ready`; execution ID printed. |
| Restart and update | Graceful SIGTERM/exit, new heartbeat, unchanged identity; incremented Server revision reported; second Issue completes using the same checkout and its `.git` marker survives. |
| Capability rejection | Incompatible request stays `Queued` and unassigned through both executions; its checkout remains absent. |
| Standalone boundary | After managed execution stops, standalone status sees zero configured projects despite the retained managed cache; adding local YAML produces one configured project. This checks discovery; standalone execution remains covered by its normal pipeline tests. |

Record PASS/FAIL per checkpoint using the [E2E evidence template](e2e-test-plan-v1.md#evidence-and-result-recording),
including project/Worker/execution IDs, revision numbers and redacted Server
diagnostics. Do not publish raw Worker logs; inspect them locally and redact any
evidence. A timeout/failure is not acceptance. Do not rerun implementation or
delete uncertain work to obtain a green result. Inspect lease/integration and
recovery state first. The incompatible request is deliberately retained queued;
cancel it through Server administration after evidence capture. Restore the test
project description through a revision-checked update if desired, disable the
foreground Worker identity, and restart the installed service only after the
campaign Worker has stopped. Do not purge retained recovery resources until safe
ownership and integration reconciliation are complete.

## Deterministic local regression checks

```sh
dotnet test tests/CodexWorker.Tests/CodexWorker.Tests.csproj --filter 'FullyQualifiedName~ManagedCheckoutTests|FullyQualifiedName~ManagedWorkerReadinessTests|FullyQualifiedName~MultiProjectConfigurationTests'
python3 -m py_compile tests/managed-worker-e2e.py
```

The combined registry/snapshot/assignment/checkout regression uses temporary
SQLite and local Git repositories. Existing readiness tests exercise WorkerHost
with deterministic Server/process seams. These tests need no live authentication;
they complement the executable live campaign and do not certify VM installation
or first successful authenticated execution.
