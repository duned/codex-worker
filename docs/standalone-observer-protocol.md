# Standalone observer reporting v1

An observer is an optional reporting identity for a standalone Worker. It does
not enroll that Worker for managed execution. The Server owns pairing and
revocation; the Worker retains standalone scheduling ownership. Reports are
observations, never authority to execute, reconcile, or control work.

## Identity and authorization

`ObserverProtocol.Mode` is `standalone-observer`. Observer IDs are `obs_` followed
by a canonical lowercase GUID in N format. They occupy separate registry tables
from managed Workers, whose IDs are unprefixed GUIDs. No observer is inserted in
`workers`, `worker_auth_tokens`, assignments, leases, or credential delivery.
Managed schedulers and APIs continue to use their existing registry and verifier.
The observer credential is `obr_` plus 32 cryptographically random bytes encoded
as hex. Pairing authorization is independently generated as `oba_` plus 32 random
bytes encoded as hex. Neither credential type is interchangeable.

Authorization storage contains only SHA-256 verifiers, observer identity, exact
HTTPS Server origin, acceptance, expiry, consumption, and revocation timestamps. Credentials and
authorizations must never be logged or included in diagnostics. Pairing responses
and registration recovery use `Cache-Control: no-store`.

## Dashboard to CLI pairing

The Server implements pairing, report ingestion, storage and inspection APIs.
Dashboard UI and Worker CLI integration are separate work.

1. The standalone CLI retains a new observer identity and supplies its public
   `ObserverPairingRequest` (contractVersion 1, mode, observerId, server) to the
   operator. The origin must exactly match the Server's configured HTTPS
   `AdministrationOrigin`, without a path, trailing slash, query or credentials.
2. An authenticated dashboard action posts that request to
   `/api/v1/observers/pairing/authorize`. Existing administration session/CSRF
   checks apply. The response is a **pending** authorization, valid for five
   minutes, bound to that observer identity and Server origin. No observer exists
   yet. A second live reservation for the identity is rejected with 409.
3. The CLI verifies the handoff's version, identity, mode, origin and expiry, and
   securely retains a freshly generated reporting credential **before** sending
   redemption. It posts the same public request to
   `/api/v1/observers/pairing/redeem`, with the authorization as Bearer and the
   proposed credential in `X-Codex-Observer-Token`. Use verified HTTPS, no redirects,
   and never put either secret in command arguments or URLs.
4. Only a successful atomic redemption consumes the authorization, registers the
   observer, and activates its dedicated reporting credential. The response has
   pairingState **accepted** and state **awaiting-first-report**. Reused, expired
   (including the exact deadline), wrong-origin, wrong-identity and revoked
   authorizations are rejected. Unsuccessful attempts do not consume it.
5. If the response is lost, retain the identity, origin and proposed credential.
   GET `/api/v1/observers/{observerId}/registration` with that reporting Bearer
   credential. Success confirms acceptance without another observer or credential
   issuance. A rejected recovery request does not prove redemption never happened;
   it can also indicate revocation or the wrong Server. Do not generate a new
   identity or silently re-pair. Recover local retained state or ask the operator
   to inspect/revoke the old identity. An expired, unconsumed reservation can be
   explicitly replaced by another dashboard authorization for the same identity.

An authenticated management POST to `/api/v1/observers/{observerId}/revoke`
atomically revokes both pending authorization and reporting credential. Revocation
is terminal for this identity and denies recovery as well as future reports. A
deliberate new pairing uses a fresh identity. Revocation never affects standalone
execution or managed Worker authentication. Portable Server backups erase
observer verifiers and revoke both registered and pending identities; restore
cannot resurrect reporting access.

## Reporting contracts and ingestion

`ObserverStatusSnapshot` and `ObserverExecutionEvent` are v1 allowlisted DTOs.
Snapshots carry a stable snapshot ID, observer ID, Worker UTC observation time,
lifecycle and at most 32 execution summaries. Events carry a stable event ID,
observer ID, Worker UTC observation time and one summary. The summary includes a
stable local execution ID, project display name, repository slug, Issue number
and title, allowlisted execution stage, optional outcome/validation result,
start/completion UTC times and optional nonnegative duration. No Server assignment
or lease is implied. IDs must survive retry; receiver deduplication keys are
`(observerId, eventId)` and execution identity is `(observerId, localExecutionId)`.
Conflicting reuse of an ID must be rejected, not overwrite a different observation.
Worker clocks are observational; receipt time and ordering are receiver concerns.

