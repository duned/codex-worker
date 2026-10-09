> Current routing: all canonical Server dashboard screens use the shared React app.
> Preview URLs redirect to canonical routes; the separate PoC bundle and legacy
> Server frontend are removed. See [final cutover evidence](server-dashboard-cutover.md).
> Migration-stage descriptions and earlier validation below are historical evidence.

# Projects and GitHub Issue React migration (24.12.5)

Projects are implemented in the single production React package at
`/dashboard-preview/projects` and `/dashboard-preview/projects/{id}`. Canonical
legacy routes remain available until the series' final route cutover. The shared
shell, public MIT Untitled UI source/tokens, theme preference, session transport,
validated reads, mutation fences and React Aria dialogs are reused. No backend
contract, GitHub authorization ownership, product version or standalone Worker
interface changes are included.

## Parity inventory

| Existing flow | React presentation and retained contract |
| --- | --- |
| Project list/detail | Friendly name and repository first, enabled state, discovery/execution policy, configured eligibility labels and actionable next checks; IDs secondary. Lists show bounded readiness observations from up to three Workers, including reported missing requirements, stale evidence and checkout failures; details expose all returned Workers. |
| Repository selection | Explicit Server-authenticated repository discovery with bounded next-page reads; manual owner/repository fallback. Discovery failure retains the draft. |
| Create/edit | Repository → Essential configuration → Review in a shared managed dialog. Read verification is required before review and repeated immediately before saving. |
| Advanced project configuration | Typed/custom requirement descriptors, numeric version constraints, authentication repository scope, ready/blocked labels and all discovery fields (`enabled`, `intervalSeconds`, `pageSize`, `deadlineSeconds`). Server validation remains authoritative. |
| Project concurrency | Server-owned `maxParallelTasks` is edited and reviewed with the project definition; Automatic adds no project cap, while an explicit 1–8 limit applies across Workers. The detail view labels it as configured Server policy and keeps observed Worker availability separate. |
| Lifecycle/delete | Shared consequence confirmations; fresh uncached revision check, expected revision, Server in-use checks, no automatic retries. Disabling pauses new admission/discovery without cancelling existing work or leases. |
| Revision conflict | Retain draft, show current revision, explicitly load current definition and verify again. Reconciliation accepts an edit only at the expected next revision with the matching normalized definition. |
| Lost project save | Retain definition/identity/revision in the session's transient workspace. Explicit read checks saved definition, absence/unchanged revision or conflict; no second automatic POST/PUT. |
| Worker association/preparation | Linked Workers use authoritative diagnostics with reported/central revision, observation status, synchronization, capability requirements and materialization separately. Stale/cached/not-reported evidence never becomes scheduling authority. Preparation links retain `step=preparation&project=…`. |
| Project executions/discovery observation | Project-filtered execution link and latest bounded 50-request observation. Queue counts are explicitly bounded, never discovery-cycle totals. |
| Repository permission | Check access shows Server authentication and repository-read separately; failures direct operators to Server GitHub connection/permission checks. Worker checkout/push permission is independent. |
| Issue list/filter/detail | Existing bounded Issue read APIs, state/label filters, labels, Server eligibility reasons and native blocked-by evidence. Eligible list rows and detail expose explicit enqueue. |
| Create/edit Issue | Transient title/body draft, explicit Server preview, then apply in a shared dialog. Successful creation opens the new Issue in the same project/filter/preparation context. |
| Eligibility labels | Only project-configured labels are offered; preview and apply use the existing configured-label API. |
| Native blocked-by administration | Positive bounded Issue number, preview and explicit apply/remove through the existing native relationship API. No label-based substitute or browser toolbox implementation. |
| Enqueue | Shared explicit confirmation, fresh project revision and Issue admission check, Server admission authority. Lost response requires positive new matching execution evidence; absence in a bounded list retains the fence. |
| Queued eligibility refresh | Explicit confirmation and current Server Issue check. Display returned execution eligibility/reasons or no queued request. Lost response reconciliation requires changed durable eligibility timestamps for visible queued requests; incomplete/unchanged evidence retains the lock. |
| Lost Issue writes | Edit/label/relationship reads establish desired or unchanged prior state. Lost create requires an explicitly supplied Issue number inspected in GitHub and matching title/body; list absence cannot establish non-creation. Unrelated/insufficient observations keep the lock. |
| Navigation and drafts | `issue`, `issueState`, `label`, `issues` and preparation query fields remain in the URL, including Back/reload/filter changes. Closing retains a transient draft; the Projects workflow offers recovery, and saved detail pages resume only drafts for that same project context. Discard requires a shared confirmation. Unload warns for retained drafts/attempts; reload/sign-out clears transient forms and authorization. |
| Dialogs and theme | Shared React Aria managed forms/confirmations, initial safe focus, pending dismissal/field locks, synchronous submit guards; persistent light/dark preference with dark default. No alert/confirm/prompt or unmanaged dialogs. |

