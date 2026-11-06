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
    ['operationId', 'workerId', 'project', 'repository', 'projectId', 'projectRevision', 'apply', 'olderThanDays', 'offset', 'limit', 'timeoutSeconds'].includes(key) && operation.request?.[key] !== expected) ||
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

// Eligibility is a Server projection, never inferred from observer status or pairing in the browser.
export function readBranchTargets(value, project) {
  if (!value || value.projectId !== project.id || value.revision !== project.revision || value.project !== project.name ||
    value.repository !== project.repository || !Array.isArray(value.targets) || value.targets.length > 200 ||
    new Set(value.targets.map(item => item?.workerId)).size !== value.targets.length || value.targets.some(item =>
      !item || !['managed', 'standalone-observer'].includes(item.mode) ||
      !(item.mode === 'managed' ? /^[a-f0-9]{32}$/ : /^obs_[a-f0-9]{32}$/).test(item.workerId) ||
      typeof item.displayName !== 'string' || item.displayName.length > 200 ||
      typeof item.eligible !== 'boolean' || typeof item.canApply !== 'boolean' || item.canApply && !item.eligible ||
      ![item.reason, item.applyReason].every(reason => reason === null || typeof reason === 'string' && /^[a-z0-9-]{1,100}$/.test(reason)) ||
      item.eligible !== (item.reason === null) || item.canApply !== (item.applyReason === null))) throw Error('Invalid branch maintenance targets');
  return value;
}
export function selectedBranchTarget(inventory, workerId) {
  return inventory?.targets.find(item => item.workerId === workerId);
}
export function requireBranchTarget(inventory, request) {
  const target = selectedBranchTarget(inventory, request.workerId);
  if (inventory.projectId !== request.projectId || inventory.revision !== request.projectRevision || inventory.project !== request.project || inventory.repository !== request.repository || !target?.eligible || request.apply && !target.canApply)
    throw Error(target?.reason ?? (request.apply ? target?.applyReason : null) ?? 'branch-target-changed');
  return target;
}
