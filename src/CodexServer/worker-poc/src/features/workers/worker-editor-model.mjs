const semanticVersionPattern = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$/;

function parseVersion(value) {
  if (typeof value !== 'string' || value.length > 200) return undefined;
  const match = semanticVersionPattern.exec(value);
  if (!match) return undefined;
  const prerelease = match[4]?.split('.') ?? [];
  if (prerelease.some(part => part.length > 1 && /^0[0-9]+$/.test(part))) return undefined;
  return { numbers: [BigInt(match[1]), BigInt(match[2]), BigInt(match[3])], prerelease };
}

function comparePrerelease(left, right) {
  if (!left.length || !right.length) return left.length === right.length ? 0 : left.length ? -1 : 1;
  for (let index = 0; index < Math.min(left.length, right.length); index++) {
    const a = left[index], b = right[index];
    const aNumeric = /^[0-9]+$/.test(a), bNumeric = /^[0-9]+$/.test(b);
    const comparison = aNumeric && bNumeric ? (BigInt(a) < BigInt(b) ? -1 : BigInt(a) > BigInt(b) ? 1 : 0)
      : aNumeric !== bNumeric ? (aNumeric ? -1 : 1) : a < b ? -1 : a > b ? 1 : 0;
    if (comparison) return comparison;
  }
  return left.length < right.length ? -1 : left.length > right.length ? 1 : 0;
}

export function compareSemanticVersions(left, right) {
  const a = parseVersion(left), b = parseVersion(right);
  if (!a || !b) return undefined;
  for (let index = 0; index < 3; index++) {
    if (a.numbers[index] !== b.numbers[index]) return a.numbers[index] < b.numbers[index] ? -1 : 1;
  }
  return comparePrerelease(a.prerelease, b.prerelease);
}

export function workerConfiguration(worker) {
  const candidate = worker?.configuredMaximumCapacity ?? worker?.maximumCapacity ?? worker?.capacity;
  return {
    displayName: typeof worker?.displayName === 'string' ? worker.displayName : '',
    maximumParallelExecutions: Number.isInteger(candidate) && candidate >= 1 && candidate <= 8 ? String(candidate) : ''
  };
}

/**
 * @typedef {{ workerVersion?: string, serverVersion?: string, lifecycle?: string } & (
 *   { state: 'unavailable' | 'loading' } |
 *   { state: 'in-progress' | 'failed', lifecycle: string } |
 *   { state: 'current' | 'available' | 'ahead', workerVersion: string, serverVersion: string }
 * )} WorkerVersionState
 */

/** @returns {WorkerVersionState} */
export function workerVersionState(worker, serverVersion, serverVersionLoading = false) {
  const heartbeat = Date.parse(worker?.lastHeartbeatAtUtc ?? '');
  if (!worker || !['online', 'draining', 'offline'].includes(worker.availability) || !Number.isFinite(heartbeat) || heartbeat > Date.now() + 60_000)
    return { state: 'unavailable' };

  const lifecycle = String(worker.lifecycleState ?? '').toLowerCase();
  if (lifecycle === 'updating' || lifecycle === 'updated' || lifecycle === 'restarting' || lifecycle === 'reconnecting')
    return { state: 'in-progress', lifecycle };
  if (lifecycle === 'update-failed' || lifecycle === 'restart-failed')
    return { state: 'failed', lifecycle };

  const workerVersion = parseVersion(worker.workerVersion);
  if (!workerVersion) return { state: 'unavailable' };
  if (serverVersionLoading) return { state: 'loading' };
  const currentServerVersion = parseVersion(serverVersion);
  if (!currentServerVersion) return { state: 'unavailable' };
  const comparison = compareSemanticVersions(worker.workerVersion, serverVersion);
  if (comparison === 0) return { state: 'current', workerVersion: worker.workerVersion, serverVersion };
  return comparison < 0
    ? { state: 'available', workerVersion: worker.workerVersion, serverVersion }
    : { state: 'ahead', workerVersion: worker.workerVersion, serverVersion };
}
