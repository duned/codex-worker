import { t } from '../../shared/i18n';
// Fixed typed actions only. Availability and authorization are checked by the Server/node.
export const nodeActions: Record<string, { action: string; label: string; elevation?: boolean; destructive?: boolean }> = {
  detect: { action: 'Detect', label: t("provisioning.reDetect") }, refresh: { action: 'Detect', label: t("provisioning.reDetect") },
  install: { action: 'Install', label: t("provisioning.install"), elevation: true }, update: { action: 'Update', label: t("provisioning.update"), elevation: true },
  uninstall: { action: 'Uninstall', label: t("provisioning.uninstall"), elevation: true, destructive: true },
  configure: { action: 'Configure', label: t("provisioning.configureAccess"), elevation: true },
  prepareauthentication: { action: "PrepareAuthentication", label: t("provisioning.prepareAuthentication") },
  login: { action: 'Login', label: t("provisioning.startDeviceLogin") }, logout: { action: 'Logout', label: t("provisioning.removeAuthentication"), destructive: true },
  checkauthentication: { action: "CheckAuthentication", label: t("provisioning.checkAuthentication") }, checkconfiguration: { action: "CheckConfiguration", label: t("provisioning.checkConfiguration") },
  generatesshkey: { action: 'GenerateSshKey', label: t("provisioning.generateSSHKey") }, inspectsshkey: { action: 'InspectSshKey', label: t("provisioning.inspectPublicKey") },
  removesshkey: { action: 'RemoveSshKey', label: t("provisioning.removeSSHKey"), destructive: true },
  verifyrepositoryaccess: { action: "VerifyRepositoryAccess", label: t("provisioning.verifyRepositoryReadAccess") }
};
export const activeCommand = (command: { status: string }) => ['Pending', 'Running'].includes(command.status);
export const expiredCommand = (command: { status: string; deadlineUtc?: string }, now: number) => command.status === 'Running' && !!command.deadlineUtc && Date.parse(command.deadlineUtc) <= now;
