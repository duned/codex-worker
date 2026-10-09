import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { serverStatusPresentation } from '../src/shared/server-status-model.mjs';

const sourceRoot = new URL('../src/', import.meta.url);
const read = path => readFileSync(new URL(path, sourceRoot), 'utf8');

test('Server readiness comes from the status response, not the live event connection', () => {
  assert.deepEqual(serverStatusPresentation({ state: 'ready', version: '0.15.0' }, false, false, 1000), {
    kind: 'ready', tone: 'success', freshness: 'current'
  });
  assert.deepEqual(serverStatusPresentation({ state: 'starting' }, false, false, 1000), {
    kind: 'not-ready', tone: 'warning', freshness: 'current', state: 'starting'
  });
  assert.deepEqual(serverStatusPresentation({ state: 'not-ready' }, false, false, 1000), {
    kind: 'not-ready', tone: 'warning', freshness: 'current', state: 'not-ready'
  });
});

test('failed, unavailable, loading and unknown observations stay distinct', () => {
  assert.deepEqual(serverStatusPresentation(undefined, true, false, 2000), {
    kind: 'unavailable', tone: 'error', freshness: 'stale'
  });
  assert.deepEqual(serverStatusPresentation({ state: 'ready' }, true, false, 2000), {
    kind: 'unavailable', tone: 'error', freshness: 'stale'
  });
  assert.deepEqual(serverStatusPresentation(undefined, true, false, 0), {
    kind: 'unavailable', tone: 'error', freshness: 'unknown'
  });
  assert.deepEqual(serverStatusPresentation(undefined, false, true, 0), {
    kind: 'loading', tone: 'gray', freshness: 'unknown'
  });
  assert.deepEqual(serverStatusPresentation(undefined, false, false, 0), {
    kind: 'unknown', tone: 'gray', freshness: 'unknown'
  });
  assert.equal(serverStatusPresentation({ state: 'offline' }, false, false, 1000).tone, 'error');
});

test('language, theme and sign-out controls keep their accessible actions', () => {
  const language = read('shared/LanguageControl.tsx');
  const theme = read('shared/ThemeControl.tsx');
  const shell = read('shared/Shell.tsx');

  assert.match(language, /aria-label=\{t\('shared\.language'\)\}/);
  assert.match(language, /Globe01/);
  assert.match(language, /value=\{language\} onChange=\{event => setLanguage\(/);
  assert.match(language, /language === 'es' \? 'Español' : 'English'/);
  assert.match(theme, /const Icon = theme === 'dark' \? Sun : Moon01/);
  assert.match(theme, /aria-label=\{t\(theme === 'dark' \? 'shared\.lightMode' : 'shared\.darkMode'\)\}/);
  assert.match(theme, /setTheme\(theme === 'dark' \? 'light' : 'dark'\)/);
  assert.match(shell, /iconLeading=\{<LogOut01/);
  assert.match(shell, /onPress=\{session\.onSignOut\}/);
});

test('sidebar status uses API version and successful fetch time, and localizes both languages', () => {
  const status = read('shared/ServerStatus.tsx');
  const validation = read('shared/api/validation.ts');
  const english = read('shared/locales/en.ts');
  const spanish = read('shared/locales/es.ts');

  assert.match(status, /typeof data\?\.version === 'string'/);
  assert.match(status, /timestamp\(timestampIso\)/);
  assert.match(status, /status\.freshness === 'stale'/);
  assert.match(validation, /optional\(item, \['version'\]\)/);
  for (const source of [english, spanish]) {
    assert.match(source, /"shared\.serverReady"/);
    assert.match(source, /"shared\.serverStatusUnavailable"/);
    assert.match(source, /"shared\.serverStatusLoading"/);
    assert.match(source, /"shared\.updated"/);
    assert.match(source, /"shared\.controlPlane"/);
  }
});
