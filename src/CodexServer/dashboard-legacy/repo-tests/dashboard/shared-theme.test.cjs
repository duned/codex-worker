const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const vm = require('node:vm');
const bootstrap = readFileSync('src/CodexServer/worker-poc/public/assets/theme.js', 'utf8');
function theme(value, unavailable = false) {
  const root = { style: {}, classList: { toggle: (name, enabled) => { root.dark = enabled; } } };
  vm.runInNewContext(bootstrap, { document: { documentElement: root }, localStorage: { getItem() { if (unavailable) throw Error('Denied'); return value; } } });
  return { dark: root.dark, colorScheme: root.style.colorScheme };
}
test('pre-paint theme defaults to dark without consulting OS and tolerates unavailable/corrupt storage', () => {
  for (const value of [null, '{}', '{', JSON.stringify({ version: 2, state: { theme: 'light' } }), JSON.stringify({ version: 1, state: { theme: 'system' } })]) {
    assert.deepEqual(theme(value), { dark: true, colorScheme: 'dark' });
  }
  assert.deepEqual(theme(null, true), { dark: true, colorScheme: 'dark' });
});
test('pre-paint theme honors only the preference owner versioned explicit light selection', () => {
  assert.deepEqual(theme(JSON.stringify({ version: 1, state: { theme: 'light', token: 'ignored' } })), { dark: false, colorScheme: 'light' });
});
