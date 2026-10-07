import type { NodeCommandSummary, NodeSummary } from '../../shared/api/contracts';
export const nodeActions: Record<string, { command: string; label: string; elevation?: boolean; destructive?: boolean }> = {
  configure: { command: 'Configure', label: 'Configure daemon access', elevation: true },
  login: { command: 'Login', label: 'Sign in with device code' }, refresh: { command: 'Detect', label: 'Refresh / Re-detect' },
  install: { command: 'Install', label: 'Install', elevation: true }, update: { command: 'Update', label: 'Update', elevation: true },
  uninstall: { command: 'Uninstall', label: 'Uninstall', elevation: true, destructive: true },
  checkauthentication: { command: 'CheckAuthentication', label: 'Check authentication' },
  logout: { command: 'Logout', label: 'Logout / Remove authentication', destructive: true },
  checkconfiguration: { command: 'CheckConfiguration', label: 'Check configuration' },
  prepareauthentication: { command: 'PrepareAuthentication', label: 'Prepare GitHub login' },
  generatesshkey: { command: 'GenerateSshKey', label: 'Generate SSH key' }, inspectsshkey: { command: 'InspectSshKey', label: 'Inspect public key' },
  removesshkey: { command: 'RemoveSshKey', label: 'Remove SSH key', destructive: true },
  verifyrepositoryaccess: { command: 'VerifyRepositoryAccess', label: 'Verify repository access' }
};
export const commandActive = (command: NodeCommandSummary) => ['Pending', 'Running'].includes(command.status);
export const commandExpired = (command: NodeCommandSummary, now: number) => command.status === 'Running' && Number.isFinite(Date.parse(command.deadlineUtc ?? '')) && Date.parse(command.deadlineUtc ?? '') <= now;
export function provisioningReason(node: NodeSummary | undefined, commands: NodeCommandSummary[] | undefined, capabilityId: string, action: string) {
  if (!node || !commands) return 'Current capability or operation evidence unavailable.';
  if (!['online', 'connected', 'draining'].includes(node.connectivity)) return 'Node is disconnected. Refresh current observations.';
  if (node.provisioningReadiness === 'busy' || commands.some(commandActive)) return 'Reconcile pending operations before another action.';
  if (!Object.hasOwn(nodeActions, action) || !node.capabilities.find(item => item.definition.id === capabilityId)?.availableActions.includes(action)) return 'This typed action is no longer advertised by the node.';
  return '';
}
export function deviceLoginUrl(uri: string) {
  return ['https://github.com/login/device', 'https://auth.openai.com/codex/device'].includes(uri) ? uri : undefined;
}
