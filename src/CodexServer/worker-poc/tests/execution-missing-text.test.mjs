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
  assert.match(i18n, /\(key \? resources\[language\]\[key\] : undefined\)\s*\?\?\s*resources\[language\]\['shared\.unknown'\]/);
  assert.match(detail, /t\(`maintenance\.classification\.\$\{attention\.classification\}` as TranslationKey\)/);
  assert.match(read('shared/locales/en.ts'), /"shared\.unknown":/);
  assert.match(read('shared/locales/es.ts'), /"shared\.unknown":/);
});

test('Execution detail shows localized unavailable text for absent optional stage and summary fields', () => {
  const detail = read('features/executions/ExecutionsPage.tsx');
  assert.match(detail, /item\.currentStage \? localizeText\(item\.currentStage\) : t\('executions\.notReported'\)/);
  assert.match(detail, /item\.completionSummary \|\| t\('executions\.noCompletionSummaryReported'\)/);
});

test('failed Execution detail uses a localized fallback for missing, null, or empty reasons', () => {
  const detail = read('features/executions/ExecutionsPage.tsx');
  const contract = read('shared/api/contracts.ts');
  const validation = read('shared/api/validation.ts');
  const english = read('shared/locales/en.ts');
  const spanish = read('shared/locales/es.ts');

  // The API's optional completionSummary is the available failure explanation;
  // a missing or blank value gets an explicit localized fallback.
  assert.match(contract, /completionSummary\?: string/);
  assert.match(validation, /'completionSummary'/);
  assert.match(detail, /item\.state === 'Failed' \? item\.completionSummary\?\.trim\(\) \? item\.completionSummary : t\('executions\.failureReasonUnavailable'\)/);
  assert.match(english, /"executions\.failureReasonUnavailable": "Failure reason unavailable"/);
  assert.match(spanish, /"executions\.failureReasonUnavailable": "Motivo del fallo no disponible"/);
});

test('missing translator input falls back safely and failed detail retains optional activity time', () => {
  const i18n = read('shared/i18n.ts');
  const detail = read('features/executions/ExecutionsPage.tsx');

  assert.match(i18n, /key: TranslationKey \| null \| undefined/);
  assert.match(i18n, /\(key \? resources\[language\]\[key\] : undefined\) \?\? resources\[language\]\['shared\.unknown'\]/);
  assert.match(detail, /attention\.lastActivityAtUtc \? ` · \$\{timestamp\(attention\.lastActivityAtUtc\)\}` : ''/);
});
