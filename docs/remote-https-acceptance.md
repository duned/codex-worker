# HTTPS security acceptance campaign (23.6)

This campaign validates series 23 without changing product versions or an existing
deployment. It uses the [23.5 proxy recipe](remote-https-deployment.md) verbatim:
Nginx and Server on S, loopback backend 5090, HTTPS on 443, ACME-only 80,
VPN-only management, and loopback Worker APIs on 5080. A and B must be different
machines from S **and from each other**. Containers/processes on S are same-host
checks, not separate-host acceptance.

No live deployment was supplied for this change. **Every live row below is
UNRUN**, including provider execution. Do not infer a pass from the presence of
a test, an old same-VM report, or a skipped command. Enter actual automated
results from the test runner separately. Transport trust authenticates the Server;
Worker API credentials authenticate an identity; delivery assignment authorizes a
secret; provider/repository permissions and actual Codex preflight establish
execution readiness. None substitutes for another.

## Isolated checks

Run from the reviewed checkout with .NET 10 and restored packages:

```sh
dotnet test tests/CodexWorker.Tests/CodexWorker.Tests.csproj --filter 'FullyQualifiedName~WorkerServerHttpTransportTests|FullyQualifiedName~ManagedHttpsSecurityTests|FullyQualifiedName~WorkerEnrollmentTests|FullyQualifiedName~CodexServerTests|FullyQualifiedName~CredentialProvisioningTests|FullyQualifiedName~SharedCredentialServicesTests|FullyQualifiedName~CredentialAdministrationTests|FullyQualifiedName~WorkerCredentialCliTests|FullyQualifiedName~ProvisioningCommandTests|FullyQualifiedName~ManagedConfigurationTests|FullyQualifiedName~GitHubOperationTests|FullyQualifiedName~GitWorktreeTests'
python3 tests/remote-https-proxy-tests.py
```

The transport fixture opens real TLS sockets. Temporary CA/leaf certificates and
per-client `CustomRootTrust` retain built-in chain, expiry and SAN verification;
there is no validation callback or global trust-store edit. Redirect tests cover
301/302/303/307/308, same/cross origin, bootstrap bodies and delivery headers;
the destination sees zero requests. The production Server fixture uses Kestrel
TLS with the same isolated certificates and the actual Worker transport. It
withholds an accepted enrollment/rotation/recovery response, aborts the connection,
recreates Server and Worker clients from durable state, and verifies authenticated
reconciliation without consuming another authorization. It then exercises
heartbeat, validated cached configuration/revision, assignment, lease renewal,
stale-generation rejection, typed detect/report and execution-result reporting.
Capabilities and tool discovery are deterministic test inputs; no live provider,
GitHub or Codex credentials are required. This demonstrates protocol flow, not a
real implementation execution. Existing execution tests own Git/recovery behavior.

The proxy fixture substitutes only addresses/certificate paths in the actual
checked-in recipe and runs a disposable Nginx process. Exit 77 is a skip, never a
pass. It checks public route/method limits, VPN restriction, forged forwarding
headers, no cache/validators, custom headers, log sentinels, TLS trust and bounded
abuse limits. It does not establish public routing, ACME renewal or cloud policy.

## Risk-to-evidence matrix

Names below identify observable regressions, not claims that they ran. Prefixes
are test classes in `tests/CodexWorker.Tests`. Record runner output/checksum for
each automated group and a distinct result for each live row.

