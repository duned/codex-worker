# Canonical React dashboard cutover (24.12.9)

The route/action/API inventories in the Home, Projects, Workers, Executions and
Settings migration documents were reviewed. Each has a production React module
using existing Server APIs; no backend contract or product version changes are
required. The remaining canonical ownership gap was shell serving and preview
navigation links. These now use a single React application across all sections.

The Server no longer embeds or serves `dashboard.html`, `dashboard-*.js`, or the
duplicate `worker-poc.js`/`.css` resources. The obsolete VM/DOM bridge tests were
retired with their runtime. Observable React contract tests cover session fencing,
stream suspension/replacement, polling ownership, expiration, mutation locks,
recovery, project/Issue verification, Worker preparation and credential metadata.
The real Server SSE contract and disconnect/shutdown tests remain.

Canonical route tests exercise shell equivalence, detail/query contexts, theme
bootstrap ordering, fixed assets, historical redirects and explicit rejection.
The artifact harness compares every asset byte and the shell against a fresh
reference build and checks removed resource rejection with Node/npm absent from
the published Server PATH. Browser harnesses now exercise canonical URLs.

Build/development requires Node 22.12+ and npm plus .NET 10; installed nodes require
no Node/npm/CDN. Run `npm ci --prefix src/CodexServer/worker-poc --no-audit --no-fund`,
then `npm run check --prefix src/CodexServer/worker-poc`. `npm run dev` serves
canonical paths at the Vite root; the optional API proxy must retain the configured
administration origin. MSBuild always regenerates the complete Vite graph before
embedding and refuses no-build publish. Untitled UI licenses remain in source and
bundle banners. No release is published and no source version is changed.

For focused acceptance, publish the Server to a disposable directory, build a fresh
reference with `node build.mjs /tmp/dashboard-reference` from the frontend directory,
then run `node tests/dashboard/worker-poc-artifact-review.cjs PUBLISH_DIRECTORY
/tmp/dashboard-reference`. An extracted Server archive can replace PUBLISH_DIRECTORY.
Run the canonical browser harnesses with separately installed Playwright/Chromium
at desktop/mobile widths, covering deep links, reload, session, dialogs and themes.
These checks do not establish live provider or deployed HTTPS/VPN acceptance.

API limits remain unchanged: bounded activity/history is not a total; Issue titles
and stage timelines absent from contracts are not fabricated. Server authority,
local consent and uncertain-operation reconciliation remain authoritative.

## Local validation evidence

Six dependency-free theme/model checks passed, as did the stubbed release-script
regression and diff/syntax review. `npm ci` failed with registry DNS `EAI_AGAIN`;
`npm run check` could not find TypeScript. The remaining React Node tests could
not load esbuild from the incomplete dependency installation. The no-restore .NET
build failed because NuGet assets were absent and its frontend restore encountered
the same DNS failure. Published/extracted Server artifact and Chromium browser
acceptance are UNRUN in this environment, because a current build cannot be
produced. These are optional local checks; the Worker configured validation gate
runs separately. Earlier migration acceptance counts do not certify this cutover.
