// Real publish/archive regression, deliberately separate from the stub release tests.
// Build a fresh reference with worker-poc/build.mjs before invoking this script.
// Usage: node tests/dashboard/worker-poc-artifact-review.cjs PUBLISHED_DIRECTORY FRESH_ASSETS
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const net = require('node:net');
const { spawn } = require('node:child_process');
const { once } = require('node:events');

(async () => {
  const [artifact, expected] = process.argv.slice(2).map(value => path.resolve(value));
  assert.ok(artifact && expected, 'Published directory and freshly built asset directory are required.');
  assert.equal(fs.existsSync(path.join(artifact, 'worker-poc')), false, 'Frontend source must not be published.');
  assert.equal(fs.existsSync(path.join(artifact, 'node_modules')), false, 'No runtime npm dependencies.');
  const listener = net.createServer(); listener.listen(0, '127.0.0.1'); await once(listener, 'listening');
  const port = listener.address().port; await new Promise(resolve => listener.close(resolve));
  const state = fs.mkdtempSync(path.join(os.tmpdir(), 'poc-artifact-'));
  // Node/npm are absent from the child PATH. No management credentials are needed.
  const child = spawn(path.join(artifact, 'CodexServer'), [`--Server:ListenUrl=http://127.0.0.1:${port}`, `--Server:DataDirectory=${state}`], {
    cwd: artifact, env: { ...process.env, PATH: '/nonexistent', CODEX_SERVER_MANAGEMENT_TOKEN: '' }, stdio: 'ignore'
  });
  let startupError; child.on('error', error => { startupError = error; });
  const exited = once(child, 'exit');
  try {
    const origin = `http://127.0.0.1:${port}`;
    const read = resource => fetch(origin + resource, { signal: AbortSignal.timeout(5000) });
    const deadline = Date.now() + 30000;
    let ready = false;
    while (Date.now() < deadline) {
      if (startupError) throw startupError;
      assert.equal(child.exitCode, null, 'Published Server exited before readiness.');
      try { ready = (await fetch(origin + '/health', { signal: AbortSignal.timeout(1000) })).ok; } catch { /* bounded startup wait */ }
      if (ready) break;
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    assert.ok(ready, 'Published Server did not start within 30 seconds.');
    const route = await read('/workers/fixture-worker/poc');
    assert.equal(route.status, 200); const html = await route.text();
    assert.ok(html.includes('id="worker-poc"') && html.includes('id="poc-legacy-owner"'));
    assert.ok(!html.includes('--ink:#183135'), 'Legacy theme must not ship on the PoC route.');
    for (const extension of ['js', 'css']) {
      assert.ok(html.includes(`/dashboard-assets/worker-poc.${extension}`));
      const response = await read(`/dashboard-assets/worker-poc.${extension}`);
      assert.equal(response.status, 200);
      assert.ok(await response.text() === fs.readFileSync(path.join(expected, `poc.${extension}`), 'utf8'), `Missing or stale embedded ${extension}`);
    }
    const preview = await read('/dashboard-preview/workers/fixture-worker?step=preparation&project=fixture-project');
    assert.equal(preview.status, 200);
    const previewHtml = await preview.text();
    assert.equal(previewHtml, fs.readFileSync(path.join(expected, 'preview/index.html'), 'utf8'), 'Stale preview shell.');
    const manifest = JSON.parse(fs.readFileSync(path.join(expected, 'preview/assets.json'), 'utf8'));
    for (const asset of manifest) {
      const response = await read('/dashboard-assets/preview/' + asset.path);
      assert.equal(response.status, 200);
      assert.deepEqual(Buffer.from(await response.arrayBuffer()), fs.readFileSync(path.join(expected, 'preview', asset.path)), `Stale preview asset ${asset.path}`);
    }
    for (const resource of ['/dashboard-preview/unknown', '/dashboard-preview/home/extra', '/dashboard-preview/api/v1/workers', '/dashboard-preview/health', '/dashboard-assets/preview/assets/unknown.js'])
      assert.equal((await read(resource)).status, 404);
    const previewPost = await fetch(origin + '/dashboard-preview/workers/fixture-worker', { method: 'POST' });
    assert.equal(previewPost.status, 405);
    assert.ok(!(await (await read('/workers')).text()).includes('/dashboard-assets/worker-poc.'));
    assert.ok(!(await (await read('/workers')).text()).includes('/dashboard-assets/preview/'));
    assert.equal((await read('/dashboard-assets/unknown.js')).status, 404);
    assert.equal((await read('/api/v1/workers/fixture-worker/diagnostics')).status, 401);
    console.log('Published artifact: current local JS/CSS, isolated React route, API authorization and no Node/npm runtime passed.');
  } finally {
    child.kill('SIGTERM');
    await Promise.race([exited, new Promise(resolve => setTimeout(resolve, 5000))]);
    if (child.exitCode === null && child.signalCode === null) { child.kill('SIGKILL'); await exited; }
    fs.rmSync(state, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
