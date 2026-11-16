import { record, serverGitHubConnection } from '../../shared/api/validation';
export const connectionPath = '/api/v1/nodes/server/github-connection';
export interface Challenge { commandId: string; userCode: string; deadline: number }
/** Split transient device instructions from cacheable metadata at the API boundary. */
export function connectionSnapshot(value: unknown, now: number) {
  const metadata = serverGitHubConnection(value);
  const raw = record(value);
  let challenge: Challenge | undefined;
  for (const entry of raw.commands as unknown[]) {
    const item = record(entry), request = record(item.request);
    const deadline = typeof item.deadlineUtc === 'string' ? Date.parse(item.deadlineUtc) : NaN;
    if (item.status !== 'Running' || request.nodeId !== 'server' || request.capabilityId !== 'github-cli' || request.action !== 'Login' || deadline <= now || !Number.isFinite(deadline) || item.loginInstructions == null) continue;
    const instructions = record(item.loginInstructions);
    if (instructions.verificationUri === 'https://github.com/login/device' && typeof instructions.userCode === 'string' && /^[A-Z0-9-]{1,32}$/.test(instructions.userCode)) {
      challenge = { commandId: String(item.id), userCode: instructions.userCode, deadline }; break;
    }
  }
  return { metadata, challenge };
}
