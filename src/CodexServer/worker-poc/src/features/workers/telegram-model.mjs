export function telegramSynchronization(worker, diagnostics, revision, unavailable) {
  if (['stale', 'offline'].includes(worker.availability)) return 'stale';
  if (unavailable || !['online', 'draining'].includes(worker.availability)) return 'pending';
  if (diagnostics?.configurationSynchronization === 'error') return 'failed';
  return diagnostics?.configurationSynchronization === 'synchronized' && diagnostics.configurationRevision === revision ? 'applied' : 'pending';
}

export function telegramResultLabel(operation) {
  if (operation.status === 'succeeded') return 'workers.telegramTestSuccess';
  if (operation.status === 'failed') return operation.diagnostic === 'telegram-rejected' ? 'workers.telegramTestRejected' : 'workers.telegramTestFailed';
  return 'workers.telegramTestPending';
}
