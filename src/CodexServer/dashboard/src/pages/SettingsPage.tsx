import { useEffect, useState } from 'react';
import { Button } from '../untitled/components/base/buttons/button';
import type { AuditEvent, Credential } from '../shared/api/contracts';
import { useApiRead } from '../shared/api/session';
import { audit as validateAudit } from '../shared/api/validation';
import { credentials as validateCredentials, serverStatus, version as validateVersion } from '../shared/api/validation';
import { useAction } from '../shared/actions';
import { useNodes, useWorkers } from '../shared/data';
import { t, useLanguage } from '../shared/i18n';
import { auditAction, auditActor, dateTime } from '../shared/model';
import { Card, Empty, Field, HeadRow, inputClass, KeyValues, Notice, PageHeader, Pill, ReadState, Row, Rows, Select, SidePanel } from '../shared/ui';

const columns = 'md:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_minmax(0,1fr)_120px_90px]';

export function SettingsPage() {
  const language = useLanguage();
  const status = useApiRead('/api/status', serverStatus), release = useApiRead('/api/version', validateVersion);
  const credentials = useApiRead('/api/v1/credentials', validateCredentials), nodes = useNodes(), workers = useWorkers();
  const [editing, setEditing] = useState<Credential | 'new'>();
  const server = nodes.data?.find(node => node.kind === 'server');
  const workerName = (id?: string | null) => id ? workers.data?.find(worker => worker.workerId === id)?.displayName || id.slice(0, 8) : t('settings.unassigned');
  return <>
    <PageHeader title={t('settings.title')} description={t('settings.description')} />
    <div className="grid grid-cols-1 gap-6 lg:grid-cols-2 lg:items-start">
      <Card title={t('settings.server')} actions={status.data ? <Pill tone={status.data.persistenceAvailable === false ? 'bad' : 'ok'}>{t(status.data.persistenceAvailable === false ? 'nav.serverUnavailable' : 'nav.serverReady')}</Pill> : undefined}>
        <ReadState read={status}><KeyValues items={[
          [t('settings.address'), <span className="font-mono text-sm">{window.location.origin}</span>],
          [t('settings.version'), release.data?.version ?? status.data?.version ?? '—'],
          [t('settings.startedAt'), dateTime(status.data?.startedAtUtc, language)]
        ]} /></ReadState>
      </Card>
      <Card title={t('settings.serverTools')} flush><ReadState read={nodes}><Rows>
        {server?.capabilities.length ? server.capabilities.map(capability => {
          const ok = capability.state.installation === 'Installed' && capability.state.health === 'Healthy' && capability.state.authentication !== 'Required';
          return <Row key={capability.definition.id} className="grid-cols-[minmax(0,1fr)_auto_auto]">
            <span className="font-medium text-primary">{capability.definition.displayName}</span>
            <span className="text-sm text-tertiary">{capability.state.detectedVersion ?? '—'}</span>
            <Pill tone={ok ? 'ok' : 'bad'}>{t(ok ? 'workers.toolReady' : capability.state.authentication === 'Required' ? 'workers.toolSignIn' : 'workers.toolProblem')}</Pill>
          </Row>;
        }) : <Empty>{t('workers.noTools')}</Empty>}
      </Rows></ReadState></Card>
    </div>
    <Card title={t('settings.credentials')} actions={<Button size="sm" color="secondary" onClick={() => setEditing('new')}>{t('settings.addCredential')}</Button>} flush>
      <ReadState read={credentials}><Rows>
        <HeadRow className={columns}><span>{t('settings.service')}</span><span>{t('settings.kind')}</span><span>{t('settings.worker')}</span><span>{t('common.status')}</span><span /></HeadRow>
        {credentials.data?.length ? credentials.data.map(credential => <Row key={credential.id} className={`grid-cols-1 ${columns}`}>
          <span className="font-medium text-primary">{credential.provider}</span>
          <span className="text-sm text-tertiary">{credential.type}</span>
          <span className="text-sm text-tertiary">{workerName(credential.assignedWorkerId)}</span>
          <span><Pill tone={credential.status === 'Ready' || credential.status === 'Active' ? 'ok' : credential.revokedAtUtc ? 'idle' : 'warn'}>{credential.status}</Pill></span>
          <span>{!credential.revokedAtUtc && <Button size="sm" color="secondary" onClick={() => setEditing(credential)}>{t('common.edit')}</Button>}</span>
        </Row>) : <Empty>{t('settings.noCredentials')}</Empty>}
      </Rows></ReadState>
    </Card>
    <CredentialPanel credential={editing} onClose={() => setEditing(undefined)} />
    <ActivityCard />
  </>;
}

const activityColumns = 'md:grid-cols-[170px_minmax(0,1fr)_minmax(0,2fr)_100px]';

