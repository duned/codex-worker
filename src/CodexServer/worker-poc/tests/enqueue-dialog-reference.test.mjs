import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const sourceRoot = new URL('../src/', import.meta.url);
const read = path => readFileSync(new URL(path, sourceRoot), 'utf8');
const reference = readFileSync(new URL('../../../../docs/mockups/enqueue-issue-dialog-approved.svg', import.meta.url), 'utf8');
const styles = read('poc.css');
const dialog = read('shared/Dialogs.tsx');
const issues = read('features/projects/Issues.tsx');
const workspace = read('features/projects/Workspace.tsx');
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

test('enqueue dialog scales the approved visual reference to a conventional centered modal', () => {
  const frame = reference.match(/<rect x="(\d+)" y="(\d+)" width="(\d+)" height="(\d+)" rx="(\d+)" fill="(#[0-9A-Fa-f]+)" stroke="(#[0-9A-Fa-f]+)" stroke-width="(\d+)"/);
  assert.ok(frame, 'Approved SVG has a modal frame');
  const [, , , , , , fill, border] = frame;
  const modal = rule('.enqueue-dialog-modal');
  const overlay = rule('.enqueue-dialog-overlay');
  const body = rule('.enqueue-dialog');
  const title = rule('.enqueue-dialog-title');
  const project = rule('.enqueue-dialog-project');
  const description = rule('.enqueue-dialog-description');
  const close = rule('.enqueue-dialog-close');
  const actions = rule('.enqueue-dialog-actions');
  const cancel = rule('.enqueue-dialog-cancel');
  const submit = rule('.enqueue-dialog-submit');

  assert.equal(cssValue(modal, 'width'), 'min(680px, calc(100vw - 32px))');
  assert.equal(cssValue(modal, 'max-height'), 'calc(100dvh - 32px)');
  assert.equal(cssValue(modal, 'border-radius'), '16px');
  assert.equal(cssValue(modal, 'border'), `1px solid ${border}`);
  assert.equal(cssValue(modal, 'background').toLowerCase(), fill.toLowerCase());
  assert.match(cssValue(overlay, 'background'), /rgb\(0 0 0 \/ 72%\)/);
  assert.equal(cssValue(overlay, 'backdrop-filter'), 'blur(4px)');
  assert.equal(cssValue(body, 'padding'), '24px');
  assert.equal(cssValue(title, 'font-size'), '22px');
  assert.equal(cssValue(title, 'font-weight'), '700');
  assert.equal(cssValue(project, 'font-size'), '17px');
  assert.equal(cssValue(project, 'font-weight'), '700');
  assert.equal(cssValue(description, 'font-size'), '15px');
  assert.equal(cssValue(description, 'line-height'), '1.55');
  assert.equal(cssValue(close, 'width'), '36px');
  assert.equal(cssValue(actions, 'gap'), '12px');
  assert.equal(cssValue(cancel, 'min-height'), '40px');
  assert.equal(cssValue(submit, 'min-height'), '40px');
  assert.equal(cssValue(submit, 'border-radius'), '8px');
  assert.equal(cssValue(submit, 'background'), '#1c142a');
  assert.doesNotMatch(styles, /min\(1534px|min\(450px|font-size: 30px|min-height: 100px/);
  assert.match(styles, /@media \(max-width: 600px\)/);
  assert.match(styles, /@media \(max-width: 380px\)/);
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
  assert.match(issues, /if \(!d \|\| guard\.current \|\| locked\) return; guard\.current = true; setBusy\(true\)/);
  assert.match(issues, /finally \{ guard\.current = false; setBusy\(false\); \}/);
  assert.match(issues, /role="status" className="enqueue-dialog-feedback enqueue-dialog-feedback-pending"/);
  assert.match(issues, /role="alert" className="enqueue-dialog-feedback enqueue-dialog-feedback-error"/);
  assert.match(issues, /className="enqueue-dialog-execution-link" to=\{`\/executions\/\$\{encodeURIComponent\(accepted\.id\)\}`\}/);
  assert.match(styles, /enqueue-dialog-close:focus-visible/);
});

test('enqueue close controls share cleanup and visibility follows the retained draft state', () => {
  const closeDialog = issues.match(/function closeDialog\(\) \{([\s\S]*?)\n  \}/)?.[1];
  assert.ok(closeDialog, 'closeDialog is defined');
  assert.match(closeDialog, /if\s*\(\s*busy\s*\)\s*return/);
  assert.match(closeDialog, /setError\(\s*''\s*\)/);
  assert.match(closeDialog, /setDiscard\(\s*false\s*\)/);
  assert.match(closeDialog, /setAccepted\(\s*undefined\s*\)/);
  assert.match(closeDialog, /if\s*\(\s*accepted\s*\)\s*w\.setIssueDraft\(\s*undefined\s*\)/);
  assert.match(closeDialog, /else\s*w\.setIssueDraft\(\s*(?:current\s*=>\s*current\s*\?\s*\{\s*\.\.\.current\s*,\s*open:\s*false\s*\}\s*:\s*current|\{\s*\.\.\.d\s*,\s*open:\s*false\s*\})\s*\)/);
  assert.match(issues, /<FormDialog isOpen=\{d\.open\}[^>]*onClose=\{closeDialog\}/);
  assert.match(issues, /className=\{change\.kind === 'enqueue' \? 'enqueue-dialog-cancel' : undefined\} isDisabled=\{busy\} onPress=\{closeDialog\}/);
  assert.match(issues, /<Button type="submit" className=\{change\.kind === 'enqueue' \? 'enqueue-dialog-submit' : undefined\}/);
  assert.match(workspace, /accepted\?: ExecutionSummary/);
  assert.match(issues, /w\.setIssueDraft\(\{ \.\.\.d, accepted: execution \}\)/);
  assert.doesNotMatch(closeDialog, /submitIssue|submit\(\)/);
});
