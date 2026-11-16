# Server dashboard: architecture and build

`src/CodexServer/dashboard` is the only Server dashboard. `src/CodexServer/dashboard-legacy`
is a reference backup of the previous dashboard: it is not built, embedded, tested or
deployed. Design rules are in the [design guide](dashboard-design-guide.md).

## Layout

| Path | Owns |
| --- | --- |
| `src/app` | Mount, routes and the error boundary. |
| `src/pages` | One file per screen plus its side panels. |
| `src/shared/api` | Session, CSRF, request fencing, status polling and response validation (`DashboardRuntime`). |
| `src/shared/model.ts` | Status, step, attention and scheduling rules shared by every screen. |
| `src/shared/ui.tsx` | Cards, rows, pills, steps, slots, filters, side panel and form fields. |
| `src/shared/locales` | English and Spanish texts, one file per area. |
| `src/untitled` | Upstream Untitled UI components and tokens (MIT, see `UNTITLED-UI-LICENSE`). |

`SessionProvider` creates one `DashboardRuntime` per tab. It polls `/api/status` and
refreshes projects, Workers and executions when the Server's freshness markers change.
Mutations go through `useAction`, which never retries; after a failure it re-reads Server
state before allowing the same action again.

Routes are the Server's explicit UI allowlist: `/home`, `/projects`, `/projects/{id}`,
`/projects/{id}/issues/{n}`, `/workers`, `/workers/{id}`, `/executions`, `/executions/{id}`
and `/settings`. Older `/dashboard-preview/...` and `/workers/{id}/poc` bookmarks redirect.

## Development

Requires Node >= 22.12. Installed Servers need no Node: assets are embedded.

```sh
npm ci --prefix src/CodexServer/dashboard --no-audit --no-fund
npm run check --prefix src/CodexServer/dashboard
npm run dev --prefix src/CodexServer/dashboard
```

`npm run check` runs typecheck, lint, the behaviour tests in `tests/*.test.mjs` and the
production build. For a live API during `npm run dev`, set `DASHBOARD_API_ORIGIN` to an
HTTPS origin that matches the Server's administration origin.

## Build and embedding

Every `dotnet build` of `CodexServer` runs `npm ci` (when the lock file changed),
typecheck, lint and `build.mjs`, which writes the Vite output and a SHA-256 manifest to
`obj/<configuration>/net10.0/dashboard/preview`. MSBuild embeds that output; at startup
the Server verifies the manifest and serves only listed assets under
`/dashboard-assets/preview/`. Publishing with `--no-build` is rejected.
