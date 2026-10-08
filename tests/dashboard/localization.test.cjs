const { test, after, afterEach } = require('node:test');
const assert = require('node:assert/strict');
const { createRequire } = require('node:module');
const { mkdtempSync, rmSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join, resolve } = require('node:path');
const frontend = resolve('src/CodexServer/worker-poc'), requireFrontend = createRequire(join(frontend, 'package.json'));
const { buildSync } = requireFrontend('esbuild');
const directory = mkdtempSync(join(tmpdir(), 'dashboard-localization-'));
buildSync({ stdin: { contents: `export * from './src/shared/i18n'; export * from './src/shared/preferences'; export * from './src/shared/Shell'; export * from './src/detail'; export * from './src/model'; export * from './src/features/executions/model';`, resolveDir: frontend }, bundle: true, platform: 'node', format: 'cjs', jsx: 'automatic', alias: { '@': join(frontend, 'src/untitled') }, external: ['react', 'react/jsx-runtime', 'react-dom', 'react-dom/server', 'react-aria-components'], outfile: join(directory, 'localization.cjs') });
// External React resolves from the product package, sharing the renderer's hook owner.
const Module = require('node:module');
const fs = require('node:fs');
const compiled = new Module(join(frontend, 'localization-test.cjs'));
compiled.paths = Module._nodeModulePaths(frontend);
compiled._compile(fs.readFileSync(join(directory, 'localization.cjs'), 'utf8'), compiled.id);
const { t, localizeText, statusLabel, resources, usePreferences, createPreferences, Application, WorkerDetail, timestamp, presentation } = compiled.exports;
const { renderToStaticMarkup } = requireFrontend('react-dom/server');
const { createElement } = requireFrontend('react');
const { props } = require('./worker-poc-fixtures.cjs');
after(() => rmSync(directory, { recursive: true, force: true }));
afterEach(() => usePreferences.getState().setLanguage('en'));
test('language preference defaults to English and persists only allowlisted browser preferences', () => {
  let saved = null;
  const storage = { getItem: () => saved, setItem: (_, value) => { saved = value; }, removeItem() {} };
  const first = createPreferences(storage);
  assert.equal(first.getState().language, 'en');
  first.getState().setLanguage('es');
  first.getState().setTheme('light');
  assert.deepEqual(JSON.parse(saved), { state: { theme: 'light', language: 'es' }, version: 1 });
  assert.equal(createPreferences(storage).getState().language, 'es');
  for (const value of ['{', JSON.stringify({ version: 2, state: { language: 'es' } }), JSON.stringify({ version: 1, state: { language: 'fr', secret: 'discarded' } })]) {
    saved = value; const state = createPreferences(storage).getState();
    assert.equal(state.language, 'en'); assert.equal('secret' in state, false);
  }
  const unavailable = createPreferences({ getItem() { throw Error('denied'); }, setItem() { throw Error('denied'); }, removeItem() {} });
  unavailable.getState().setLanguage('es'); assert.equal(unavailable.getState().language, 'es');
});
test('catalog completeness and interpolation stay aligned in both languages', () => {
  assert.deepEqual(Object.keys(resources.en).sort(), Object.keys(resources.es).sort());
  for (const key of Object.keys(resources.en)) {
    assert.ok(resources.es[key].trim(), key);
    assert.deepEqual(resources.en[key].match(/\{\w+\}/g)?.sort(), resources.es[key].match(/\{\w+\}/g)?.sort(), key);
  }
  usePreferences.getState().setLanguage('es');
  assert.equal(t('projects.saved', { name: 'My project' }).includes('«My project» guardado'), true);
});
test('switching localizes representative shell, worker, errors and dialog copy while preserving unknown API values', () => {
  const shell = () => renderToStaticMarkup(createElement(Application, { session: { authenticated: false, message: 'Your session expired. Sign in again.' }, children: null }));
  assert.match(shell(), /Administration sign in/);
  usePreferences.getState().setLanguage('es');
  const html = shell();
  for (const text of ['Inicio', 'Proyectos', 'Ejecuciones', 'Ajustes', 'Idioma', 'Inicio de sesión de administración', 'Su sesión caducó', 'Iniciar sesión']) assert.ok(html.includes(text), text);
  assert.ok(!html.includes('Administration sign in'));
  const worker = renderToStaticMarkup(createElement(WorkerDetail, props));
  for (const text of ['Detalle del agente', 'Preparación', 'Ejecuciones recientes', 'Validación']) assert.ok(worker.includes(text), text);
  assert.equal(t('executions.cancelOnlyThisQueuedRequestAssignedAndRunningWorkCannotBeCancelled').startsWith('Cancele solo'), true);
  assert.equal(t('shared.cancel'), 'Cancelar');
  assert.equal(statusLabel('NeedsReprovision'), 'Requiere reaprovisionamiento');
  assert.equal(statusLabel('FutureState<unsafe>'), 'FutureState<unsafe>');
  assert.equal(presentation({ state: 'FutureState', currentStage: 'FutureStage' }).text, 'FutureState · FutureStage');
  usePreferences.getState().setLanguage('en');
  assert.match(shell(), /Administration sign in/);
});
test('retained messages switch both ways and timestamps use the same local timezone and fixed format', () => {
  const time = timestamp('2026-01-03T04:05:06Z');
  const message = t('projects.saved', { name: 'Home' });
  usePreferences.getState().setLanguage('es');
  assert.match(localizeText(message), /«Home» guardado/);
  const spanish = localizeText(message);
  assert.equal(timestamp('2026-01-03T04:05:06Z'), time);
  assert.match(time, /^\d{2}\/\d{2}\/2026, \d{2}:05:06$/);
  assert.equal(timestamp('invalid'), 'No informado');
  usePreferences.getState().setLanguage('en');
  assert.equal(localizeText(spanish), message);
});
test('literal translation keys in production sources exist, including JavaScript presentation modules', () => {
  const ts = requireFrontend('typescript');
  function inspect(directory) {
    for (const item of fs.readdirSync(directory, { withFileTypes: true })) {
      const file = join(directory, item.name);
      if (item.isDirectory()) { if (!['untitled', 'locales'].includes(item.name)) inspect(file); continue; }
      if (!/\.(tsx?|jsx?)$/.test(file)) continue;
      const source = ts.createSourceFile(file, fs.readFileSync(file, 'utf8'), ts.ScriptTarget.Latest, true, file.endsWith('x') ? ts.ScriptKind.TSX : ts.ScriptKind.TS);
      function visit(node) {
        if (ts.isCallExpression(node) && node.expression.getText(source) === 't' && node.arguments[0] && ts.isStringLiteral(node.arguments[0])) assert.ok(Object.hasOwn(resources.en, node.arguments[0].text), `${file}: ${node.arguments[0].text}`);
        ts.forEachChild(node, visit);
      }
      visit(source);
    }
  }
  inspect(join(frontend, 'src'));
});
