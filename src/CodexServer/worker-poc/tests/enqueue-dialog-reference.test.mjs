import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const sourceRoot = new URL('../src/', import.meta.url);
const read = path => readFileSync(new URL(path, sourceRoot), 'utf8');
const reference = readFileSync(new URL('../../../../docs/mockups/enqueue-issue-dialog-approved.svg', import.meta.url), 'utf8');
const styles = read('poc.css');
const dialog = read('shared/Dialogs.tsx');
const issues = read('features/projects/Issues.tsx');
const english = read('shared/locales/en.ts');
const spanish = read('shared/locales/es.ts');

function rule(selector) {
  const start = styles.indexOf(`${selector} {`);
  assert.notEqual(start, -1, `Missing ${selector} style rule`);
  const end = styles.indexOf('}', start);
  return styles.slice(start, end);
}

function cssValue(block, property) {
  return block.match(new RegExp(`(?:^|[;{]\\s*)${property}:\\s*([^;]+)`))?.[1]?.trim();
}

test('enqueue desktop geometry and colors follow the approved SVG', () => {
  const frame = reference.match(/<rect x="(\d+)" y="(\d+)" width="(\d+)" height="(\d+)" rx="(\d+)" fill="(#[0-9A-Fa-f]+)" stroke="(#[0-9A-Fa-f]+)" stroke-width="(\d+)"/);
  assert.ok(frame, 'Approved SVG has a modal frame');
  const [, x, y, width, height, radius, fill, border, borderWidth] = frame;
  const modal = rule('.enqueue-dialog-modal');
  const body = rule('.enqueue-dialog');
  const title = rule('.enqueue-dialog-title');
  const project = rule('.enqueue-dialog-project');
  const description = rule('.enqueue-dialog-description');
  const close = rule('.enqueue-dialog-close');
  const actions = rule('.enqueue-dialog-actions');
  const cancel = rule('.enqueue-dialog-cancel');
  const submit = rule('.enqueue-dialog-submit');

  assert.equal(cssValue(modal, 'width'), `min(${width}px, calc(100vw - 56px))`);
  assert.equal(cssValue(modal, 'min-height'), `min(${height}px, calc(100dvh - 48px))`);
  assert.equal(cssValue(modal, 'border-radius'), `${radius}px`);
  assert.equal(cssValue(modal, 'border'), `${borderWidth}px solid ${border}`);
  assert.equal(cssValue(modal, 'background').toLowerCase(), fill.toLowerCase());
  assert.equal(cssValue(modal, 'box-shadow'), 'none');
  assert.equal(cssValue(modal, 'transform'), 'translateY(-1px)');
  assert.equal(cssValue(body, 'padding'), '36px 31px 31px');
  assert.equal(cssValue(title, 'font-size'), '36px');
  assert.equal(cssValue(title, 'font-weight'), '700');
  assert.equal(cssValue(project, 'font-size'), '31px');
  assert.equal(cssValue(project, 'font-weight'), '700');
  assert.equal(cssValue(description, 'font-size'), '30px');
  assert.equal(cssValue(description, 'line-height'), '1.7');
  assert.equal(cssValue(close, 'top'), '36px');
  assert.equal(cssValue(actions, 'gap'), '64px');
  assert.equal(cssValue(cancel, 'font-size'), '30px');
  assert.equal(cssValue(submit, 'width'), '241px');
  assert.equal(cssValue(submit, 'min-height'), '100px');
  assert.equal(cssValue(submit, 'border-radius'), '18px');
  assert.equal(cssValue(submit, 'background'), '#1c142a');

  const desktopX = (1596 - (Number(width) + 6)) / 2;
  const desktopY = (500 - Number(height)) / 2 - 1;
  assert.equal(desktopX, Number(x));
  assert.ok(Math.abs(desktopY - Number(y)) <= 1, `Modal top differs from SVG by ${desktopY - Number(y)}px`);
  assert.equal(desktopX + Number(width), 1562);
  assert.equal(desktopX + Number(borderWidth) + 31, 61);
  assert.equal(desktopX + Number(width) - Number(borderWidth) - 31, 1529);
});

test('enqueue content stays localized and keeps project identity separate from repository metadata', () => {
  assert.match(issues, /variant=\{change\.kind === 'enqueue' \? 'enqueue' : 'default'\}/);
  assert.match(issues, /projectName=\{change\.kind === 'enqueue' \? d\.project\.name : undefined\}/);
  assert.match(issues, /description=\{change\.kind === 'enqueue' \? t\('projects\.enqueueDescription'\)/);
  assert.ok(dialog.indexOf('slot="title"') < dialog.indexOf('className="enqueue-dialog-project"'));
  assert.ok(dialog.indexOf('className="enqueue-dialog-project"') < dialog.indexOf('className={isEnqueue ? \'enqueue-dialog-description\''));
  assert.doesNotMatch(dialog, /LanguageControl|shared\.language/);
  assert.match(read('shared/Shell.tsx'), /<LanguageControl\s*\/>/);
  assert.match(english, /"projects\.enqueueDescription": "Request a new execution for this Issue\. The Server will check whether it can run and whether a Worker is available\."/);
  assert.match(spanish, /"projects\.enqueueDescription": "Solicita una nueva ejecución de esta Issue\. El Server comprobará si puede ejecutarse y si hay un Worker disponible\."/);
  assert.match(dialog, /"shared\.closeDialog"|t\('shared\.closeDialog'\)/);
});

test('enqueue retains keyboard, pending, rejection and safe navigation behavior', () => {
  assert.match(dialog, /isDismissable=\{!pending\}/);
  assert.match(dialog, /isKeyboardDismissDisabled=\{pending\}/);
  assert.match(dialog, /isDisabled=\{pending\} onPress=\{onClose\}/);
  assert.match(issues, /guard\.current \|\| locked/);
  assert.match(issues, /role="status" className="enqueue-dialog-feedback enqueue-dialog-feedback-pending"/);
  assert.match(issues, /role="alert" className="enqueue-dialog-feedback enqueue-dialog-feedback-error"/);
  assert.match(issues, /className="enqueue-dialog-execution-link" to=\{`\/executions\/\$\{encodeURIComponent\(accepted\.id\)\}`\}/);
  assert.match(styles, /@media \(max-width: 600px\)/);
  assert.match(styles, /@media \(max-width: 380px\)/);
  assert.match(styles, /enqueue-dialog-close:focus-visible/);
});
