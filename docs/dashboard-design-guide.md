# Administration dashboard design guide

The Server dashboard lives in `src/CodexServer/dashboard` (React + Untitled UI). The
approved visual and content reference is the navigable mockup
[`mockups/dashboard-v2.html`](mockups/dashboard-v2.html); open it in a browser and
compare rendered screens against it. Earlier SVG references and notes are archived in
[`archive/dashboard-v1`](archive/dashboard-v1) and are no longer implementation targets.

## What every screen must answer

1. Is everything working?
2. What is happening right now?
3. What do I have to do?

Show those answers first. Identifiers, capability evidence and raw diagnostics go in a
secondary *Diagnostics* tab or a collapsed *Technical detail* block, never on the main path.

## Screens

| Screen | Route | Content |
| --- | --- | --- |
| Home | `/home` | One status line, **Needs your attention** grouped by cause — projects paused on a Worker, unresolved restarts/updates, executions with an unknown outcome, queued requests that cannot be assigned, failed latest attempts and Workers that cannot take work — each item with its cause in one sentence and the action that resolves it, **Running now** with lifecycle steps, recently finished executions with filters. |
| Projects | `/projects` | List with search and status filter; side panel to add a project. |
| Project | `/projects/{id}` | Issues (open/all, search, status filter) with their derived status, configuration card, pause/resume (pausing holds new assignments on every Worker through `PUT /api/v1/project-scheduling/{id}` without changing the project revision; a project disabled through its lifecycle shows Resume instead), add an Issue to the queue, edit panel. |
| Issue | `/projects/{id}/issues/{n}` | Status, *what happened / what you can do* when it failed, description (Markdown), attempts with steps, status card and actions card. |
| Executions | `/executions` | Every attempt with search and state filter. |
| Execution | `/executions/{id}` | Five lifecycle steps with durations, explanation and actions, result and details. |
| Workers | `/workers` | Aligned table: Worker, health (one state with its main cause and last signal, filterable), activity, slots. Standalone Workers appear read-only. Side panel to connect a Worker. |
| Worker | `/workers/{id}` | One **health** tile (ready / working / working with paused projects / pausing / paused / not working / offline) with its main cause and last signal, a slots tile, projects paused on this Worker with **Resume on this Worker**; pause/resume scheduling; *Work* and *Diagnostics* tabs; edit panel. |
| Settings | `/settings` | Server, Server tools and credentials (side panel to add, replace, assign or revoke). |

Edits always happen in a right-hand side panel. Secrets are write-only.

## Status language

Use the shared mapping in `src/shared/model.ts`; do not invent per-screen rules.

- Execution: queued, starting, preparing, implementing, validating, integrating, reporting,
  integrated, conflict, conflict · retried (automatically requeued), Worker lost · retried,
  interrupted, failed, cancelled.
- Lifecycle steps: prepare, implement, validate, integrate, report. The step in progress
  animates; a failed step is red.
- Worker: connection and scheduling are separate facts and are always shown separately.
- Running states move: pulsing status dot, a light sweep on the current step and on
  occupied slots. All motion is disabled under `prefers-reduced-motion`.

Only the latest attempt of an Issue can need attention; an automatically requeued
failure does not.

## Foundation

- Untitled UI components and tokens (`src/untitled`) plus Tailwind utilities. No
  separate component CSS; `src/styles.css` holds only composition and animations.
- Semantic colours: green ok, red problem, yellow warning/paused, blue running, grey idle.
  Always pair colour with text.
- Dark theme by default; light theme from the sidebar. Spanish by default; English available.
  Both languages are required for every text (`src/shared/locales/{en,es}/*.ts`).
- Lists use the shared `Filters` (search + status select). Tables keep fixed status
  columns so header and rows align; on narrow screens rows stack.
- Loading, empty, failed (with retry) and "no matches" are distinct states.
- Keyboard operation, visible focus, labelled inputs, real links and buttons; side panels
  trap focus and close with Escape.
