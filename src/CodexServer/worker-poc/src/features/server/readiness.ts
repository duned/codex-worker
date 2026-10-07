import type { NodeSummary, ServerGitHubConnection } from '../../shared/api/contracts';
export function serverGitHubReadiness(node: NodeSummary | undefined, connection: ServerGitHubConnection | undefined) {
  const capability = node?.capabilities.find(item => item.definition.id === 'github-cli');
  if (!node || !connection || !capability || node.connectivity !== 'connected') return { complete: false, detail: 'Connection observations unavailable', tone: 'gray' as const };
  if (node.observationsStale) return { complete: false, detail: 'Observations stale · check authentication', tone: 'warning' as const };
  const active = connection.commands.find(command => ['Pending', 'Running'].includes(command.status));
  const latest = active ?? connection.commands[0];
  const operation = capability.state.operation;
  const failed = latest && ['Login', 'CheckAuthentication'].includes(latest.request.action) && ['Failed', 'TimedOut'].includes(latest.status);
  const observationFailed = operation && ['login', 'checkauthentication'].includes(operation.action ?? '') && ['Failed', 'TimedOut'].includes(operation.state);
  const complete = !active && !failed && operation?.state !== 'Running' && !observationFailed && capability.state.installation === 'Installed' && capability.state.health === 'Healthy' && capability.state.authentication === 'Satisfied';
  return { complete, detail: active ? `${active.request.action} · ${active.status}` : failed || observationFailed ? 'Authentication check failed · inspect connection' : complete ? 'Current Server GitHub authentication' : `Authentication: ${capability.state.authentication ?? 'unavailable'}`, tone: complete ? 'success' as const : 'warning' as const };
}
export function preparedWorker(nodes: NodeSummary[] | undefined) {
  return nodes?.find(node => node.kind === 'worker' && !node.observationsStale && node.connectivity === 'connected' && node.executionReadiness === 'ready');
}
