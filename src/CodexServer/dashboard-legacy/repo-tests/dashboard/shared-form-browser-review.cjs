// Deterministic composition fixture for the shared form pattern, using production source/CSS.
const assert = require('node:assert/strict');
const { createRequire } = require('node:module');
const { resolve, join } = require('node:path');
const { chromium } = require('playwright');
const frontend = resolve('src/CodexServer/worker-poc');
const { buildSync } = createRequire(join(frontend, 'package.json'))('esbuild');
const script = buildSync({ stdin: { resolveDir: frontend, loader: 'tsx', contents: `
import { useState } from 'react';
import { createRoot } from 'react-dom/client';
import { ActionDialog } from './src/shared/Dialogs';
import { PageHeading, StatusBadge, ViewState, ResourceIdentity } from './src/shared/Presentation';
import { Input } from './src/untitled/components/base/input/input';
import { Button } from './src/untitled/components/base/buttons/button';
function Fixture() {
  const [open, setOpen] = useState(false);
  return <main className="space-y-5 p-4 text-primary">
    <PageHeading title="Shared form fixture" breadcrumbs={[{ label: 'Settings', href: '/settings' }, { label: 'Form' }]} />
    <ResourceIdentity name="Build Worker North" id="worker-a" />
    <div className="flex flex-wrap gap-2">{['gray', 'success', 'warning', 'error'].map((tone, index) => <StatusBadge key={tone} tone={tone}>{['Unknown', 'Connected', 'Stale', 'Failed'][index]}</StatusBadge>)}</div>
    <ViewState title="No observations yet">Refresh authoritative state to retrieve current observations.</ViewState>
    <Button onPress={() => setOpen(true)}>Open form</Button>
    {open && <ActionDialog isOpen title="Update contact" description="Save the entered contact after reviewing this change." actionLabel="Save contact" onClose={() => setOpen(false)} onSubmit={async () => {
      window.submissions = (window.submissions || 0) + 1;
      await new Promise(resolve => { window.releaseSubmission = resolve; });
      if (window.rejectSubmission) throw Error('private response body');
    }}><Input name="contact" label="Contact email" type="email" isRequired /></ActionDialog>}
  </main>;
}
createRoot(document.getElementById('fixture')).render(<Fixture />);
` }, bundle: true, write: false, format: 'iife', jsx: 'automatic', alias: { '@': join(frontend, 'src/untitled') }, define: { 'process.env.NODE_ENV': '"production"' } }).outputFiles[0].text;
const fs = require('node:fs');
const assets = resolve('src/CodexServer/obj/worker-poc/preview');
const css = JSON.parse(fs.readFileSync(join(assets, 'assets.json'))).find(asset => asset.path.endsWith('.css')).path;
(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage();
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.route('https://form.test/**', route => {
      const url = new URL(route.request().url());
      if (url.pathname === '/fixture.js') return route.fulfill({ body: script, contentType: 'text/javascript' });
      if (url.pathname === '/theme.js') return route.fulfill({ path: join(frontend, 'public/assets/theme.js'), contentType: 'text/javascript' });
      if (url.pathname === '/fixture.css') return route.fulfill({ path: join(assets, css), contentType: 'text/css' });
      return route.fulfill({ contentType: 'text/html', body: '<!doctype html><html lang="en" class="dark-mode"><head><meta name="viewport" content="width=device-width, initial-scale=1"><script src="/theme.js"></script><link rel="stylesheet" href="/fixture.css"></head><body><div id="fixture"></div><script src="/fixture.js"></script></body></html>' });
    });
    for (const theme of ['dark', 'light']) for (const width of [1280, 375, 640]) {
      await page.setViewportSize({ width, height: 900 }); await page.goto('https://form.test/');
      await page.evaluate(theme => localStorage.setItem('codex-dashboard-preferences', JSON.stringify({ version: 1, state: { theme } })), theme); await page.reload();
      await page.getByRole('button', { name: 'Open form' }).click();
      await page.getByRole('button', { name: 'Cancel', exact: true }).click();
      assert.equal(await page.evaluate(() => window.submissions || 0), 0);
      await page.getByRole('button', { name: 'Open form' }).click();
      await page.getByRole('button', { name: 'Save contact' }).click();
      assert.equal(await page.evaluate(() => window.submissions || 0), 0, 'Required field prevents submission.');
      await page.getByLabel('Contact email').fill('invalid');
      await page.getByRole('button', { name: 'Save contact' }).click();
      assert.equal(await page.evaluate(() => window.submissions || 0), 0, 'Invalid field prevents submission.');
      await page.getByLabel('Contact email').fill('contact@example.test');
      await page.getByRole('button', { name: 'Save contact' }).click();
      await page.getByText('Submitting…', { exact: true }).waitFor();
      await page.keyboard.press('Enter'); await page.keyboard.press('Escape');
      assert.equal(await page.getByRole('dialog').count(), 1);
      assert.equal(await page.getByRole('button', { name: 'Save contact' }).isDisabled(), true);
      assert.equal(await page.evaluate(() => window.submissions), 1);
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      await page.evaluate(() => window.releaseSubmission());
      await page.getByRole('dialog').waitFor({ state: 'hidden' });
      await page.waitForFunction(() => document.activeElement?.textContent === 'Open form');
    }
    await page.getByRole('button', { name: 'Open form' }).click();
    await page.getByLabel('Contact email').fill('contact@example.test');
    await page.getByRole('button', { name: 'Save contact' }).click();
    await page.evaluate(() => { window.rejectSubmission = true; window.releaseSubmission(); });
    await page.getByRole('alert').waitFor();
    assert.equal(await page.getByRole('button', { name: 'Save contact' }).isDisabled(), true);
    assert.ok(!(await page.locator('body').innerText()).includes('private response body'));
    assert.deepEqual(errors, []);
    console.log('Shared form browser review passed: dark/light desktop/mobile/zoom layout, validation, cancel, pending, exactly-once acceptance, focus restoration and safe rejection.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
