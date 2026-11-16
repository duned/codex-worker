import test from 'node:test';
import assert from 'node:assert/strict';
import { build } from 'esbuild';

const load = async file => {
  const { outputFiles } = await build({ entryPoints: [new URL(file, import.meta.url).pathname], bundle: true, write: false, format: 'esm', platform: 'node' });
  return import('data:text/javascript;base64,' + Buffer.from(outputFiles[0].text).toString('base64'));
};
const { en } = await load('../src/shared/locales/en.ts'), { es } = await load('../src/shared/locales/es.ts');

test('both languages have the same keys and the same placeholders', () => {
  assert.deepEqual(Object.keys(es).sort(), Object.keys(en).sort());
  const placeholders = text => [...text.matchAll(/\{(\w+)\}/g)].map(match => match[1]).sort();
  for (const key of Object.keys(en)) assert.deepEqual(placeholders(es[key]), placeholders(en[key]), key);
});
