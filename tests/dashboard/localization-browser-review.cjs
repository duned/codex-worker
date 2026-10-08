// Production dashboard, deterministic API fixtures; no provider or node mutations.
const assert = require('node:assert/strict');
const path = require('node:path');
const { chromium } = require('playwright');
const { props } = require('./worker-poc-fixtures.cjs');
const assets = path.resolve('src/CodexServer/obj/worker-poc/preview');
(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1280, height: 1000 } }), errors = [], writes = [];
    page.setDefaultTimeout(10000);
    await page.clock.install({ time: props.now });
    page.on('pageerror', error => errors.push(error.message));
    let empty = false, fail = false;
    const worker = { ...props.observations[0], schedulingPolicy: 'Disabled', authenticationCredentialStatus: 'active', workerVersion: '0.15.0' };
    const projects = props.projects.map(item => ({ ...item, revision: 1, enabled: true, requirements: [] }));
    await page.route('https://localization.test/**', route => {
      const request = route.request(), url = new URL(request.url()), endpoint = url.pathname;
      if (endpoint.startsWith('/dashboard-assets/preview/')) return route.fulfill({ path: path.join(assets, endpoint.slice('/dashboard-assets/preview/'.length)), contentType: endpoint.endsWith('.css') ? 'text/css' : 'text/javascript' });
      if (/^\/(home|workers|projects|executions|settings)(\/|$)/.test(endpoint)) return route.fulfill({ path: path.join(assets, 'index.html'), contentType: 'text/html' });
      if (endpoint === '/api/v1/administration/session') return route.fulfill({ json: { csrfToken: 'fixture-csrf', expiresAtUtc: '2026-01-01T01:00:00Z' } });
      if (endpoint === '/api/v1/events/stream') return route.fulfill({ status: 503 });
      if (request.method() !== 'GET') { writes.push(endpoint); return route.fulfill({ status: 503 }); }
      const fixtures = {
        '/api/status': { state: 'running', version: 'fixture', startedAtUtc: '2026-01-01T00:00:00Z' },
        '/api/version': { version: '0.15.0' }, '/api/v1/workers': empty ? [] : [worker], '/api/v1/workers/worker-a': worker,
        '/api/v1/nodes': props.nodes, '/api/v1/nodes/server/commands': [], '/api/v1/nodes/worker-a/commands': [], '/api/v1/provisioning': [],
        '/api/v1/projects': empty ? [] : projects, '/api/v1/credentials': [],
        '/api/v1/workers/worker-a/credential-access': { status: 'active' },
        '/api/v1/workers/worker-a/diagnostics': { ...props.diagnostics, canActivate: true, activationBlockingReasons: [], capabilityObservationsCurrent: true, projects: [] },
        '/api/v1/executions': empty ? [] : [...props.executions, { ...props.executions[0], id: 'future', state: 'FutureState', currentStage: 'FutureStage' }],
        '/api/v1/nodes/server/github-connection': { commands: [], provisioningEnabled: false, elevationAllowed: false }
      };
      if (fail && endpoint === '/api/v1/executions') return route.fulfill({ status: 503 });
      assert.ok(Object.hasOwn(fixtures, endpoint), `Unexpected request ${endpoint}`);
      return route.fulfill({ json: fixtures[endpoint] });
    });
    await page.goto('https://localization.test/workers/worker-a?step=preparation');
    await page.getByRole('heading', { name: 'Build Worker North' }).waitFor();
    const time = await page.getByRole('region', { name: 'Server connection' }).filter({ visible: true }).locator('time').textContent();
    await page.getByLabel('Language', { exact: true }).filter({ visible: true }).selectOption('es');
    assert.equal(new URL(page.url()).pathname, '/workers/worker-a');
    assert.equal(new URL(page.url()).search, '?step=preparation');
    await page.getByRole('heading', { name: 'Ejecuciones recientes' }).waitFor();
    assert.equal(await page.getByRole('region', { name: 'Conexión del servidor' }).filter({ visible: true }).locator('time').textContent(), time);
    await page.getByRole('button', { name: 'Activar planificación', exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Cancelar', exact: true }).click();
    await page.getByRole('link', { name: 'Proyectos', exact: true }).filter({ visible: true }).first().click();
    await page.getByRole('heading', { name: 'Proyectos', exact: true }).waitFor();
    await page.getByRole('button', { name: 'Crear proyecto', exact: true }).click();
    const dialog = page.getByRole('dialog');
    await dialog.getByRole('button', { name: 'Siguiente', exact: true }).click();
    await dialog.getByText('Este campo es obligatorio.', { exact: true }).waitFor();
    await dialog.getByLabel('Repositorio (propietario/repositorio)').fill('owner/draft');
    await dialog.getByLabel('Idioma', { exact: true }).selectOption('en');
    assert.equal(await dialog.getByLabel('Repository (owner/repository)').inputValue(), 'owner/draft');
    await dialog.getByLabel('Language', { exact: true }).selectOption('es');
    assert.equal(await dialog.getByLabel('Repositorio (propietario/repositorio)').inputValue(), 'owner/draft');
    await dialog.getByRole('button', { name: 'Cerrar', exact: true }).click();
    await page.reload();
    await page.getByRole('heading', { name: 'Proyectos', exact: true }).waitFor();
    assert.equal(await page.locator('html').getAttribute('lang'), 'es');
    assert.deepEqual(await page.evaluate(() => JSON.parse(localStorage.getItem('codex-dashboard-preferences')).state), { theme: 'dark', language: 'es' });
    await page.getByRole('link', { name: 'Ejecuciones', exact: true }).filter({ visible: true }).first().click();
    await page.getByText('FutureState · FutureStage', { exact: true }).waitFor();
    const filter = page.getByLabel('Estado');
    await filter.selectOption('Running');
    assert.equal(new URL(page.url()).searchParams.get('state'), 'Running');
    assert.equal(await filter.locator('option[value="Running"]').textContent(), 'En ejecución');
    fail = true;
    await page.getByRole('button', { name: 'Actualizar ejecuciones' }).click();
    await page.getByText('Ejecuciones no disponibles. Actualice para obtener datos actuales.', { exact: true }).waitFor();
    fail = false; empty = true;
    await page.getByRole('button', { name: 'Actualizar ejecuciones' }).click();
    await page.getByText('No hay solicitudes de ejecución coincidentes.', { exact: true }).waitFor();
    await page.getByRole('link', { name: 'Ajustes', exact: true }).filter({ visible: true }).first().click();
    await page.getByRole('button', { name: 'Añadir credencial', exact: true }).click();
    const credential = page.getByRole('dialog');
    await credential.getByLabel('Proveedor').fill('draft-provider');
    await credential.getByLabel('Idioma', { exact: true }).selectOption('en');
    assert.equal(await credential.getByLabel('Provider').inputValue(), 'draft-provider');
    await credential.getByRole('button', { name: 'Cancel', exact: true }).click();
    await page.setViewportSize({ width: 375, height: 900 });
    await page.getByRole('button', { name: 'Expand navigation menu' }).click();
    await page.getByLabel('Language', { exact: true }).filter({ visible: true }).selectOption('es');
    assert.equal(await page.getByRole('button', { name: 'Cerrar menú de navegación', exact: true }).count(), 1);
    await page.keyboard.press('Escape');
    assert.equal(writes.length, 0); assert.deepEqual(errors, []);
    console.log('Localization browser review passed: routes, drafts/dialogs, reload, timestamps, status values, errors/empty states, mobile navigation.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