/** The audit log, newest first, one page at a time. */
function ActivityCard() {
  const language = useLanguage();
  const [before, setBefore] = useState<number>(), [older, setOlder] = useState<AuditEvent[]>([]);
  const page = useApiRead(`/api/v1/audit?limit=50${before ? `&before=${before}` : ''}`, validateAudit);
  const [shown, setShown] = useState<AuditEvent[]>([]);
  useEffect(() => { if (page.data) setShown([...older, ...page.data.events]); }, [page.data, older]);
  const more = page.data?.nextBefore;
  return <Card title={t('audit.title')} flush actions={more ? <Button size="sm" color="secondary" onClick={() => { setOlder(shown); setBefore(more); }}>{t('audit.more')}</Button> : undefined}>
    <p className="px-5 py-4 text-sm text-tertiary">{t('audit.description')}</p>
    <ReadState read={page}><Rows>
      <HeadRow className={activityColumns}><span>{t('audit.when')}</span><span>{t('audit.who')}</span><span>{t('audit.what')}</span><span>{t('audit.result')}</span></HeadRow>
      {shown.length ? shown.map(event => {
        const action = auditAction(event);
        return <Row key={event.id} className={`grid-cols-1 ${activityColumns}`}>
          <span className="text-sm text-tertiary tabular-nums">{dateTime(event.atUtc, language)}</span>
          <span className="truncate font-medium text-primary">{auditActor(event)}</span>
          <span className="min-w-0 text-sm text-secondary">{action.key ? t(action.key) : null}
            <span className="block truncate font-mono text-xs text-tertiary" title={action.raw}>{action.raw}</span></span>
          <span><Pill tone={event.outcome === 'succeeded' ? 'ok' : event.outcome === 'refused' ? 'warn' : 'bad'}>{t(`audit.outcome.${event.outcome}`)}</Pill></span>
        </Row>;
      }) : <Empty>{t('audit.none')}</Empty>}
    </Rows></ReadState>
  </Card>;
}

/** Create a credential or replace its secret. Stored values are never shown. */
function CredentialPanel({ credential, onClose }: { credential?: Credential | 'new'; onClose(): void }) {
  useLanguage();
  const action = useAction(), workers = useWorkers(), existing = credential && credential !== 'new' ? credential : undefined;
  const [provider, setProvider] = useState('github'), [type, setType] = useState('token'), [secret, setSecret] = useState(''), [worker, setWorker] = useState('');
  useEffect(() => { setSecret(''); setProvider(existing?.provider ?? 'github'); setType(existing?.type ?? 'token'); setWorker(existing?.assignedWorkerId ?? ''); }, [credential, existing]);
  async function save() {
    const base = '/api/v1/credentials';
    if (!existing) {
      if (await action.run(base, base, 'POST', { provider, type, secret: { value: secret } }, 'settings.credentialSaved')) onClose();
      return;
    }
    const path = `${base}/${encodeURIComponent(existing.id)}`;
    if (secret && !await action.run(`${path}/secret`, `${path}/secret`, 'PUT', { value: secret }, 'settings.credentialSaved')) return;
    if (worker && worker !== existing.assignedWorkerId && !await action.run(`${path}/assignment`, `${path}/assignment`, 'PUT', { workerId: worker }, 'settings.credentialAssigned')) return;
    onClose();
  }
  return <SidePanel open={!!credential} onClose={onClose} title={t(existing ? 'settings.editCredential' : 'settings.addCredential')}
    footer={<>{existing && <Button color="secondary-destructive" isDisabled={!!action.busy} onClick={() => void action.run(`revoke-${existing.id}`,
      `/api/v1/credentials/${encodeURIComponent(existing.id)}/revoke`, 'POST', {}, 'settings.credentialRevoked').then(done => { if (done) onClose(); })}>{t('settings.revoke')}</Button>}
      <Button color="secondary" onClick={onClose}>{t('common.cancel')}</Button>
      <Button color="primary" isDisabled={!!action.busy || (!existing && (!secret || !provider.trim() || !type.trim()))} onClick={() => void save()}>{t('common.save')}</Button></>}>
    {!existing && <>
      <Select label={t('settings.service')} value={provider} onChange={setProvider} options={[{ value: 'github', label: 'GitHub' }, { value: 'telegram', label: 'Telegram' }]} />
      <Field label={t('settings.kind')}><input className={inputClass} value={type} onChange={event => setType(event.target.value)} /></Field>
    </>}
    <Field label={t(existing ? 'settings.newValue' : 'settings.value')} hint={t(existing ? 'settings.newValueHint' : 'settings.valueHint')}>
      <input className={inputClass} type="password" autoComplete="off" value={secret} onChange={event => setSecret(event.target.value)} /></Field>
    {existing && <Select label={t('settings.worker')} value={worker} onChange={setWorker} hint={t('settings.workerHint')}
      options={[{ value: '', label: t('settings.unassigned') }, ...(workers.data ?? []).map(item => ({ value: item.workerId, label: item.displayName || item.workerId.slice(0, 8) }))]} />}
    <Notice>{t('settings.secretNotice')}</Notice>
  </SidePanel>;
}
