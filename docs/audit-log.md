# Operator identity and audit log

The Server records every administrative change: who made it, what it was, when, and whether it succeeded. The dashboard shows it in **Settings → Activity**; the API is `GET /api/v1/audit?limit=50&before=<id>&operator=<name>` (management authorization, newest first, `nextBefore` pages back).

## Who

The management token is shared, so identity is declared, not authenticated:

- **Dashboard:** the sign-in form asks for *Your name*. It is kept with the browser session (and remembered in that browser) and recorded with every change made in it. Without a name the actor shows as *Dashboard (no name)*.
- **API clients with the bearer token:** send `X-Codex-Operator: <name>` (percent-encoded if not ASCII). Without it the actor shows as *API token*.
- **MCP clients:** recorded as *MCP client*; with the management token they may also send `X-Codex-Operator`.

Names are trimmed, at most 64 printable characters; anything else is ignored. Maintenance commands keep the same identity in their `authorizedBy` field.

## What is recorded

- Every authorized `POST`, `PUT`, `PATCH` and `DELETE` under `/api` made with the management token or a browser session: queueing, cancelling, prioritising, reconciling, archiving, project and Worker changes, pauses, credentials, updates, maintenance and provisioning. Sign-in is recorded with the name given.
- MCP tools that change state (`dispatch_execution`, `cancel_execution`, Issue planning and maintenance tools), with their identifying arguments such as `projectId` and `issueNumber`. Read-only tools are not recorded.
- The result: *Done* (2xx), *Refused* (4xx, including conflicts and validation errors) or *Failed* (5xx). Each event keeps the request id (`X-Codex-Request-Id`) to correlate with the Server log.

Not recorded: reads, previews (`previewOnly`) and project verification, which change nothing; Worker and observer calls, which carry their own identity; requests without valid management authorization. Request bodies, query strings and credentials are never stored.

## Retention

Events are kept for 365 days and at most the latest 20,000; older ones are removed as new ones arrive. The log lives in the Server database, so Server backups include it.
