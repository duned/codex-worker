# Server dashboard navigation

For approved visual and interaction requirements, follow the
[dashboard design guide](dashboard-design-guide.md).

React + Untitled UI is the sole Server dashboard. The shared package under
`src/CodexServer/worker-poc` retains its historical directory name, but builds one
Vite application and asset graph. The local standalone Worker dashboard is unchanged.

| Screen | Supported routes and context |
| --- | --- |
| Home | `/home` |
| Projects | `/projects`, `/projects/{id}`; `issue`, `issueState`, `label`, `issues` |
| Workers | `/workers`, `/workers/{id}`; preparation `step`, `project`, and enrollment `enroll=1` / `prepare=1` |
| Executions | `/executions`, `/executions/{id}`; `view=maintenance`, `project`, `state`, `issue`, `offset`, `operationOffset` |
| Settings | `/settings`, `/settings/{credentialId}`; `node=server` |

`/` normalizes to `/home` in the browser, preserving query context. Supported old
`#/section[/id]?context` bookmarks replace the current history entry once.
Historical `/dashboard-preview` routes redirect to their canonical counterparts;
`/workers/{id}/poc` redirects to Worker detail, preserving queries. All screens
share React routing, session restoration, cancellable typed API reads, the single
stream/fallback owner, and persistent dark/light preference (dark by default).
Missing resources show explicit errors; navigation/reload never submits mutations.
Dialogs use React Aria focus containment and dismissal. Secret/edit drafts remain
transient; authentication tokens never enter URLs or persistent browser storage.

The Server serves only the explicit GET route allowlist above and fixed embedded
assets. Unknown routes, extra segments, unsupported methods, API/protocol/health
failures never receive a shell fallback. Protected API reads and writes retain
existing authorization, CSRF, origin, HTTPS and node-local policy checks.

No proxy policy expansion is necessary: the existing `/dashboard-assets/preview/`
asset prefix is retained. Allow only that same-origin prefix in addition to the
explicit dashboard routes; do not allow arbitrary filesystem/static resources or
API fallback. Legacy `/dashboard-assets/worker-poc.js` and `.css` return 404.

See [final integration checklist and evidence](server-dashboard-final-review.md) for parity and validation,
and [React development/build guidance](server-dashboard-react.md).
