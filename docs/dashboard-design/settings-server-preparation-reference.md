# Settings and Server preparation visual reference

Open the self-contained [Settings and Server preparation mockup](settings-server-preparation-reference.html) in a browser. It uses the established administration dashboard shell, typography, cards, status badges, dialogs and responsive layout. The example values are illustrative UI fixtures, not live state or credentials.

## Contract mapping

| Mockup content | Existing source |
| --- | --- |
| Server service account connection readiness and explicit preparation/login actions | `GET /api/v1/nodes`, the Server GitHub connection endpoint, shared `serverGitHubReadiness` projection, and typed actions from `src/CodexServer/worker-poc/src/features/settings/ServerPreparation.tsx` |
| Server capabilities, advertised typed actions, provisioning readiness and operation controls/history | `GET /api/v1/nodes`, `GET /api/v1/nodes/server/commands`, `POST /api/v1/provisioning/commands`, and cancel/reconcile command endpoints; `NodeCommandSummary` and `nodeActions` define the displayed action/history boundary |
| Credential status, provider/type, version and assignment, with add/replace/assign/revoke dialogs | `GET /api/v1/credentials`, `GET /api/v1/credentials/{id}`, and the existing credential administration mutations. `settings/contracts.ts` explicitly projects metadata and discards unknown fields; it never reads secret payloads. Registered Worker options come from `GET /api/v1/workers`. |
| Status freshness and refresh errors | Node, command and connection observations retain their API freshness/status fields; failed reads do not constitute current readiness. Credential and Worker reads have section-level loading/error states. |

The page keeps connection, capability, provisioning and credential state separate. The primary view summarizes the next allowed action; operation diagnostics and detailed capability evidence are secondary. Device-login consent, elevation and quiescence confirmation remain explicit dialog gates. No secret, device code, private key or raw operation output appears in the mockup.

## Data gaps and limits

- Server command history is bounded; there is no total or complete long-term history count. The mockup labels it as bounded and does not imply completeness.
- Credential metadata establishes version/status/assignment, not secret correctness. The API has no secret comparison/read or idempotency key. If a create/replace response is lost and fresh metadata cannot establish the result, the existing UI keeps the action locked for reconciliation.
- Node and GitHub connection observations can be stale or unavailable. They cannot authorize a new action; submission re-reads current node, command and connection state, and the Server still enforces local provisioning policy.
- Operation failure details are structured API evidence. The reference intentionally keeps them out of the primary view; it does not imply access to raw logs.

The artifact is a design reference only. Production UI and API contracts are unchanged.