| Risk | Isolated automated coverage | Separate-host check / current result |
| --- | --- | --- |
| 23.1 remote plaintext / invalid endpoint | `WorkerServerHttpTransportTests.InvalidPersistedEndpointsFailBeforeSending`, `ServerHttpBackendRemainsLoopbackOnly` | L1 public backend/Worker ports, remote HTTP refusal: UNRUN |
| 23.1 trust, expiry, hostname | `WorkerServerHttpTransportTests.RealTlsEnforcesChainValidityAndHostname` | L2 actual Worker rejection and recovery with bad certificates: UNRUN |
| 23.1 redirects disclose custom headers or replay operations | `WorkerServerHttpTransportTests.RedirectsNeverDeliverCredentialsOrBodiesToDestination` over TLS | L2 disposable redirect collector receives zero requests: UNRUN |
| 23.2 A/B impersonation, body/path mismatch, management separation, legacy fallback | `CodexServerTests.WorkerRoutesRequireExactDurableIdentityAndRetainOwnershipChecks` enumerates normal routes and credentials | L3 repeat from both Workers and VPN/non-VPN: UNRUN |
| 23.2 delivery identity and assignment | `CredentialProvisioningTests.CredentialsPersistEncryptedMetadataAndRestrictRetrievalToAssignedWorker`; `CodexServerTests.CredentialAdministrationApiProjectsSecretFreeMetadataAndKeepsDeliveryAssignedOnly` | L4 assigned, unassigned, reassigned, replaced, revoked delivery: UNRUN |
| 23.3 generic bootstrap replaces identity; scoped/one-use/expired authorization | `WorkerEnrollmentTests.GenericEnrollmentCannotReplaceIdentityAndTargetedAuthorizationIsOneUseAndFresh`, `ExpiredTargetedAuthorizationDoesNotChangeCredentialOrVisibility`, `FailedReassociationPreservesActiveStateAndNeverDisclosesPreviousCredential` | L5 enroll/recover/rotate with wrong scope, expired and consumed authorizations: UNRUN |
| 23.3 response loss/interrupted publication and real token rotation | `ManagedHttpsSecurityTests.AcceptedEnrollmentWithLostTlsResponseRecoversAcrossBothRestarts`; `WorkerEnrollmentTests.UnconfirmedAcknowledgementRetainsPendingAndLastWorkingFiles`, `InterruptedPublicationBlocksRuntimeAndCompletesFromVerifiedPendingState` | L5 retain pending material, restart both services, old-token 401: UNRUN |
| 23.3 independent durable API/delivery revocation | `CodexServerTests.WorkerApiTokenRevocationIsPersistentAndSeparateFromCredentialDeliveryAuthorization`, `WorkerAdministrationApiProjectsPolicyAndSeparatesApiAndDeliveryRevocation` | L4 revoke each separately, restart, recheck: UNRUN |
| 23.4 logs, bounded diagnostics, CLI, metadata, backups | `SharedCredentialServicesTests.SharedRedactionRemovesHeaderAndQuotedSecretsBeforeBounding`; `WorkerServerHttpTransportTests.PollingAndReportingRetainBoundedRedactedErrorsAndCorrelation`; `CredentialAdministrationTests`, `WorkerCredentialCliTests`; `CodexServerTests.BackupRoundTripPreservesControlPlaneMetadataAndExcludesSecrets` | L6 sentinel scan of actual journals/proxy/cloud tooling/backup: UNRUN |
| 23.4 cache and secret-bearing rejection responses | `CodexServerTests.BootstrapAndTransientLoginResponsesProhibitStorageIncludingRejections`, credential API tests; production HTTPS cache sentinel assertion; proxy fixture conditional/repeated delivery | L4/L6 conditional retrieval after replace/revoke, no 304/HIT/stale secret: UNRUN |
| 23.5 proxy route/firewall/forwarding/log policy and renewal | `tests/remote-https-proxy-tests.py` (isolated local proxy only) | L1/L2/L6 actual public/VPN IPv4/IPv6, renewal hooks and failure: UNRUN |
| Lease authority under outage/restart; expiry/owner/generation fencing | `CodexServerTests.LeaseRenewalIsGenerationAndOwnerBoundAndExpiryCreatesSafeRetry`, `WorkerAssignmentRequestsRespectEligibilityCapacityAndRetainOwnershipAfterRestart`; HTTPS generation rejection | L7 block A's egress, observe expiry, reject late renewal/report: UNRUN |
| Uncertain typed/provider/integration mutation must not replay | `ProvisioningCommandTests.DurableDispatchDoesNotReplayAndConflictsRemainLockedUntilAcknowledgement`; `GitHubOperationTests.TransientGraphQlMutationIsClassifiedUncertainAndNeverReplayed`; `CodexServerTests.ExpiredLeaseReconciliationCreatesLinkedAttemptOnlyBeforeIntegrationAndIsIdempotent`; `GitWorktreeTests.CancellationDuringIntegrationRepairPreservesWorkAndNeverIntegrates` | L7 preserve/reconcile uncertain state, no automatic replay: UNRUN |
| Representative managed secure flow and revision agreement | `ManagedHttpsSecurityTests` integrated TLS flow; managed configuration/readiness/checkout and execution suites | L8 small authorized real Issue and post-upgrade repeat: UNRUN |

## Prepare the authorized campaign

The operator must supply explicit approval for S/A/B disposable hosts, DNS, VPN,
firewall administration, certificate issuance and a disposable repository/Issues.
Use protected secret transport; never ask an agent to obtain live secrets or
change the current deployment. If any prerequisite is absent, leave the affected
rows UNRUN with its owner and next action. Network/TLS checks can proceed without
provider access; L8 cannot. No step authorizes destructive Git cleanup or remote
Worker administration.

Pin one release for all nodes and preserve the exact artifacts privately:

```sh
sha256sum SERVER_ARCHIVE WORKER_ARCHIVE INSTALLER PROXY_CONFIG RELOAD_HOOK
/opt/codex-server/CodexServer --version
/opt/codex-worker/CodexWorker --version
uname -srmo
nginx -v
openssl version
```

