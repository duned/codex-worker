function archivedBranch(branch) {
  return typeof branch.branch === 'string' && branch.branch.length <= 250 && /^(done|complete)\/feature\/.+/.test(branch.branch) &&
    !/[\s\x00-\x1f\x7f~^:?*\[\\]/.test(branch.branch) && !branch.branch.includes('..') && !branch.branch.includes('@{') &&
    !branch.branch.includes('//') && !/[/.]$/.test(branch.branch) && branch.branch.split('/').every(part => !part.startsWith('.') && !part.endsWith('.lock'));
}
export function selectable(branch) {
  return branch.decision === 'eligible' && archivedBranch(branch) && /^[a-f0-9]{40}([a-f0-9]{24})?$/i.test(branch.tip ?? '');
}
export function selections(rows, names) {
  return rows.filter(row => names.includes(row.branch) && selectable(row)).map(row => ({ branch: row.branch, expectedTip: row.tip }));
}
export function branchOperation(value, request) {
  const operation = value?.operation ?? value;
  if (!operation || Object.entries(request).some(([key, expected]) =>
    ['operationId', 'workerId', 'project', 'repository', 'apply', 'olderThanDays', 'offset', 'limit', 'timeoutSeconds'].includes(key) && operation.request?.[key] !== expected) ||
    operation.request?.action !== 'archived-branches' || typeof operation.status !== 'string') throw Error('Unexpected branch operation');
  const selected = request.branches;
  const returnedSelection = operation.request?.branches;
  if (selected ? !Array.isArray(returnedSelection) || returnedSelection.length !== selected.length ||
    selected.some((item, index) => returnedSelection[index]?.branch !== item.branch || returnedSelection[index]?.expectedTip !== item.expectedTip) :
    returnedSelection != null) throw Error('Unexpected branch selection');
  const rows = operation.report?.branches ?? [];
  if (!Array.isArray(rows) || rows.length > (request.limit ?? 100) || new Set(rows.map(row => row?.branch)).size !== rows.length || rows.some(row => !row || !archivedBranch(row) || !['eligible', 'skipped', 'review', 'deleted'].includes(row.decision) || typeof row.reason !== 'string' || row.reason.length > 300 || selected && !selected.some(item => item.branch === row.branch))) throw Error('Invalid branch inventory');
  return operation;
}
