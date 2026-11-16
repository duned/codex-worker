import { t, useLanguage, localizeText } from '../../shared/i18n';
import { StatusBadge, AdvancedDisclosure } from '../../shared/Presentation';
import { Button } from '../../untitled/components/base/buttons/button';
import { useServerReadiness } from './useServerReadiness';
export function ServerConnectionSummary() {
  useLanguage();
  const { github, node, connection } = useServerReadiness();
  return <section aria-label={t("server.serverGitHubConnection")} className="space-y-3 rounded-xl border border-secondary p-5">
    <h2 className="text-lg font-semibold text-primary">{t("server.serverGitHubConnection")}</h2>
    <StatusBadge tone={github.tone}>{github.complete ? 'Connected' : github.detail}</StatusBadge>
    <p>{t("server.serverAuthenticationDoesNotEstablishRepositoryWritePermissionOrWorkerReadiness")}</p>
    <Button href="/settings?node=server" color="secondary">{t("server.manageServerGitHubConnection")}</Button>
    <AdvancedDisclosure><p>{t("server.observations")}{' '}{node?.observationsStale ? 'stale' : node ? 'current' : localizeText('unavailable')}{t("server.provisioning")}{' '}{connection.data ? connection.data.provisioningEnabled ? localizeText('enabled') : t("server.disabledLocally") : localizeText('unavailable')}.</p></AdvancedDisclosure>
  </section>;
}