Run the relevant version commands on each owning node. Record installed binary
hashes too, release URL/tag, reviewed source commit, UTC timestamps, OS, architecture,
.NET runtime if not self-contained, Nginx/Certbot versions and proxy diff against
the pinned recipe. Never infer version from series 23. Record public/private/VPN
addresses, A/AAAA, routing/NAT, trust-root fingerprints and served leaf serial/SAN/
expiry. Certificate private keys, tokens, database and node auth files are not evidence.

Install following [23.5](remote-https-deployment.md), including least-privilege
accounts and protected configuration. Issue distinct one-use bootstrap tokens
with `sudo codex-server worker-token create` redirected to protected files, transfer
through the authorized secret channel, and enroll A/B with the documented installer
`--token-file` flow. Record identity IDs, not token values. Retain owner-only pending
state on failure. Give each Worker its own state and checkout roots. No inbound
Worker port is required. Use synthetic disposable delivery credentials for L4/L6;
provider authentication for L8 remains node-local and separately authorized.

## Live steps and expected results

Run in order, after prerequisites are met. Authenticate HTTP tests using mode-0600
curl config/header/body files from protected sources, with `-o /dev/null` for bodies.
Never use `-k`, `--location`, verbose/trace, literal secrets in arguments, shell
tracing or captured token-issuance output. Inspect responses privately and record
only status, request ID and allowlisted headers. Do not archive raw bodies/logs.

1. **L1 — topology and ports.** On S/A/B run `sudo ss -lntp`; 5090 on S and
   5080 on A/B must bind loopback only. From an independent non-VPN client run
   `nc -vz -w 3 HOST PORT` for S:5090, A:5080, B:5080 and public SSH; all fail.
   Repeat on published IPv6. From A/B use the curl checks in 23.5: unauthenticated
   Worker configuration is 401, dashboard/projects 403 outside VPN, port-80
   application routes 404, forged VPN forwarding headers still 403. On VPN,
   management API is 401 without its own token, succeeds with it, and rejects
   Worker tokens. Check host and cloud firewall effective rules, not just files.
2. **L2 — TLS and renewal.** Run 23.5's `openssl s_client` verification and
   `certbot renew --dry-run --run-deploy-hooks`; record served serial/expiry before
   and after reload and continued A/B heartbeats. In a dedicated disposable test
   proxy, serve unknown-CA, expired, and wrong-SAN certificates in turn. Actual
   Worker enrollment/polling must fail with zero accepted operations; restore the
   valid certificate and verify recovery. A disposable redirect endpoint returning
   each 301/302/303/307/308 must fail bootstrap/delivery, with zero collector
   requests (count only; do not log headers/bodies). Break only the disposable
   ACME webroot, run dry-run: renewal fails while the old valid endpoint continues;
   restore and rerun. Never replace a production certificate or lower trust.
3. **L3 — identity boundaries.** With protected clients using A's API token,
   request B's registration, heartbeat, configuration, assignment, plan/typed
   request/report, execution report and renewal routes from the matrix test.
   Expect 401 and unchanged B state; reverse A/B. A valid own-path token with
   another identity in body gets 400. Own-identity access to another execution/
   command gets ownership conflict (409). Management/delivery/bootstrap/legacy
   shared tokens cannot authenticate normal Worker routes; API tokens cannot
   authenticate management. Read-only own configuration is 200.
4. **L4 — delivery and revocation.** Create and assign a disposable sentinel
   credential using supported credential administration; configure independent
   delivery tokens through VPN-only `PUT /api/v1/workers/ID/credential-access`
   with a protected body. A can retrieve only its assignment with its delivery
   token; B and API/management-token substitutions get rejected. Replace, reassign,
   revoke and repeat conditional retrieval (`If-None-Match`, `If-Modified-Since`):
   no 304/HIT or old secret. Check no-store/no-cache/Expires 0 and absent validators
   on success/errors. Revoke API token: API 401 but assigned delivery still works.
   Recover API; revoke delivery: delivery rejected but heartbeat works. Restart S
   and the affected Worker and repeat. Revoke disposable provider tokens at their
   provider separately; Server revocation cannot erase already delivered copies.
5. **L5 — lifecycle faults.** Drain A, wait for no assignments, stop A, and use
   `worker-token authorize ID rotate` plus installer `--operation rotate` from
   23.5. Verify new API polling works and old protected API token returns 401.
   Try generic bootstrap for that existing identity, wrong-identity/wrong-operation,
   expired and consumed authorizations: reject without replacing active files.
   For response loss, use a disposable fault proxy with backend retries disabled:
   forward exactly one enrollment/rotation request, suppress its response after
   Server acceptance, then close. Verify Server committed and Worker retained
   `.pending`; restart S/A, restore normal routing and retry same operation/material.
   Reconcile through the retained credential, without another bootstrap mutation.
   Repeat recover after revocation. Stop A between pending verification and local
   publication; restart/retry and verify stable identity, complete publication and
   cleared pending file. Do not remove files to make a retry work.
