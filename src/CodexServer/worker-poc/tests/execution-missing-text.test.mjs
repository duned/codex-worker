import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const sourceRoot = new URL('../src/', import.meta.url);
const read = path => readFileSync(new URL(path, sourceRoot), 'utf8');

test('Execution detail keeps rendering when a server-derived translation key has no text', () => {
  const i18n = read('shared/i18n.ts');
  const detail = read('features/executions/ExecutionsPage.tsx');

  // Execution status/classification text can be derived from server data, so
  // the key may have no matching resource even though the static type is cast.
  assert.match(i18n, /resources\[language\]\[key\]\s*\?\?\s*resources\[language\]\['shared\.unknown'\]/);
  assert.match(detail, /t\(`maintenance\.classification\.\$\{attention\.classification\}` as TranslationKey\)/);
  assert.match(read('shared/locales/en.ts'), /"shared\.unknown":/);
  assert.match(read('shared/locales/es.ts'), /"shared\.unknown":/);
});

test('Execution detail shows localized unavailable text for absent optional stage and summary fields', () => {
  const detail = read('features/executions/ExecutionsPage.tsx');
  assert.match(detail, /item\.currentStage \? localizeText\(item\.currentStage\) : t\('executions\.notReported'\)/);
  assert.match(detail, /item\.completionSummary \|\| t\('executions\.noCompletionSummaryReported'\)/);
});
