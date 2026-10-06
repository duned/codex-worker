# Secure Server ↔ Worker final integration (23)

## Supported topology and review

One Server owns the registry, project revisions, assignment leases and credential
metadata. Multiple Workers initiate outbound HTTPS polling; assignments and typed
orders arrive in responses. Workers need no public listener. The supported
[Nginx deployment](remote-https-deployment.md) terminates TLS on the Server host,
uses a loopback HTTP backend, and restricts administration to the operator VPN.
Worker management remains loopback-only. HTTP between machines is unsupported,
including private networks. Standalone operation remains independent.

The combined review covered these existing boundaries:

- `WorkerServerHttpTransport` is shared by enrollment, polling, reporting and
  delivery. Production uses platform chain/hostname/validity verification, rejects
  redirects, disables cookies, and bounds response size and request duration.
- Bootstrap authorizations are one-use, expiring and operation/identity scoped.
  Registry consumption, credential hash replacement and Worker visibility commit
  in one SQLite transaction. Generic enrollment cannot replace a known identity.
  Normal Worker routes require that identity's active durable API credential;
  administration and assigned-only delivery have separate credentials/checks.
  The legacy shared registration environment variable grants no authority.
- Worker enrollment journals fresh pending material before sending it. A lost
  response is reconciled through that retained credential before attempting
  another bootstrap mutation. Verified publication blocks runtime reads until
  both active credential and association are published. Endpoint changes require
  explicit `associate`; previous-origin API material is never sent to the new
  endpoint. Concurrent local operations retain the existing registration gate.
- Central secrets retain AES-GCM storage and assigned-only delivery; API responses
  prohibit storage, including rejections. Backups exclude usable authentication
  and secrets. Provider authentication stays node-local; redacted diagnostics,
  metadata and cached configuration are separate from secret delivery.
- Current configuration revisions, lease owner/generation checks, pre-integration
  authority checks, node-local typed provisioning policy and uncertain-operation
  reconciliation retain their existing Server/Worker ownership. An outage or
  cached snapshot cannot authorize scheduling or integration independently.

No replacement protocol, store, authorization fallback or scheduling workflow was
needed. Integration changes correct HTTP-to-HTTPS migration guidance, remove a
stale dashboard test expecting shared-token fallback, and extend the actual
HTTPS Server/Worker fixture. That fixture now includes deterministic Server Issue
discovery, eligibility-checked enqueue, assignment/revision agreement, renewal and
stale generation rejection, typed detect/report, execution reporting and encrypted
assigned credential delivery. Association response-loss coverage models a retained
previous-origin association; it does not deploy a second remote Server. Enrollment,
rotation, recovery and association all recreate Server and Worker clients from
durable state after the accepted response is withheld.

## Local validation record

Date: 2026-10-06. Source product version: **0.22.0**, as already defined in
`Directory.Build.props`; no version change or release publication. Environment:
Ubuntu 24.04 x64, Linux 7.0.0-1014-azure, .NET SDK 10.0.112, MSBuild 18.0.11,
.NET/ASP.NET runtime 10.0.12, Python 3.12.3, Bash 5.2.21, Node v24.21.0,
OpenSSL 3.0.13. Nginx is absent.

Topology: one local test host, temporary SQLite/filesystem state, per-client test
CA trust and loopback Kestrel HTTPS sockets. Provider and capability inputs are
fakes; no live GitHub/Codex execution or separate-machine deployment was used.

| Check | Outcome |
| --- | --- |
| `dotnet test CodexWorker.sln --nologo` | Sandbox blocked MSBuild pipe/socket creation. Single-node retry was blocked restoring NuGet (`NU1301`, network permission denied). |
| Same solution run with `-m:1 -p:UseSharedCompilation=false`, permitted escalation | Restored packages; 269 toolbox tests passed. Caught a nullable test-path diagnostic, corrected before the Server/Worker rerun. |
| `dotnet test tests/CodexWorker.Tests/CodexWorker.Tests.csproj --nologo --no-restore -m:1 -p:UseSharedCompilation=false`, permitted escalation | Passed: 1,455 tests, 0 failed/skipped; compilation completed without compiler/analyzer warnings. |
| `bash tests/installer-tests.sh` | Passed installer/uninstaller, helper and Worker lifecycle checks; fixture `visudo` emitted host `/etc/sudo.conf` ownership diagnostics. |
| `bash tests/worker-installer-lifecycle-tests.sh` | Passed. |
| `bash tests/cw-tests.sh` | Passed CLI, execution and cleanup fixtures. |
| `node --test tests/dashboard/*.test.cjs` | All 7 files passed after correcting the obsolete fallback assertion. |
| `python3 tests/remote-https-proxy-tests.py` | Exit 77: skipped because Nginx is unavailable; OpenSSL is installed. |
| `git diff --check` | Passed. |

These are Codex local checks, not the Worker's separately configured authoritative
validation gate. A proxy fixture skip is not a security acceptance pass.

## Operator acceptance and remaining limitations

All separate-machine acceptance remains **UNRUN**. Execute
[the L1–L8 security matrix](remote-https-acceptance.md#live-steps-and-expected-results)
on disposable Server S and distinct Workers A/B, recording pinned artifact hashes,
versions and redacted observations. Specifically verify real IPv4/IPv6 routing and
firewalls; public/private route separation; CA trust, bad certificates, redirects
and renewal failure/recovery; A/B identity and delivery isolation; bootstrap scope,
rotation/revocation and lost-response publication recovery; sentinel-safe proxy,
application and infrastructure outputs/backups; outage lease fencing and uncertain
mutation reconciliation; then a real authorized Issue discovery-to-report flow.
Run the isolated proxy fixture on a host with Nginx as well.

For upgrades, drain Workers, preserve state and use pinned installers. For an
endpoint change, issue `worker-token authorize WORKER_ID associate` at the
destination (also for the same registry gaining HTTPS), then register with
`--operation associate`. Use `rotate` only at the existing endpoint. Preserve
pending material after uncertainty and retry the original endpoint/operation;
verify polling before retiring old access. Revoke an old distinct Server's
credential separately, and configure destination delivery authorization separately.
For an endpoint change on the same registry, do not revoke its newly associated
API credential; the previous token has already been replaced.
Never delete identity/recovery resources to make registration succeed.

Operational trust includes the Server host, proxy, VPN policy, platform CA store,
clock and authorized operators. CA renewal, cloud/firewall policy, inherited
logging/APM/caches and fleet/NAT rate-limit capacity require live checks and ongoing
monitoring. Server revocation cannot erase already delivered provider material;
provider-side revocation remains necessary. Backups require protected external
configuration/key custody and fresh authorization after restore. This review and
same-host fixture evidence do not certify a production or remote deployment.