6. **L6 — secrecy and infrastructure.** Follow the sentinel audit in
   [secret protection](secret-protection.md#remote-campaign-checks). Check actual
   application journals, diagnostics, CLI outputs, metadata, exported supported
   backups, Nginx/global/cloud/APM logs and configured cache locations privately.
   Scan using patterns loaded from protected sentinel files, emitting only counts
   and artifact names, never matching lines. Expect zero secrets in these outputs;
   protected issuance, authorized delivery and node-local credential files are
   intentional secret boundaries and are excluded from evidence. Audit effective
   `nginx -T` privately for no cache, body/header capture, inherited real-IP rules
   or retry. Record policy digest and scan counts, never the dump. Exercise bounded
   body/enrollment bursts (413/429) without starving B's renewal/heartbeat.
7. **L7 — interruption and uncertainty.** Use disposable egress rules to isolate
   A while B remains healthy. Observe Server lease expiry, generation fencing and
   absence of offline scheduling from cached configuration. Reconnect: stale
   renewal/report must conflict. For accepted typed commands with lost responses,
   restart before report and confirm no automatic second mutation. For a task at
   Integration, interrupt communication and restart; expect uncertain preserved
   state and no automatic reimplementation, push, provider replay or lease transfer.
   Record IDs/generations/stages and use supported verified reconciliation only
   after inspecting authoritative repository/provider state. Restore egress rules;
   never discard worktrees/commits or force-push. Keep provider/Git mutation fault
   injection automated unless the operator separately authorizes the live action.
8. **L8 — real work and upgrade.** Follow
   [managed-worker-e2e.md](managed-worker-e2e.md) for actual readiness/preflight,
   compatible/incompatible projects, disposable Issues and deterministic configured
   validation. The harness is opt-in; use its documented protected environment
   inputs on the Worker, never token arguments. Run a small file-edit Issue on A,
   then B, recording configuration version/project revision, assignment/lease
   generation, renewal, typed command terminal report and terminal execution result/
   authoritative validation. Each machine owns its own checkout; Worker owns Git/
   GitHub lifecycle. Confirm repository/provider access separately from successful
   enrollment. Drain, upgrade pinned artifacts using supported installers, preserve
   state, reconcile the proxy route map and repeat L1–L8, including old-token and
   stale-generation rejection. Missing provider access leaves real work UNRUN.

## Acceptance evidence template

Copy this template per campaign; every row starts UNRUN. Store only redacted
artifacts with hashes, permissions and retention owner. Mark PASS only after an
observation; distinguish FAIL, UNRUN and SKIP with reasons. A partial campaign is
not overall separate-host acceptance.

```text
Campaign ID / operator / UTC window:
Authorization reference (hosts, DNS, VPN, disposable repository/Issues):
Release / source commit / artifact URLs / archive + installed SHA-256:
S / A / B OS, versions, addresses, identity IDs, independent host evidence:
DNS A/AAAA / NAT / VPN / cloud + host firewall policy digest:
Proxy config + hook SHA-256 / effective policy digest / served cert fingerprint:
Protected credential source references (no values):

Class                       Check/group    Result  Evidence hash  Expected/observed  Reason/next action/owner
isolated automated          matrix groups  UNRUN   -              -                  run commands above
same-host HTTPS/proxy       TLS + Nginx    UNRUN   -              -                  install test prerequisites
separate-host live          L1 ports       UNRUN   -              -                  authorized S/A/B required
separate-host live          L2 TLS/renewal UNRUN   -              -                  disposable certificate faults
separate-host live          L3 identities  UNRUN   -              -                  protected A/B clients
separate-host live          L4 delivery    UNRUN   -              -                  disposable sentinels
separate-host live          L5 recovery    UNRUN   -              -                  controlled response-loss proxy
separate-host live          L6 secrecy     UNRUN   -              -                  private count-only audit
separate-host live          L7 uncertainty UNRUN   -              -                  disposable egress/fault authorization
separate-host live/provider L8 real Issue  UNRUN   -              -                  authorized repo + provider access
post-upgrade live           L1-L8 repeat   UNRUN   -              -                  pinned upgrade artifacts

Execution IDs / project revisions / lease generations / typed command IDs:
Sentinel scan scope / zero-or-nonzero counts / redacted evidence hashes:
Fault injection point / accepted operation count / recovery observation:
Uncertain resources retained / reconciliation authorization and result:
Cleanup: disposable secrets/provider tokens revoked; rules restored; retained
recovery state handed to owner; no current deployment changes:
Reviewer / unresolved failures / overall acceptance (UNRUN until complete):
```