`features/projects` owns project/Issue composition, contract validators and the
transient workspace. The workspace is mounted above routes but inside private
session presentation, so polling/navigation cannot lose mutation lineage and
sign-out clears it. Only explicit reconciliation can release an uncertain fence.
The shared transport's preview method allowlists verification/Issue preview paths
and forces `previewOnly: true` for Issue requests. It has the same CSRF,
cancellation, timeout, session-generation and safe-error boundaries as other calls.

## Honest API limits

No discovery-cycle timestamp, cycle candidate total, complete Issue total or
unbounded execution history exists in these projections. Capability eligibility,
materialization, observation freshness and scheduling permission remain distinct.
A lost operation with no conclusive durable evidence stays fenced for that
session; refresh and navigation do not authorize replay. A fresh session/reload
cannot retain browser-only attempt metadata, so inspect Server/GitHub state before
submitting again. Drafts and operation evidence are never stored in preferences.

Worker preparation still opens its existing complete administration route while
its feature migration proceeds. Projects do not implement a new Worker activation
workflow. Relationship graphs, parent/sub-Issue batch tools and CLI-only workflows
were not existing dashboard flows and are outside this parity inventory.

## Validation

`react-projects.test.cjs` covers boundary contracts, discovery bounds, normalized
save/revision reconciliation, custom typed requirements/scopes, native relationship
state, safe Issue URLs, separate stale Worker evidence and forced non-effect
previews. Existing infrastructure, project onboarding and GitHub administration
regressions remain applicable.

`projects-browser-review.cjs` reviews the production asset graph using deterministic
Server-shaped fixtures at 1280px and 375px. It covers project creation/edit/lifecycle/
delete, discovery/manual entry, permission/verification failures, revision conflict,
stale Worker observations, Issue preview/edit/create/relationships/labels/enqueue/
eligibility refresh, lost mutation responses, reload/Back/filter/preparation context,
transient draft navigation and absence of render/navigation writes. Screenshots are
written under `/tmp/projects-browser-review` by default. Run with separately
installed Playwright and Chromium, as for the other production browser reviews.
These checks do not replace Worker-configured authoritative validation or deployed
HTTPS/provider/real-node acceptance.

Local review passed the frontend typecheck, lint and production build, 49 focused
Node regressions, and both the Projects/Issue and existing preview production
browser reviews. Desktop/mobile dark screens, the mobile project form and light
Issue preview screenshots were inspected locally. Browser process restrictions
were resolved using permitted process access and existing libraries/fonts under
`/tmp`; no machine packages were installed. Locked public npm restoration also
required permitted network access. The optional focused .NET test attempt with
`--no-restore` could not start because this checkout lacks restored test assets
(`NETSDK1004`). No live provider, deployed HTTPS or real-node campaign was run.
Worker-configured checks remain the separate authoritative gate.
