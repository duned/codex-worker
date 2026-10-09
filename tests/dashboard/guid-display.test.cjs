const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '../../src/CodexServer/worker-poc/src');
const source = relative => fs.readFileSync(path.join(root, relative), 'utf8');

test('shared GUID formatter consistently exposes the full value accessibly', () => {
  const component = source('shared/GuidDisplay.tsx');
  assert.match(component, /return value\.slice\(0, 7\)/);
  assert.match(component, /title=\{value\}/);
  assert.match(component, /aria-label=\{t\('shared\.shortGuidAccessible'/);
  assert.match(source('shared/locales/en.ts'), /"shared\.shortGuidAccessible"/);
  assert.match(source('shared/locales/es.ts'), /"shared\.shortGuidAccessible"/);
});

test('execution list surfaces link the shared short identifier to its detail route', () => {
  const surfaces = [
    'features/home/HomePage.tsx',
    'features/projects/Detail.tsx',
    'features/executions/ExecutionsPage.tsx',
    'detail.jsx'
  ];
  for (const file of surfaces) {
    const text = source(file);
    assert.match(text, /<GuidDisplay value=\{item\.id\} \/>/, `${file} should use shared execution GUID display`);
    assert.match(text, /executions\/\$\{encodeURIComponent\(item\.id\)\}/, `${file} should link to the full execution ID`);
  }
});
