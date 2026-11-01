import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const sourceRoot = new URL('../src/', import.meta.url);
const read = path => readFileSync(new URL(path, sourceRoot), 'utf8');

test('Claude login is a typed node action with a manual vendor terminal handoff', () => {
  const provisioning = read('features/nodes/provisioning.ts');
  const component = read('features/nodes/NodeProvisioning.tsx');
  const validation = read('shared/api/validation.ts');

  assert.match(provisioning, /command: 'Login'/);
  assert.match(component, /action === 'login' && capability\.definition\.id === 'claude-cli' \? t\('nodes\.signInToClaude'\)/);
  assert.match(component, /selectedClaudeLogin \? t\('nodes\.signInToClaude'\)/);
  assert.match(component, /item\.request\.nodeId === nodeId && item\.request\.capabilityId === 'claude-cli' && item\.request\.action === 'Login'/);
  assert.match(component, /ManualVendorCliCompletionRequired/);
  assert.match(component, /commandActive\(item\) \|\| \(item\.request\.nodeId === nodeId/);
  assert.match(component, /claudeManualCompletionRequired/);
  assert.match(read('shared/locales/en.ts'), /claude auth login/);
  assert.match(provisioning, /https:\/\/code\.claude\.com\/docs\/en\/troubleshoot-install/);
  assert.doesNotMatch(component, /claude\.com\/cai\/oauth\/authorize/);
  assert.match(validation, /request\.nodeId === 'server'.*request\.capabilityId !== 'claude-cli'.*request\.action !== 'Login'.*item\.status !== 'Running'/s);
  assert.match(validation, /progress\.instructionsUri !== 'https:\/\/code\.claude\.com\/docs\/en\/troubleshoot-install'/);
});

test('Claude sign-in text is localized and authoritative observations stay explicit', () => {
  const english = read('shared/locales/en.ts');
  const spanish = read('shared/locales/es.ts');
  const details = read('detail.jsx');

  for (const source of [english, spanish]) {
    assert.match(source, /"nodes\.signInToClaude"/);
    assert.match(source, /"nodes\.claudeManualSignInInstructions"/);
    assert.match(source, /"nodes\.claudeVendorInstructions"/);
  }
  assert.match(details, /t\('shared\.authentication'\)/);
  assert.match(details, /t\('shared\.detected'\)/);
  assert.match(details, /state\.detectedAtUtc \? timestamp\(state\.detectedAtUtc\)/);
});
