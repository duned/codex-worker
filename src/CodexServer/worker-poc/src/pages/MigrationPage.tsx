import { ServerConnectionSummary } from '../features/server/ServerConnectionSummary';
import { useLocation } from 'react-router-dom';
import { canonicalPath, type Section } from '../app/routes';
import { Button } from '../untitled/components/base/buttons/button';
import { PageHeading, ViewState } from '../shared/Presentation';
export function MigrationPage({ section }: { section: Section }) {
  const location = useLocation();
  const title = section.charAt(0).toUpperCase() + section.slice(1);
  return <><PageHeading title={title} />{section === 'settings' && <ServerConnectionSummary />}<ViewState title="Dashboard migration preview">
    <p>This screen is being migrated. Use the current dashboard for authoritative observations and administration.</p>
    <Button href={canonicalPath(location.pathname, location.search)} color="secondary">Open {title.toLowerCase()} in current dashboard</Button>
  </ViewState></>;
}
