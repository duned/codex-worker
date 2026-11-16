export function restartPending(operation) {
  return Boolean(operation && !['succeeded', 'failed'].includes(operation.state));
}
export function restartAvailable(worker, operation) {
  return Boolean(worker && ['online', 'draining'].includes(worker.availability) &&
    worker.capabilities?.some(item => item.type === 'protocol' && item.name === 'worker-restart-v1') &&
    !restartPending(operation) && operation?.state !== 'failed');
}

export function restartStatusPaths(workerId) {
  const workerPath = `/api/v1/workers/${encodeURIComponent(workerId)}`;
  return {
    operation: `${workerPath}/update/recovery`,
    worker: workerPath
  };
}

export function recoveryAvailable(operation, nodeQuiescent) {
  return Boolean(operation?.state === 'failed' && operation.recovery?.eligible && nodeQuiescent);
}

export function releaseRestartDrainAvailable(operation, nodeQuiescent) {
  return Boolean(operation?.restart && operation.state === 'failed' &&
    !operation.drainReleasedAtUtc && operation.recovery?.releaseDrainEligible && nodeQuiescent);
}
