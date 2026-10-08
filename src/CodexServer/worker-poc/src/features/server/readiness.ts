import { t, statusLabel } from '../../shared/i18n';
import type { NodeSummary, ServerGitHubConnection } from '../../shared/api/contracts';
export function serverGitHubReadiness(node: NodeSummary | undefined, connection: ServerGitHubConnection | undefined) {
  const capability = node?.capabilities.find(item => item.definition.id === 'github-cli');
  if (!node || !connection || !capability || node.connectivity !== 'connected') return { complete: false, detail: t("server.connectionUnavailable"), tone: 'gray' as const };
  if (node.observationsStale) return { complete: false, detail: t("server.observationsStale"), tone: 'warning' as const };
  const active = connection.commands.find(command => ['Pending', 'Running'].includes(command.status));
  const latest = active ?? connection.commands[0];
  const operation = capability.state.operation;
  const failed = latest && ['Login', "CheckAuthentication"].includes(latest.request.action) && ['Failed', 'TimedOut'].includes(latest.status);
  const observationFailed = operation && ['login', 'checkauthentication'].includes(operation.action ?? '') && ['Failed', 'TimedOut'].includes(operation.state);
  const complete = !active && !failed && operation?.state !== 'Running' && !observationFailed && capability.state.installation === 'Installed' && capability.state.health === 'Healthy' && capability.state.authentication === 'Satisfied';
  return { complete, detail: active ? `${statusLabel(active.request.action)} · ${statusLabel(active.status)}` : failed || observationFailed ? t("server.authenticationCheckFailedInspectConnection") : complete ? t("server.currentServerGitHubAuthentication") : t('server.authenticationState', { state: statusLabel(capability.state.authentication) }), tone: complete ? 'success' as const : 'warning' as const };
}
export function preparedWorker(nodes: NodeSummary[] | undefined) {
  return nodes?.find(node => node.kind === 'worker' && !node.observationsStale && node.connectivity === 'connected' && node.executionReadiness === 'ready');
}
