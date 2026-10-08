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
import { Input } from '../../untitled/components/base/input/input';
type Edit = { kind: 'create' } | { kind: 'replace' | 'assign' | 'revoke'; before: Credential };
export function SettingsPage() {
  const location = useLocation(), session = useSession();
  // Route/session replacement destroys all private fields, codes and local consent.
  return session.authenticated ? <SettingsView key={`${session.generation}:${location.pathname}:${location.search}`} /> : <PageHeading title="Settings" />;
}
function SettingsView() {
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
    <PageHeading title="Settings" actions={<ThemeControl />} />
    <ServerPreparation />
    <section aria-label="Credential metadata" className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3"><h2 className="text-lg font-semibold text-primary">Credential metadata</h2>
        <div className="flex flex-wrap gap-3"><Button color="secondary" onPress={() => { void list.refetch(); }}>Refresh metadata</Button><Button isDisabled={owner.locked()} onPress={() => open({ kind: 'create' })}>Add credential</Button></div>
      </div>
      <p className="text-sm text-secondary">Metadata never retrieves a secret. Assignment permits delivery to one registered Worker with separate delivery authorization. Revocation stops future delivery; provider-side permissions remain unchanged.</p>
      {list.error && <Notice error>{list.error}</Notice>}{registered.error && <Notice error>{registered.error}</Notice>}
      {!list.data && !list.error && <Notice>Loading credential metadata…</Notice>}
      {list.data?.length === 0 && <Notice>No Server-managed credentials are registered.</Notice>}
      {list.data?.map(c => <article key={c.id} className="space-y-3 rounded-xl border border-secondary bg-primary p-5">
        <div className="flex flex-wrap items-start justify-between gap-3"><ResourceIdentity name={`${c.provider} · ${c.type}`} id={c.id} /><StatusBadge tone={c.status === 'Ready' ? 'success' : c.status === 'NeedsReprovision' ? 'warning' : 'gray'}>{c.status}</StatusBadge></div>
        <p className="text-sm text-secondary">Version {c.version} · {c.assignedWorkerId ? `Assigned to ${registered.data?.find(w => w.workerId === c.assignedWorkerId)?.displayName ?? c.assignedWorkerId}` : 'Unassigned'}</p>
        <div className="flex flex-wrap gap-3"><Button color="secondary" href={`/settings/${encodeURIComponent(c.id)}${location.search}`}>Show metadata</Button>
          <Button color="secondary" isDisabled={owner.locked(c.id) || c.status !== 'Ready' || !registered.data?.length} onPress={() => open({ kind: 'assign', before: c })}>Assign</Button>
          <Button color="secondary" isDisabled={owner.locked(c.id) || !['Ready', 'NeedsReprovision'].includes(c.status)} onPress={() => open({ kind: 'replace', before: c })}>Replace secret</Button>
          <Button color="secondary-destructive" isDisabled={owner.locked(c.id) || !['Ready', 'NeedsReprovision'].includes(c.status)} onPress={() => open({ kind: 'revoke', before: c })}>Revoke</Button>
        </div><AdvancedDisclosure title="Credential resource details"><p>Updated {timestamp(c.updatedAtUtc)}{c.revokedAtUtc && ` · Revoked ${timestamp(c.revokedAtUtc)}`}</p>{c.assignedWorkerId && <Button color="link-gray" href={`/workers/${encodeURIComponent(c.assignedWorkerId)}`}>Assigned Worker</Button>}</AdvancedDisclosure>
      </article>)}
      {owner.message && <Notice>{owner.message}</Notice>}
      {Object.keys(owner.attempts).filter(key => owner.locked(key.slice('credential:'.length))).map(key => <Notice key={key}>A credential action is unconfirmed. No secret input was retained. <Button color="secondary" onPress={() => { void owner.reconcile(key); }}>Refresh authoritative credential state</Button></Notice>)}
    </section>
    {resourceId && <CredentialDetail id={resourceId} onClose={() => navigate(`/settings${location.search}`)} />}
    {edit && <ActionDialog isOpen title={edit.kind === 'create' ? 'Add Server-managed credential' : edit.kind === 'replace' ? 'Replace credential secret' : edit.kind === 'assign' ? 'Assign credential' : 'Revoke credential'}
      description={edit.kind === 'revoke' ? 'Stop future Server delivery. Provider-side authorization and secrets already delivered to nodes are unchanged.' : edit.kind === 'assign' ? 'Select a registered Worker. Delivery requires its separately authorized delivery credential; node login and scheduling permission are unchanged.' : 'The input is sent once to protected Server storage and cleared before submission. Metadata cannot retrieve it.'}
      onClose={close} actionLabel={edit.kind === 'revoke' ? 'Revoke' : 'Save'} destructive={edit.kind === 'revoke'} onSubmit={submit}
      disabled={edit.kind === 'create' ? !provider.trim() || !type.trim() || !secret.trim() : edit.kind === 'replace' ? !secret.trim() : edit.kind === 'assign' ? !workerId : false}>
      {edit.kind === 'create' && <><Input label="Provider" value={provider} onChange={setProvider} maxLength={80} /><Input label="Credential type" value={type} onChange={setType} maxLength={80} /></>}
      {(edit.kind === 'create' || edit.kind === 'replace') && <Input label="Secret" type="password" autoComplete="off" value={secret} onChange={setSecret} maxLength={16384} />}
      {edit.kind === 'assign' && <div role="group" aria-label="Select Worker" className="space-y-2"><p className="text-sm font-medium text-secondary">Worker</p>{registered.data?.map(w => <Button key={w.workerId} color="secondary" aria-pressed={workerId === w.workerId} onPress={() => setWorkerId(w.workerId)}>{w.displayName ?? w.workerId}{workerId === w.workerId && ' · Selected'}</Button>)}</div>}
    </ActionDialog>}
  </div>;
}
function CredentialDetail({ id, onClose }: { id: string; onClose(): void }) {
  const metadata = useApiRead(credentialPath(id), credential);
  const value = metadata.data?.id === id ? metadata.data : undefined;
  return <FormDialog isOpen title="Credential metadata" description="Only resource metadata is requested; secret delivery is restricted to the assigned authorized Worker." onClose={onClose}>
    {metadata.error ? <Notice error>Credential unavailable or deleted. {metadata.error}</Notice> : value ? <dl className="space-y-2 break-all text-sm text-secondary">{([
      ['Provider', value.provider], ['Type', value.type], ['ID', value.id], ['Secret reference', value.secretReference], ['Status', value.status],
      ['Assigned Worker', value.assignedWorkerId ?? 'Unassigned'], ['Version', value.version], ['Created', value.createdAtUtc], ['Updated', value.updatedAtUtc], ['Revoked', value.revokedAtUtc ?? 'Not revoked']
    ] as const).map(([label, item]) => <div key={label}><dt className="font-semibold">{label}</dt><dd>{item}</dd></div>)}</dl> : <Notice>Loading credential metadata…</Notice>}
    <Button color="secondary" onPress={() => { void metadata.refetch(); }}>Refresh detail</Button><Button autoFocus color="secondary" onPress={onClose}>Close</Button>
  </FormDialog>;
}
