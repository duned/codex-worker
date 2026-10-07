// Fixed typed actions only. Availability and authorization are checked by the Server/node.
export const nodeActions: Record<string, { action: string; label: string; elevation?: boolean; destructive?: boolean }> = {
  detect: { action: 'Detect', label: 'Re-detect' }, refresh: { action: 'Detect', label: 'Re-detect' },
  install: { action: 'Install', label: 'Install', elevation: true }, update: { action: 'Update', label: 'Update', elevation: true },
  uninstall: { action: 'Uninstall', label: 'Uninstall', elevation: true, destructive: true },
  configure: { action: 'Configure', label: 'Configure access', elevation: true },
  prepareauthentication: { action: 'PrepareAuthentication', label: 'Prepare authentication' },
  login: { action: 'Login', label: 'Start device login' }, logout: { action: 'Logout', label: 'Remove authentication', destructive: true },
  checkauthentication: { action: 'CheckAuthentication', label: 'Check authentication' }, checkconfiguration: { action: 'CheckConfiguration', label: 'Check configuration' },
  generatesshkey: { action: 'GenerateSshKey', label: 'Generate SSH key' }, inspectsshkey: { action: 'InspectSshKey', label: 'Inspect public key' },
  removesshkey: { action: 'RemoveSshKey', label: 'Remove SSH key', destructive: true },
  verifyrepositoryaccess: { action: 'VerifyRepositoryAccess', label: 'Verify repository read access' }
};
export const activeCommand = (command: { status: string }) => ['Pending', 'Running'].includes(command.status);
export const expiredCommand = (command: { status: string; deadlineUtc?: string }, now: number) => command.status === 'Running' && !!command.deadlineUtc && Date.parse(command.deadlineUtc) <= now;
