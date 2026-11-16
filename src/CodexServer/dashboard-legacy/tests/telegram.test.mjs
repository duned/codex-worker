import assert from 'node:assert/strict';
import { test } from 'node:test';
import { telegramSynchronization, telegramResultLabel } from '../src/features/workers/telegram-model.mjs';

test('Telegram test readiness requires current acknowledged and available observations', () => {
  const worker = { availability: 'online' }, diagnostics = { configurationSynchronization: 'synchronized', configurationRevision: 4 };
  assert.equal(telegramSynchronization(worker, diagnostics, 4, false), 'applied');
  assert.equal(telegramSynchronization(worker, diagnostics, 5, false), 'pending');
  assert.equal(telegramSynchronization(worker, diagnostics, 4, true), 'pending');
  assert.equal(telegramSynchronization(worker, undefined, 4, false), 'pending');
  assert.equal(telegramSynchronization({ availability: 'unknown' }, diagnostics, 4, false), 'pending');
  for (const availability of ['stale', 'offline']) assert.equal(telegramSynchronization({ availability }, diagnostics, 4, false), 'stale');
  assert.equal(telegramSynchronization(worker, { ...diagnostics, configurationSynchronization: 'error' }, 4, false), 'failed');
  assert.equal(telegramSynchronization(worker, { ...diagnostics, configurationSynchronization: 'cached' }, 4, false), 'pending');
});

test('Telegram pending, accepted, rejected and uncertain results have distinct presentation', () => {
  for (const status of ['pending', 'running']) assert.equal(telegramResultLabel({ status }), 'workers.telegramTestPending');
  assert.equal(telegramResultLabel({ status: 'succeeded', diagnostic: 'accepted' }), 'workers.telegramTestSuccess');
  assert.equal(telegramResultLabel({ status: 'failed', diagnostic: 'telegram-rejected' }), 'workers.telegramTestRejected');
  for (const diagnostic of ['configuration-changed', 'delivery-unavailable']) assert.equal(telegramResultLabel({ status: 'failed', diagnostic }), 'workers.telegramTestFailed');
});
