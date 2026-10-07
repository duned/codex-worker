import { StatusBadge, AdvancedDisclosure } from '../../shared/Presentation';
import { Button } from '../../untitled/components/base/buttons/button';
import { useServerReadiness } from './useServerReadiness';
export function ServerConnectionSummary() {
  const { github, node, connection } = useServerReadiness();
  return <section aria-label="Server GitHub connection" className="space-y-3 rounded-xl border border-secondary p-5">
    <h2 className="text-lg font-semibold text-primary">Server GitHub connection</h2>
    <StatusBadge tone={github.tone}>{github.complete ? 'Connected' : github.detail}</StatusBadge>
    <p>Server authentication does not establish repository write permission or Worker readiness.</p>
    <Button href="/settings?node=server" color="secondary">Manage Server GitHub connection</Button>
    <AdvancedDisclosure><p>Observations: {node?.observationsStale ? 'stale' : node ? 'current' : 'unavailable'}. Provisioning: {connection.data ? connection.data.provisioningEnabled ? 'enabled' : 'disabled locally' : 'unavailable'}.</p></AdvancedDisclosure>
  </section>;
}
