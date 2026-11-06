export function selectable(branch) {
  return branch.decision === 'eligible' && typeof branch.branch === 'string' && branch.branch.length <= 250 && /^(done|complete)\/feature\/.+/.test(branch.branch) &&
    !/[\s\x00-\x1f\x7f~^:?*\[\\]/.test(branch.branch) && !branch.branch.includes('..') && !branch.branch.includes('@{') &&
    !branch.branch.includes('//') && !/[/.]$/.test(branch.branch) && branch.branch.split('/').every(part => !part.startsWith('.') && !part.endsWith('.lock')) &&
    /^[a-f0-9]{40}([a-f0-9]{24})?$/i.test(branch.tip ?? '');
}
export function selections(rows, names) {
  return rows.filter(row => names.includes(row.branch) && selectable(row)).map(row => ({ branch: row.branch, expectedTip: row.tip }));
}
export function branchOperation(value, request) {
  const operation = value?.operation ?? value;
  if (!operation || Object.entries(request).some(([key, expected]) =>
    ['operationId', 'workerId', 'project', 'repository', 'apply', 'olderThanDays', 'offset'].includes(key) && operation.request?.[key] !== expected) ||
    operation.request?.action !== 'archived-branches' || typeof operation.status !== 'string') throw Error('Unexpected branch operation');
  const rows = operation.report?.branches ?? [];
  if (!Array.isArray(rows) || rows.length > 100 || rows.some(row => !row || typeof row.branch !== 'string' || typeof row.decision !== 'string' || typeof row.reason !== 'string')) throw Error('Invalid branch inventory');
  return operation;
}
