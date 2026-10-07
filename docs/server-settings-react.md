> Current routing: all canonical Server dashboard screens use the shared React app.
> Preview URLs redirect to canonical routes; the separate PoC bundle and legacy
> Server frontend are removed. See [final cutover evidence](server-dashboard-cutover.md).
> Migration-stage descriptions and earlier validation below are historical evidence.

# React Settings, credential administration and Server preparation parity (24.12.8)

The shared production React package now owns `/dashboard-preview/settings`,
`/dashboard-preview/settings/{credentialId}` and the `?node=server` context.
Canonical `/settings` routes remain legacy-owned until the series cutover. No
backend contract, local Worker dashboard or product version changes are included.
Settings is loaded as a separate chunk from the same application and embedded
asset pipeline, rather than introducing another frontend package.

## Parity inventory

| Flow | React implementation and authority |
| --- | --- |
| Server GitHub connection | Uses the same `serverGitHubReadiness` projection as Home. Connected requires current, healthy installed CLI/authentication evidence, with no retained active operation or failed latest authentication check. Submission success alone cannot complete connection. Repository read/Issue write permission, Worker authentication and push permission remain separate. |
| Guided preparation | Explicit preparation and separate explicit device login; private product-managed service-account context and refusal of unrelated operator authentication are explained. Existing advertised actions and Server local provisioning/elevation policy govern controls. No operation starts on render, navigation, reload or provider return. |
| Installation/elevation | Installation has a separate Untitled UI checkbox and action confirmation. A fresh policy/action read precedes submission; the node still enforces local authorization. Advanced elevated and destructive typed actions use the same dialogs. |
| Temporary device challenge | The existing authenticated connection endpoint recovers a running login. Only the fixed GitHub verification origin, supported Server Login and an unexpired deadline produce instructions. A deadline timer clears the transient challenge even without another poll. Unknown/terminal/expired instructions are discarded. |
| Active-operation recovery | Dedicated node/connection reads retain active operations ahead of bounded terminal history. Queued cancellation and expired-running reconciliation remain available, with explicit quiescence confirmation. Running operations must stop at their deadline; the browser does not cancel them or replay writes. |
| Response loss | The shared runtime holds the Server node mutation fence across polling/navigation. Explicit Refresh authoritative state reads inventory, node commands and connection before clearing uncertainty. A recovered active command still prevents new submissions. |
| Server capabilities | Promotes the existing PoC capability presentation used by Worker detail. Advanced disclosures retain reported installation/version, authentication/configuration, health, update, detection, operation and diagnostics. Advertised, fixed allowlisted typed actions include re-detection, checks, installation/update/removal, authentication preparation/login/logout, public SSH identity management and repository read verification. No arbitrary shell or executable input is accepted. |
| Operation history | Bounded node-scoped typed history retains operation IDs, action/status, diagnostics, structured failure descriptions, queue/deadline times and public SSH identities. Cancel/reconcile controls are contextual. Legacy Worker provisioning plans remain in Worker/legacy administration; they do not represent Server node preparation. |
| Credential metadata | List/detail request only `/api/v1/credentials` and `/api/v1/credentials/{id}`. Boundary validators project the metadata allowlist and discard unknown payloads. No Worker secret-delivery or secret-read request occurs. |
| Credential create/replace | Transient password input is cleared before submission, on dialog close, navigation and logout. Existing protected Server storage APIs are used once. Create/replace responses are checked against expected metadata; uncertain writes retain a lock without retaining secret inputs. |
| Credential assignment/revocation | A registered Worker is explicitly selected; current metadata and Worker existence are checked before assignment. Assignment remains exclusive and delivery authorization remains separate. Revoke stops future delivery without changing provider permissions or already-delivered secrets. Status restrictions remain visible and authoritative Server semantics are preserved. |
| Credential detail/reload | Resource URL restores the shared metadata dialog after session restore. Missing/deleted IDs show an error; no other credential is selected. Close/Escape returns to Settings. Details retain secret reference, version, timestamps, assignment and revocation metadata. |
| Credential response loss | Safe attempt metadata is retained in the Settings workspace across navigation. Explicit fresh metadata must establish a matching new resource, replacement version, assignment or revocation before releasing the fence. Absent, ambiguous or conflicting evidence retains the lock. No create/replace is automatically retried and no secret is read for reconciliation. |
| Forms/dialog focus/theme | Official shared Untitled UI buttons, inputs, checkboxes and tokens compose the existing React Aria dialogs. Focus is contained; Escape/Cancel and resource-detail reload are supported. Every overlay uses theme surface/focus tokens. Settings uses the existing persisted preference owner and defaults to dark; no competing theme store is introduced. |
| Navigation/session privacy | Route/query changes destroy Settings transient state, local authorization and consent. Logout/session replacement destroys forms/challenges and clears private query state through the shared runtime. Codes and secret inputs never enter query/mutation caches, browser stores, URLs, diagnostics or review screenshots. |

## Contract limits and validation

History is bounded and supplies no total or complete long-term chronology. Failed
or stale observations cannot authorize readiness. The credential API has no
idempotency key or secret-comparison read: if metadata cannot establish an uncertain
create/replace outcome, the UI retains the lock and directs the operator to Server
administration. A fresh metadata version is evidence of a replacement, not a
browser claim about the secret's contents. No contract expansion is introduced.

`react-settings.test.cjs` covers metadata projection, challenge expiry/origin/status,
authoritative authentication, credential outcome matching and lost-response fences.
`settings-browser-review.cjs` serves the production bundle with deterministic
Server-shaped fixtures at 1280px/375px. It covers light/dark metadata/form/confirmation
overlays, consent reset, explicit preparation/login, pending recovery and queued
cancellation, response loss, reload, expiry, quiescence reconciliation, credential
create/replace/assign/revoke, detail reload/deletion and logout/input cleanup.
Screenshots are taken only when secret inputs are empty and no device code is
present. No live provider or deployment campaign is needed for these fixtures.

```sh
npm run check --prefix src/CodexServer/worker-poc
node --test --test-isolation=none tests/dashboard/react-settings.test.cjs tests/dashboard/react-infrastructure.test.cjs tests/dashboard/home-overview.test.cjs
NODE_PATH=/path/to/review/node_modules node tests/dashboard/settings-browser-review.cjs
```

Worker-configured validation remains the authoritative gate before integration.

Local review passed frontend type checking, lint and production build, the 58-test
focused dashboard regression set, a final 33-test contracts/runtime/Worker-controls
check and the Settings Chromium fixtures at both widths/themes. The existing Home
browser fixtures also passed, including the shared Settings readiness projection. Desktop and mobile
metadata/form/consent/confirmation screenshots were inspected; review artifacts
remain under `/tmp/settings-browser-review`. Browser launch needed approved process
access and the existing `/tmp` browser libraries/font configuration; no machine
packages were installed. The Server .NET build could not complete because restore
assets were absent and NuGet was unreachable (the SQLite package was unavailable
in the task cache). No live provider, credential delivery or deployed HTTPS campaign
was performed. These are local self-checks, not Worker-authoritative validation.
