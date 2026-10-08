import { t, useLanguage, localizeText, statusLabel } from '../../shared/i18n';
import { timestamp } from '../../model';
import { useState } from 'react';
import { useLocation, useNavigate, useParams } from 'react-router-dom';
import { useApiRead, useSession } from '../../shared/api/session';
import { workers } from '../../shared/api/validation';
import { credentials, credential, credentialPath, type Credential, type CredentialAttempt } from './contracts';
import { useSettings } from './Workspace';
import { ServerPreparation } from './ServerPreparation';
import { ActionDialog, FormDialog } from '../../shared/Dialogs';
import { PageHeading, Notice, ResourceIdentity, StatusBadge, AdvancedDisclosure } from '../../shared/Presentation';
import { ThemeControl } from '../../shared/ThemeControl';
import { Button } from '../../untitled/components/base/buttons/button';
import { Input } from '../../shared/Input';
type Edit = { kind: 'create' } | { kind: 'replace' | 'assign' | 'revoke'; before: Credential };
export function SettingsPage() {
  useLanguage();
  const location = useLocation(), session = useSession();
  // Route/session replacement destroys all private fields, codes and local consent.
  return session.authenticated ? <SettingsView key={`${session.generation}:${location.pathname}:${location.search}`} /> : <PageHeading title={t("settings.settings")} />;
}
function SettingsView() {
  useLanguage();
  const { resourceId } = useParams();
  const location = useLocation();
  const navigate = useNavigate(), owner = useSettings();
  const list = useApiRead('/api/v1/credentials', credentials);
  const registered = useApiRead('/api/v1/workers', workers);
  const [edit, setEdit] = useState<Edit>();
  const [provider, setProvider] = useState(''), [type, setType] = useState(''), [secret, setSecret] = useState(''), [workerId, setWorkerId] = useState('');
  function close() { setSecret(''); setProvider(''); setType(''); setWorkerId(''); setEdit(undefined); }
  function open(value: Edit) { close(); setEdit(value); if (value.kind === 'assign') setWorkerId(value.before.assignedWorkerId ?? ''); }
  async function submit() {
    if (!edit) return;
    const transient = secret; setSecret('');
    const attempt: CredentialAttempt = edit.kind === 'create' ? { kind: 'create', provider, type, existingIds: list.data?.map(c => c.id) ?? [] }
      : { kind: edit.kind, before: edit.before, workerId: edit.kind === 'assign' ? workerId : undefined };
    await owner.submit(attempt, transient); close();
  }
  return <div className="space-y-8">
    <PageHeading title={t("settings.settings")} actions={<ThemeControl />} />
    <ServerPreparation />
    <section aria-label={t("settings.credentialMetadata")} className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3"><h2 className="text-lg font-semibold text-primary">{t("settings.credentialMetadata")}</h2>
        <div className="flex flex-wrap gap-3"><Button color="secondary" onPress={() => { void list.refetch(); }}>{t("settings.refreshMetadata")}</Button><Button isDisabled={owner.locked()} onPress={() => open({ kind: 'create' })}>{t("settings.addCredential")}</Button></div>
      </div>
      <p className="text-sm text-secondary">{t("settings.metadataNeverRetrievesASecretAssignmentPermitsDeliveryToOneRegisteredWorker")}</p>
      {list.error && <Notice error>{list.error}</Notice>}{registered.error && <Notice error>{registered.error}</Notice>}
      {!list.data && !list.error && <Notice>{t("settings.loadingCredentialMetadata")}</Notice>}
      {list.data?.length === 0 && <Notice>{t("settings.noServerManagedCredentialsAreRegistered")}</Notice>}
      {list.data?.map(c => <article key={c.id} className="space-y-3 rounded-xl border border-secondary bg-primary p-5">
        <div className="flex flex-wrap items-start justify-between gap-3"><ResourceIdentity name={`${c.provider} · ${c.type}`} id={c.id} /><StatusBadge tone={c.status === 'Ready' ? 'success' : c.status === 'NeedsReprovision' ? 'warning' : 'gray'}>{localizeText(c.status)}</StatusBadge></div>
        <p className="text-sm text-secondary">{t("settings.version")}{' '}{c.version} · {c.assignedWorkerId ? t('settings.assignedTo', { name: registered.data?.find(w => w.workerId === c.assignedWorkerId)?.displayName ?? c.assignedWorkerId }) : localizeText('Unassigned')}</p>
        <div className="flex flex-wrap gap-3"><Button color="secondary" href={`/settings/${encodeURIComponent(c.id)}${location.search}`}>{t("settings.showMetadata")}</Button>
          <Button color="secondary" isDisabled={owner.locked(c.id) || c.status !== 'Ready' || !registered.data?.length} onPress={() => open({ kind: 'assign', before: c })}>{t("settings.assign")}</Button>
          <Button color="secondary" isDisabled={owner.locked(c.id) || !['Ready', "NeedsReprovision"].includes(c.status)} onPress={() => open({ kind: 'replace', before: c })}>{t("settings.replaceSecret")}</Button>
          <Button color="secondary-destructive" isDisabled={owner.locked(c.id) || !['Ready', "NeedsReprovision"].includes(c.status)} onPress={() => open({ kind: 'revoke', before: c })}>{t("settings.revoke")}</Button>
        </div><AdvancedDisclosure title={t("settings.credentialResourceDetails")}><p>{t("settings.updated")}{' '}{timestamp(c.updatedAtUtc)}{c.revokedAtUtc && t('settings.revokedAt', { time: timestamp(c.revokedAtUtc) })}</p>{c.assignedWorkerId && <Button color="link-gray" href={`/workers/${encodeURIComponent(c.assignedWorkerId)}`}>{t("settings.assignedWorker")}</Button>}</AdvancedDisclosure>
      </article>)}
      {owner.message && <Notice>{owner.message}</Notice>}
      {Object.keys(owner.attempts).filter(key => owner.locked(key.slice('credential:'.length))).map(key => <Notice key={key}>{t("settings.aCredentialActionIsUnconfirmedNoSecretInputWasRetained")}{' '}<Button color="secondary" onPress={() => { void owner.reconcile(key); }}>{t("settings.refreshAuthoritativeCredentialState")}</Button></Notice>)}
    </section>
    {resourceId && <CredentialDetail id={resourceId} onClose={() => navigate(`/settings${location.search}`)} />}
    {edit && <ActionDialog isOpen title={edit.kind === 'create' ? t("settings.addServerManagedCredential") : edit.kind === 'replace' ? t("settings.replaceCredentialSecret") : edit.kind === 'assign' ? t("settings.assignCredential") : t("settings.revokeCredential")}
      description={edit.kind === 'revoke' ? t("settings.stopFutureServerDeliveryProviderSideAuthorizationAndSecretsAlreadyDeliveredTo") : edit.kind === 'assign' ? t("settings.selectARegisteredWorkerDeliveryRequiresItsSeparatelyAuthorizedDeliveryCredentialNode") : t("settings.theInputIsSentOnceToProtectedServerStorageAndClearedBefore")}
      onClose={close} actionLabel={edit.kind === 'revoke' ? t('settings.revoke') : t('projects.save')} destructive={edit.kind === 'revoke'} onSubmit={submit}
      disabled={edit.kind === 'create' ? !provider.trim() || !type.trim() || !secret.trim() : edit.kind === 'replace' ? !secret.trim() : edit.kind === 'assign' ? !workerId : false}>
      {edit.kind === 'create' && <><Input label={t("settings.provider")} value={provider} onChange={setProvider} maxLength={80} /><Input label={t("settings.credentialType")} value={type} onChange={setType} maxLength={80} /></>}
      {(edit.kind === 'create' || edit.kind === 'replace') && <Input label={t("settings.secret")} type="password" autoComplete="off" value={secret} onChange={setSecret} maxLength={16384} />}
      {edit.kind === 'assign' && <div role="group" aria-label={t("settings.selectWorker")} className="space-y-2"><p className="text-sm font-medium text-secondary">{t("settings.worker")}</p>{registered.data?.map(w => <Button key={w.workerId} color="secondary" aria-pressed={workerId === w.workerId} onPress={() => setWorkerId(w.workerId)}>{w.displayName ?? w.workerId}{workerId === w.workerId && t('settings.selected')}</Button>)}</div>}
    </ActionDialog>}
  </div>;
}
function CredentialDetail({ id, onClose }: { id: string; onClose(): void }) {
  useLanguage();
  const metadata = useApiRead(credentialPath(id), credential);
  const value = metadata.data?.id === id ? metadata.data : undefined;
  return <FormDialog isOpen title={t("settings.credentialMetadata")} description={t("settings.onlyResourceMetadataIsRequestedSecretDeliveryIsRestrictedToTheAssigned")} onClose={onClose}>
    {metadata.error ? <Notice error>{t("settings.credentialUnavailableOrDeleted")}{' '}{localizeText(metadata.error)}</Notice> : value ? <dl className="space-y-2 break-all text-sm text-secondary">{([
      ['Provider', value.provider], ['Type', value.type], ['ID', value.id], [t("settings.secretReference"), value.secretReference], ['Status', value.status],
      [t("settings.assignedWorker"), value.assignedWorkerId ?? t("settings.unassigned")], ['Version', value.version], ['Created', timestamp(value.createdAtUtc)], ['Updated', timestamp(value.updatedAtUtc)], ['Revoked', value.revokedAtUtc ? timestamp(value.revokedAtUtc) : t("settings.notRevoked")]
    ] as const).map(([label, item]) => <div key={label}><dt className="font-semibold">{localizeText(label)}</dt><dd>{label === 'Status' ? statusLabel(String(item)) : label === 'Revoked' && !value.revokedAtUtc ? t('settings.notRevoked') : item}</dd></div>)}</dl> : <Notice>{t("settings.loadingCredentialMetadata")}</Notice>}
    <Button color="secondary" onPress={() => { void metadata.refetch(); }}>{t("settings.refreshDetail")}</Button><Button autoFocus color="secondary" onPress={onClose}>{t("settings.close")}</Button>
  </FormDialog>;
}
