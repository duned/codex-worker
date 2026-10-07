// Production React Projects/Issue acceptance using only deterministic Server-shaped fixtures.
// NODE_PATH=/path/to/playwright/node_modules node tests/dashboard/projects-browser-review.cjs [assets] [screenshots]
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');
const { props } = require('./worker-poc-fixtures.cjs');
const assets = path.resolve(process.argv[2] || 'src/CodexServer/obj/worker-poc/preview');
const output = path.resolve(process.argv[3] || '/tmp/projects-browser-review');
const clone = value => structuredClone(value);
(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage();
    await page.clock.setFixedTime(props.now);
    page.setDefaultTimeout(12000);
    const errors = [], effects = [], previews = [];
    page.on('pageerror', error => errors.push(error.message));
    let projects = clone(props.projects), failVerify = false, loseSave = false, loseIssue = false, loseEnqueue = false, loseRefresh = false, loseLifecycle = false, loseDelete = false;
    let queue = [], nextIssue = 30;
    const issues = new Map([[27, { number: 27, title: 'Repair parser', body: 'Bounded description', state: 'open', url: 'https://github.com/owner/repo/issues/27', labels: ['ready'], blockedBy: [], isEligible: true, eligibilityReasons: [] }]]);
    await page.route('https://dashboard.test/**', async route => {
      const request = route.request(), url = new URL(request.url()), endpoint = url.pathname, method = request.method();
      if (endpoint.startsWith('/dashboard-assets/preview/')) {
        const relative = endpoint.slice('/dashboard-assets/preview/'.length); assert.ok(!relative.includes('..'));
        return route.fulfill({ path: path.join(assets, relative), contentType: relative.endsWith('.css') ? 'text/css' : 'text/javascript' });
      }
      if (endpoint.startsWith('/dashboard-preview')) return route.fulfill({ path: path.join(assets, 'index.html'), contentType: 'text/html' });
      if (endpoint.endsWith('/session')) return route.fulfill({ json: { csrfToken: 'fixture-csrf', expiresAtUtc: '2026-01-01T01:00:00Z' } });
      if (endpoint.endsWith('/stream')) return route.fulfill({ status: 503 });
      const body = request.postDataJSON();
      if (method !== 'GET') assert.equal(request.headers()['x-codex-csrf'], 'fixture-csrf');
      if (endpoint === '/api/v1/projects/verify') return route.fulfill({ status: failVerify ? 503 : 200, json: { repository: body.repository, defaultBranch: body.defaultBranch, repositoryReadable: true, branchExists: true, diagnostic: 'Server read verified.' } });
      if (endpoint === '/api/v1/github/repositories') return route.fulfill({ json: { repositories: [{ repository: 'owner/new', name: 'New project', description: 'Discovered repository', defaultBranch: 'trunk' }], nextPage: null } });
      if (endpoint === '/api/v1/workers') return route.fulfill({ json: [props.observations[0]] });
      if (endpoint.endsWith('/diagnostics')) return route.fulfill({ json: { ...props.diagnostics, projects: [{ projectId: 'project-a', isEligible: true, missingRequirements: [], workerReportedRevision: 1, materializationState: 'failed', observationStatus: 'stale-heartbeat' }] } });
      if (endpoint === '/api/v1/executions') {
        const items = queue.filter(e => !url.searchParams.get('projectId') || e.projectId === url.searchParams.get('projectId')).filter(e => !url.searchParams.get('workId') || e.workReference.id === url.searchParams.get('workId'));
        return route.fulfill({ json: items });
      }
      if (endpoint.endsWith('/github/access')) return route.fulfill({ json: { repository: 'owner/repo', cliAuthenticated: true, repositoryReadable: false, checkedAtUtc: '2026-01-01T00:02:00Z', diagnostic: 'Read unavailable.' } });
      const issueMatch = endpoint.match(/\/projects\/([^/]+)\/github\/issues(?:\/(\d+))?(.*)$/);
      if (issueMatch) {
        const [, projectId, rawNumber, suffix] = issueMatch, number = Number(rawNumber), current = issues.get(number);
        if (method === 'GET') return route.fulfill({ status: rawNumber && !current ? 404 : 200, json: rawNumber ? current ?? {} : [...issues.values()] });
        if (body?.previewOnly) { previews.push(endpoint); return route.fulfill({ json: { operation: rawNumber ? 'update' : 'create', repository: projects.find(p => p.id === projectId)?.repository ?? 'owner/repo', previewOnly: true, changed: true, issueNumber: rawNumber ? number : null, title: body.title ?? current?.title, body: body.body ?? current?.body, label: body.label ?? null, applied: body.applied ?? null, relatedIssueNumber: body.blockerIssueNumber ?? null } }); }
        effects.push({ endpoint, method, body });
        if (suffix === '/enqueue') {
          const e = { id: 'queued-27', projectId, workReference: { type: 'github-issue', id: String(number) }, state: 'Queued', createdAtUtc: '2026-01-01T00:02:30Z', managedEligibilityState: 'eligible', managedEligibilityReasons: [], managedEligibilityCheckedAtUtc: '2026-01-01T00:02:30Z' };
          queue.push(e); if (loseEnqueue) { loseEnqueue = false; return route.abort(); } return route.fulfill({ status: 201, json: e });
        }
        if (suffix === '/eligibility/refresh') { queue = queue.map(e => ({ ...e, managedEligibilityCheckedAtUtc: '2026-01-01T00:02:31Z' })); if (loseRefresh) { loseRefresh = false; return route.abort(); } return route.fulfill({ json: queue }); }
        let updated;
        if (!rawNumber) { const newNumber = nextIssue++; updated = { ...clone(issues.get(27)), number: newNumber, title: body.title, body: body.body, url: `https://github.com/owner/repo/issues/${newNumber}` }; }
        else if (suffix === '/labels/configured') updated = { ...current, labels: body.applied ? [...current.labels, body.label] : current.labels.filter(l => l !== body.label) };
        else if (suffix === '/dependencies/blocked-by') updated = { ...current, blockedBy: body.applied ? [{ number: body.blockerIssueNumber, title: 'Prerequisite', state: 'open', url: `https://github.com/owner/repo/issues/${body.blockerIssueNumber}` }] : [] };
        else updated = { ...current, title: body.title, body: body.body };
        updated.isEligible = updated.labels.includes('ready') && !updated.labels.includes('blocked') && !updated.blockedBy.some(b => b.state === 'open');
        updated.eligibilityReasons = updated.isEligible ? [] : ['Configured label or open blocked-by Issue prevents eligibility.'];
        issues.set(updated.number, updated);
        if (loseIssue) { loseIssue = false; return route.abort(); }
        return route.fulfill({ json: { operation: rawNumber ? 'update' : 'create', repository: 'owner/repo', previewOnly: false, changed: true, issueNumber: updated.number, title: updated.title, body: updated.body, label: body.label ?? null, applied: body.applied ?? null, relatedIssueNumber: body.blockerIssueNumber ?? null } });
      }
      if (endpoint === '/api/v1/projects' && method === 'GET') return route.fulfill({ json: projects });
      const projectMatch = endpoint.match(/^\/api\/v1\/projects\/([^/]+)(\/lifecycle)?$/);
      if (projectMatch && method === 'GET') return route.fulfill({ status: projects.some(p => p.id === projectMatch[1]) ? 200 : 404, json: projects.find(p => p.id === projectMatch[1]) ?? {} });
      if (endpoint === '/api/v1/projects' || projectMatch) {
        effects.push({ endpoint, method, body });
        if (method === 'DELETE') { projects = projects.filter(p => p.id !== projectMatch[1]); if (loseDelete) { loseDelete = false; return route.abort(); } return route.fulfill({ status: 204 }); }
        if (projectMatch?.[2]) { const updated = { ...projects.find(p => p.id === projectMatch[1]), enabled: body.enabled, revision: body.expectedRevision + 1 }; projects = projects.map(p => p.id === updated.id ? updated : p); if (loseLifecycle) { loseLifecycle = false; return route.abort(); } return route.fulfill({ json: updated }); }
        const updated = method === 'POST' ? { ...body, id: 'new-project', enabled: true, revision: 1 } : { ...projects.find(p => p.id === projectMatch[1]), ...body.definition, revision: body.expectedRevision + 1 };
        projects = method === 'POST' ? [...projects, updated] : projects.map(p => p.id === updated.id ? updated : p);
        if (loseSave) { loseSave = false; return route.abort(); } return route.fulfill({ json: updated });
      }
      throw Error(`Unexpected fixture ${method} ${endpoint}`);
    });
    const detail = 'https://dashboard.test/dashboard-preview/projects/project-a?issue=27&issueState=open&label=ready&issues=1&step=preparation';
    for (const width of [1280, 375]) {
      await page.setViewportSize({ width, height: 1000 }); await page.goto(detail);
      await page.getByRole('heading', { name: 'Sample project', exact: true }).waitFor();
      await page.getByRole('heading', { name: '#27 · Repair parser' }).waitFor();
      await page.getByText('Stale heartbeat · check Worker connection', { exact: true }).waitFor();
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `No overflow at ${width}`);
      assert.equal(effects.length, 0);
      await page.getByRole('button', { name: 'Check repository access' }).click();
      await page.getByText(/Repository read: unavailable/).waitFor();
      await page.screenshot({ path: path.join(output, `project-${width}.png`), fullPage: true });
      await page.reload(); await page.getByRole('heading', { name: '#27 · Repair parser' }).waitFor(); assert.equal(new URL(page.url()).search, new URL(detail).search);
      if (width === 375) {
        await page.getByRole('button', { name: 'Edit project', exact: true }).click(); const mobileDialog = page.getByRole('dialog', { name: 'Edit project', exact: true });
        await mobileDialog.getByRole('button', { name: 'Next', exact: true }).click(); await mobileDialog.getByLabel('Description').fill('Mobile draft');
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)); await page.screenshot({ path: path.join(output, 'project-form-mobile.png'), fullPage: true });
        await mobileDialog.getByRole('button', { name: 'Discard draft', exact: true }).click(); await page.getByRole('dialog', { name: 'Discard project draft?' }).getByRole('button', { name: 'Discard draft', exact: true }).click();
        assert.equal(effects.length, 0);
      }
    }
    await page.setViewportSize({ width: 1280, height: 1000 });
    await page.getByRole('button', { name: 'Use light mode' }).click(); await page.reload();
    await page.getByRole('button', { name: 'Use dark mode' }).waitFor();
    // Edit preview, lost response and explicit reconciliation, with exactly one durable write.
    await page.getByRole('button', { name: 'Edit title/body' }).click();
    let dialog = page.getByRole('dialog', { name: 'Edit Issue #27' });
    await dialog.getByLabel('Issue title').fill('Parser repaired');
    await dialog.getByRole('button', { name: 'Preview changes' }).click(); await dialog.getByRole('heading', { name: 'Server preview' }).waitFor();
    assert.equal(effects.length, 0); await page.screenshot({ path: path.join(output, 'issue-preview-light.png'), fullPage: true }); loseIssue = true;
    await dialog.getByRole('button', { name: 'Apply previewed changes' }).click(); await dialog.getByText(/Result uncertain/).waitFor();
    await dialog.getByRole('button', { name: 'Close', exact: true }).click();
    await page.getByRole('button', { name: 'Reconcile authoritative state' }).click();
    await page.getByText('Desired Issue state confirmed.').waitFor(); assert.equal(effects.length, 1);
    // Native dependencies and configured labels use previews and existing Server APIs.
    await page.reload(); await page.getByRole('heading', { name: '#27 · Parser repaired' }).waitFor();
    await page.getByRole('button', { name: 'Add blocked-by Issue' }).click(); dialog = page.getByRole('dialog', { name: 'Administer Issue #27' });
    await dialog.getByLabel('Blocking Issue number').fill('28'); await dialog.getByRole('button', { name: 'Preview changes' }).click(); await dialog.getByRole('button', { name: 'Apply previewed changes' }).click(); await dialog.waitFor({ state: 'hidden' });
    await page.getByRole('button', { name: 'Remove relationship #28' }).waitFor();
    await page.getByRole('button', { name: 'Remove relationship #28' }).click(); dialog = page.getByRole('dialog', { name: 'Administer Issue #27' }); await dialog.getByRole('button', { name: 'Preview changes' }).click(); await dialog.getByRole('button', { name: 'Apply previewed changes' }).click(); await dialog.waitFor({ state: 'hidden' });
    await page.getByRole('button', { name: 'Add blocked', exact: true }).click(); dialog = page.getByRole('dialog', { name: 'Administer Issue #27' }); await dialog.getByRole('button', { name: 'Preview changes' }).click(); await dialog.getByRole('button', { name: 'Apply previewed changes' }).click(); await dialog.waitFor({ state: 'hidden' });
    await page.getByRole('button', { name: 'Remove blocked', exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Explicitly enqueue' }).isDisabled(), true);
    await page.getByRole('button', { name: 'Remove blocked', exact: true }).click(); dialog = page.getByRole('dialog', { name: 'Administer Issue #27' }); await dialog.getByRole('button', { name: 'Preview changes' }).click(); await dialog.getByRole('button', { name: 'Apply previewed changes' }).click(); await dialog.waitFor({ state: 'hidden' });
    loseEnqueue = true; await page.getByRole('button', { name: 'Explicitly enqueue' }).click(); dialog = page.getByRole('dialog', { name: 'Administer Issue #27' }); await dialog.getByRole('button', { name: 'Enqueue Issue' }).click(); await dialog.getByText(/Result uncertain/).waitFor(); await dialog.getByRole('button', { name: 'Close', exact: true }).click();
    await page.getByRole('button', { name: 'Reconcile authoritative state' }).click(); await page.getByText('Matching execution confirmed. No enqueue repeated.').waitFor(); assert.equal(effects.filter(e => e.endpoint.endsWith('/enqueue')).length, 1);
    loseRefresh = true; await page.getByRole('button', { name: 'Refresh queued eligibility' }).click(); dialog = page.getByRole('dialog', { name: 'Administer Issue #27' }); await dialog.getByRole('button', { name: 'Refresh eligibility' }).click(); await dialog.getByText(/Result uncertain/).waitFor(); await dialog.getByRole('button', { name: 'Close', exact: true }).click(); await page.getByRole('button', { name: 'Reconcile authoritative state' }).click(); await page.getByText('queued-27: eligible', { exact: true }).waitFor();
    // Create response loss requires explicit identity; never infer absence from a bounded Issue list.
    loseIssue = true; await page.getByRole('button', { name: 'Create Issue' }).click(); dialog = page.getByRole('dialog', { name: 'Create Issue' }); await dialog.getByLabel('Issue title').fill('Created task'); await dialog.getByLabel('Issue body').fill('Transient body'); await dialog.getByRole('button', { name: 'Preview changes' }).click(); await dialog.getByRole('button', { name: 'Apply previewed changes' }).click(); await dialog.getByText(/Result uncertain/).waitFor(); await dialog.getByRole('button', { name: 'Close', exact: true }).click(); await page.getByLabel('Created Issue number (inspect GitHub)').fill('30'); await page.getByRole('button', { name: 'Reconcile authoritative state' }).click(); await page.getByText('Desired Issue state confirmed.').waitFor();
    // Project save verification failure and revision conflict retain the same draft.
    await page.getByRole('button', { name: 'Edit project', exact: true }).click(); dialog = page.getByRole('dialog', { name: 'Edit project', exact: true }); await dialog.getByRole('button', { name: 'Next', exact: true }).click();
    failVerify = true; await dialog.getByRole('button', { name: 'Verify and review' }).click(); await dialog.getByText(/Operation unavailable/).waitFor(); assert.equal(await dialog.getByRole('button', { name: 'Save reviewed project' }).count(), 0);
    failVerify = false; await dialog.getByRole('button', { name: 'Verify and review' }).click(); await dialog.getByRole('button', { name: 'Save reviewed project' }).waitFor();
    projects[0] = { ...projects[0], revision: 2, description: 'Concurrent edit' }; await dialog.getByRole('button', { name: 'Save reviewed project' }).click(); await dialog.getByRole('button', { name: 'Load current definition' }).waitFor(); await dialog.getByRole('button', { name: 'Load current definition' }).click(); await dialog.getByRole('button', { name: 'Next', exact: true }).click(); assert.equal(await dialog.getByLabel('Description').inputValue(), 'Concurrent edit');
    await dialog.getByText('Enable automatic Issue discovery', { exact: true }).click();
    assert.equal(await dialog.getByRole('checkbox', { name: 'Enable automatic Issue discovery' }).isChecked(), true); await dialog.getByText('Advanced · discovery and execution requirements', { exact: true }).click(); await dialog.getByLabel('Discovery interval (seconds)').fill('600'); await dialog.getByLabel('Maximum discovery page size').fill('10'); await dialog.getByLabel('Cycle deadline (seconds)').fill('30');
    await dialog.getByRole('button', { name: 'Verify and review' }).click(); loseSave = true; await dialog.getByRole('button', { name: 'Save reviewed project' }).click(); await dialog.getByText(/Save result uncertain/).waitFor(); await dialog.getByRole('button', { name: 'Close', exact: true }).click();
    await page.getByRole('button', { name: 'Check saved definition' }).click(); await page.getByText('Saved definition confirmed.').waitFor(); assert.deepEqual(projects[0].automaticDiscovery, { enabled: true, intervalSeconds: 600, pageSize: 10, deadlineSeconds: 30 });
    // URL Back/filter and transient form navigation, with no effects on refresh.
    const count = effects.length; await page.getByLabel('Filter by label').fill('blocked'); await page.getByRole('button', { name: 'Load Issues' }).click(); assert.equal(new URL(page.url()).searchParams.get('step'), 'preparation'); await page.goBack(); await page.getByRole('heading', { name: '#27 · Parser repaired' }).waitFor(); assert.equal(new URL(page.url()).search, new URL(detail).search); assert.equal(effects.length, count);
    await page.goto('https://dashboard.test/dashboard-preview/projects'); await page.getByRole('heading', { name: 'Projects', exact: true }).waitFor();
    await page.getByRole('button', { name: 'Create project', exact: true }).click(); dialog = page.getByRole('dialog', { name: 'Create project', exact: true }); await dialog.getByRole('button', { name: 'Discover repositories' }).click(); await dialog.getByLabel('Accessible repository').selectOption('owner/new'); await dialog.getByRole('button', { name: 'Next', exact: true }).click(); assert.equal(await dialog.getByLabel('Project name').inputValue(), 'New project');
    await dialog.getByRole('button', { name: 'Close', exact: true }).click(); await page.getByRole('link', { name: 'Home', exact: true }).click(); await page.getByRole('link', { name: 'Projects', exact: true }).click(); await page.getByRole('button', { name: 'Resume project draft' }).click(); dialog = page.getByRole('dialog', { name: 'Create project', exact: true }); assert.equal(await dialog.getByLabel('Project name').inputValue(), 'New project');
    await dialog.getByRole('button', { name: 'Verify and review' }).click(); await dialog.getByRole('button', { name: 'Save reviewed project' }).click(); await dialog.waitFor({ state: 'hidden' }); await page.getByRole('link', { name: /New project/ }).click(); await page.getByRole('heading', { name: 'New project', exact: true }).waitFor();
    loseLifecycle = true; await page.getByRole('button', { name: 'Disable', exact: true }).click(); dialog = page.getByRole('dialog', { name: 'Disable project?' }); await dialog.getByRole('button', { name: 'Disable project', exact: true }).click(); await dialog.getByRole('button', { name: 'Cancel' }).click(); await page.getByRole('button', { name: 'Reconcile authoritative state' }).click(); await page.getByText('Lifecycle change confirmed.').waitFor();
    // React reconciliation refreshes observations, never repeats the write.
    await page.reload(); await page.getByRole('button', { name: 'Enable', exact: true }).waitFor(); await page.getByRole('button', { name: 'Enable', exact: true }).click(); dialog = page.getByRole('dialog', { name: 'Enable project?' }); await dialog.getByRole('button', { name: 'Enable project', exact: true }).click(); await dialog.waitFor({ state: 'hidden' });
    loseDelete = true; await page.getByRole('button', { name: 'Delete', exact: true }).click(); dialog = page.getByRole('dialog', { name: 'Delete central project?' }); await dialog.getByRole('button', { name: 'Delete project', exact: true }).click(); await dialog.getByRole('button', { name: 'Cancel' }).click(); await page.getByRole('button', { name: 'Reconcile authoritative state' }).click(); await page.getByText('Deletion confirmed.').waitFor(); assert.equal(effects.filter(e => e.method === 'DELETE').length, 1);
    assert.equal(previews.length >= 5, true); assert.deepEqual(errors, []);
    console.log('Projects/Issue production browser review passed at 1280px and 375px.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
