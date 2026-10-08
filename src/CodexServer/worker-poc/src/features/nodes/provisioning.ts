import { t } from '../../shared/i18n';
import type { NodeCommandSummary, NodeSummary } from '../../shared/api/contracts';
export const nodeActions: Record<string, { command: string; label: string; elevation?: boolean; destructive?: boolean }> = {
  configure: { command: 'Configure', label: t("nodes.configureDaemonAccess"), elevation: true },
  login: { command: 'Login', label: t("nodes.signInWithDeviceCode") }, refresh: { command: 'Detect', label: t("nodes.refreshReDetect") },
  install: { command: 'Install', label: t("nodes.install"), elevation: true }, update: { command: 'Update', label: t("nodes.update"), elevation: true },
  uninstall: { command: 'Uninstall', label: t("nodes.uninstall"), elevation: true, destructive: true },
  checkauthentication: { command: "CheckAuthentication", label: t("nodes.checkAuthentication") },
  logout: { command: 'Logout', label: t("nodes.logoutRemoveAuthentication"), destructive: true },
  checkconfiguration: { command: "CheckConfiguration", label: t("nodes.checkConfiguration") },
  prepareauthentication: { command: "PrepareAuthentication", label: t("nodes.prepareGitHubLogin") },
  generatesshkey: { command: 'GenerateSshKey', label: t("nodes.generateSSHKey") }, inspectsshkey: { command: 'InspectSshKey', label: t("nodes.inspectPublicKey") },
  removesshkey: { command: 'RemoveSshKey', label: t("nodes.removeSSHKey"), destructive: true },
  verifyrepositoryaccess: { command: "VerifyRepositoryAccess", label: t("nodes.verifyRepositoryAccess") }
};
export const commandActive = (command: NodeCommandSummary) => ['Pending', 'Running'].includes(command.status);
export const commandExpired = (command: NodeCommandSummary, now: number) => command.status === 'Running' && Number.isFinite(Date.parse(command.deadlineUtc ?? '')) && Date.parse(command.deadlineUtc ?? '') <= now;
export function provisioningReason(node: NodeSummary | undefined, commands: NodeCommandSummary[] | undefined, capabilityId: string, action: string) {
  if (!node || !commands) return t("nodes.currentCapabilityOrOperationEvidenceUnavailable");
  if (!['online', 'connected', 'draining'].includes(node.connectivity)) return t("nodes.nodeIsDisconnectedRefreshCurrentObservations");
  if (node.provisioningReadiness === 'busy' || commands.some(commandActive)) return t("nodes.reconcilePendingOperationsBeforeAnotherAction");
  if (!Object.hasOwn(nodeActions, action) || !node.capabilities.find(item => item.definition.id === capabilityId)?.availableActions.includes(action)) return t("nodes.thisTypedActionIsNoLongerAdvertisedByTheNode");
  return '';
}
export function deviceLoginUrl(uri: string) {
  return ['https://github.com/login/device', 'https://auth.openai.com/codex/device'].includes(uri) ? uri : undefined;
}
