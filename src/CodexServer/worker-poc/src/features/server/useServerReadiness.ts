import { useApiRead } from '../../shared/api/session';
import { nodes, serverGitHubConnection } from '../../shared/api/validation';
import { serverGitHubReadiness } from './readiness';
export function useServerReadiness() {
  const inventory = useApiRead('/api/v1/nodes', nodes);
  const connection = useApiRead('/api/v1/nodes/server/github-connection', serverGitHubConnection);
  const node = inventory.data?.find(item => item.id === 'server' && item.kind === 'server');
  return { inventory, connection, node, github: serverGitHubReadiness(node, connection.data) };
}
