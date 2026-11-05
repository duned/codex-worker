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

Only SHA-256 verifiers, observer identity, exact HTTPS Server origin, acceptance,
expiry, consumption, and revocation timestamps are stored. Credentials and
authorizations must never be logged or included in diagnostics. Pairing responses
and registration recovery use `Cache-Control: no-store`.

## Dashboard to CLI pairing

This change defines the handoff and implements its Server boundary; dashboard UI,
Worker CLI integration, report ingestion, and report persistence are separate work.

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

## Reporting contracts and future receiver obligations

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
Missing optional information is null, not a free-form explanation. A future
receiver must match payload observer identity to the authenticated route identity,
check revocation on each request, and mark the observer reporting only after an
accepted first report. `awaiting-first-report` currently remains until that
receiver is implemented; no report data is stored by this change.

The credential verifier is exclusively for reporting routes and the read-only
registration recovery route. It must never authorize assignments, configuration,
credential retrieval/delivery, provisioning, maintenance or lifecycle controls.
Reporting routes are deliberately not mapped yet; they must not acknowledge and
discard reports before persistence exists.

Producers must select display fields explicitly. Never serialize execution
history or configuration wholesale. Secrets, environment values, local paths,
prompts, transcripts, raw output and diagnostic text have no protocol fields.
Bounds and field allowlists cannot detect secrets embedded in a legitimate Issue
title or project name: producers remain responsible for safe display values.