Use `ObserverProtocol.ReadSnapshot`/`ReadEvent` at the reporting boundary after
limiting the HTTP body to 65,536 bytes. They reject unknown/duplicate properties,
unsupported versions, excessive depth, invalid identities, oversized fields,
duplicate snapshot executions and invalid lifecycle/stage/result/timing values.
Missing optional information is null, not a free-form explanation. POST snapshots
to `/api/v1/observers/{observerId}/snapshots` and events to
`/api/v1/observers/{observerId}/events` using the reporting Bearer credential and
JSON content type. Both known-length and chunked bodies are bounded before
deserialization. Payload identity must match the authenticated route. Authorization
is checked again within the persistence transaction so revocation cannot race a
write. Unknown observers, management credentials and pairing authorizations cannot
submit reports. No unsolicited report creates an observer.

Only the first accepted **snapshot** establishes connectivity and changes
`awaiting-first-report` to `reporting`. Events received before it can be retained,
but do not establish connectivity. The Server persists receipt time independently
from Worker observation time. Exact duplicates return `200 {"state":"duplicate"}`
without changing receipt time or adding history. Accepted new observations return
`200 {"state":"accepted"}`. Conflicting reuse of a retained report identity or
execution identity returns 409. Execution identity binds repository, Issue number
and start time. Reports older than or equal to seven days, more than five minutes
in the future, or behind a retention cutoff return 409 `observer_report_expired`.
These limits are Server admission policy; clocks never determine freshness.
An execution cannot start or complete after its report's Worker observation time.

Snapshot lifecycle advances only with a strictly newer Worker observation time.
Execution projections advance with newer observations; a terminal observation can
also replace a nonterminal observation at the same timestamp. Once terminal, the
execution projection remains fixed. Snapshot execution display is normalized
against those projections so a stale summary cannot undo a terminal outcome.
History preserves the original validated observations, including delayed reports;
inspection presents the current projection separately. Rejected reports roll back
all changes. Duplicate or reordered deliveries never mutate managed work.

Each observer retains the latest snapshot, at most 256 execution projections and
512 snapshot/event history receipts. History is pruned by Server insertion order;
execution projections are pruned by Worker observation time. Pruning durably
advances conservative replay cutoffs: new report timestamps must exceed the
largest pruned report timestamp, and previously unretained executions must start
after the largest pruned execution start time. Thus evicted deliveries are
rejected instead of becoming new work/history. Exact idempotency and identity
collision detection apply within retained history; callers must never reuse IDs
outside that window. Long-delayed reports and old executions that have been
evicted can be rejected even with a new report ID. Retained execution projections
and the latest snapshot's at most 32 summaries retain their identity and terminal
state regardless of the execution start cutoff. Snapshot enrichment is persisted
before projection pruning so eviction cannot reveal an older running summary.

Authenticated administrators can GET `/api/v1/observers/{observerId}` for safe
registration/connectivity metadata, snapshot and execution projections, or
`/api/v1/observers/{observerId}/history?limit=50` for recent receipts (1..512).
Reporting credentials cannot read these administration views. Revocation retains
history for inspection and denies further ingestion. Reports survive restart and
portable backup/restore; restored identities remain revoked. There are no UI,
managed project, assignment, lease, execution request or GitHub state mutations.

The credential verifier is exclusively for reporting routes and the read-only
registration recovery route. It must never authorize assignments, configuration,
credential retrieval/delivery, provisioning, maintenance or lifecycle controls.

Producers must select display fields explicitly. Never serialize execution
history or configuration wholesale. Secrets, environment values, local paths,
prompts, transcripts, raw output and diagnostic text have no protocol fields.
Bounds and field allowlists cannot detect secrets embedded in a legitimate Issue
title or project name: producers remain responsible for safe display values.
