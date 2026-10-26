import { t } from '../i18n';
import { QueryClient } from '@tanstack/react-query';
import { ApiError, cancelled, HttpClient, type Validator } from './client';
import { sessionDocument, serverStatus } from './validation';
export const queryKeys = { session: (generation: number) => ['private', generation] as const,
  read: (generation: number, path: string) => ['private', generation, path] as const };
export function createQueryClient() {
  return new QueryClient({ defaultOptions: {
    queries: { retry: false, staleTime: Infinity, gcTime: 10 * 60 * 1000, refetchOnWindowFocus: false, refetchOnReconnect: false, refetchInterval: false },
    mutations: { retry: false }
  } });
}
export interface SessionState { authenticated: boolean; pending: boolean; message: string; generation: number; live: string; logoutAvailable: boolean }
// One owner for a React root/tab. Cache, requests, status polling and transient
// mutation fences all share this generation; observations never grant authority.
export class DashboardRuntime {
  private controller = new AbortController();
  private csrf = '';
  private logoutCsrf = '';
  private expiry?: ReturnType<typeof setTimeout>;
  private markers?: string[];
  private visibilityCleanup?: () => void;
  private pollGeneration = 0;
  private poll?: ReturnType<typeof setTimeout>;
  private listeners = new Set<() => void>();
  private fences = new Map<string, 'pending' | 'uncertain'>();
  private state: SessionState = { authenticated: false, pending: true, message: t("api.checkingAdministrationSession"), generation: 0, live: t("shared.liveSignedOut"), logoutAvailable: false };
  constructor(readonly queries = createQueryClient(), readonly http = new HttpClient()) {}
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  snapshot = () => this.state;
  private publish(update: Partial<SessionState>) { this.state = { ...this.state, ...update }; this.listeners.forEach(listener => listener()); }
  private current(generation: number) { return generation === this.state.generation && !this.controller.signal.aborted; }
  reset(message = t("api.signInToAdministerThisServer")) {
    this.controller.abort(); this.controller = new AbortController();
    this.stopLive(); this.markers = undefined; clearTimeout(this.expiry); this.csrf = '';
    void this.queries.cancelQueries(); this.queries.clear(); this.fences.clear();
    this.publish({ authenticated: false, pending: false, message, generation: this.state.generation + 1, live: t("shared.liveSignedOut"), logoutAvailable: !!this.logoutCsrf });
  }
  async session(method: 'GET' | 'POST' | 'DELETE', token?: string) {
    if (method === 'DELETE') this.logoutCsrf = this.csrf || this.logoutCsrf;
    else this.logoutCsrf = '';
    this.reset(method === 'DELETE' ? t("api.signingOut") : t("api.checkingAdministrationSession"));
    const generation = this.state.generation;
    this.publish({ pending: true });
    try {
      const result = await this.http.request('/api/v1/administration/session', {
        method, token, csrf: method === 'DELETE' ? this.logoutCsrf : undefined, signal: this.controller.signal, session: true
      }, method === 'DELETE' ? () => null : sessionDocument);
      if (!this.current(generation)) return;
      this.logoutCsrf = '';
      if (!result) { this.publish({ pending: false, message: t("api.signedOut"), logoutAvailable: false }); return; }
      this.csrf = result.csrfToken;
      this.publish({ authenticated: true, pending: false, message: '', logoutAvailable: true });
      this.expiry = setTimeout(() => { if (this.current(generation)) this.reset(t("api.yourSessionExpiredSignInAgain")); }, Math.min(2147483647, result.expires - Date.now()));
      this.startLive();
    } catch (error) {
      if (!this.current(generation)) return;
      if (method === 'DELETE' && error instanceof ApiError && error.status === 401) {
        this.logoutCsrf = ''; this.publish({ pending: false, message: t("api.signedOut"), logoutAvailable: false }); return;
      }
      this.publish({ pending: false, logoutAvailable: !!this.logoutCsrf,
        message: method === 'DELETE' ? t("api.signOutCouldNotBeConfirmedByTheServerRetrySignOut") : error instanceof ApiError ? error.message : t("api.sessionUnavailable") });
    }
  }
  async read<T>(path: string, signal: AbortSignal, validate: Validator<T>): Promise<T> {
    if (!this.state.authenticated) throw new ApiError(t("api.administrationSignInRequired"));
    const generation = this.state.generation;
    try {
      const value = await this.http.request(path, { signal: AbortSignal.any([signal, this.controller.signal]) }, validate);
      if (!this.current(generation)) throw cancelled();
      return value;
    } catch (error) { this.rejectSession(error, generation); throw error; }
  }
  private rejectSession(error: unknown, generation: number) {
    if (this.current(generation) && error instanceof ApiError && [401, 403].includes(error.status ?? 0)) this.reset(t("api.yourSessionExpiredOrWasRejectedSignInAgain"));
  }
  // Existing Server-only verification/preview endpoints have no durable effects.
  // Force previewOnly here so this path cannot accidentally submit an Issue write.
  async preview<T>(path: string, method: 'POST' | 'PUT' | 'PATCH', body: Record<string, unknown>, signal: AbortSignal, validate: Validator<T>): Promise<T> {
    const verify = path === '/api/v1/projects/verify' && method === 'POST';
    if (!verify && !/^\/api\/v1\/projects\/[^/]+\/github\/issues(?:\/\d+(?:\/labels\/configured|\/dependencies\/blocked-by)?)?$/.test(path)) throw new ApiError(t("api.unsupportedPreviewPath"));
    if (!this.state.authenticated) throw new ApiError(t("api.administrationSignInRequired"));
    const generation = this.state.generation;
    try {
      const value = await this.http.request(path, { method, body: verify ? body : { ...body, previewOnly: true }, csrf: this.csrf, signal: AbortSignal.any([signal, this.controller.signal]) }, validate);
      if (!this.current(generation)) throw cancelled();
      return value;
    } catch (error) { this.rejectSession(error, generation); throw error; }
  }
  locked(resource: string) { return this.fences.has(resource); }
  async mutate<T>(resource: string, path: string, method: 'POST' | 'PUT' | 'PATCH' | 'DELETE', body: unknown, validate: Validator<T>, authoritativeCheck: (signal: AbortSignal) => Promise<void>) {
    if (!this.state.authenticated || this.locked(resource)) throw new ApiError(t("api.refreshAuthoritativeStateBeforeRetrying"));
    const generation = this.state.generation, signal = this.controller.signal;
    this.fences.set(resource, 'pending'); this.publish({});
    let submitted = false;
    try {
      // A fresh, uncached read checks revision/consent/eligibility before sending.
      // The Server still enforces all authorization and lifecycle constraints.
      await authoritativeCheck(signal);
      if (!this.current(generation)) throw cancelled();
      submitted = true;
      const value = await this.http.request(path, { method, body, csrf: this.csrf, signal }, validate);
      if (!this.current(generation)) throw cancelled();
      this.fences.delete(resource);
      await this.invalidateMutation(path, generation);
      return value;
    } catch (error) {
      if (this.current(generation)) {
        if (submitted) this.fences.set(resource, 'uncertain'); else this.fences.delete(resource);
      }
      this.rejectSession(error, generation); throw error;
    } finally { if (this.current(generation)) this.publish({}); }
  }
  async reconcile(resource: string, authoritativeRead: (signal: AbortSignal) => Promise<void>) {
    if (!this.state.authenticated || this.fences.get(resource) === 'pending') throw new ApiError(t("api.reconciliationUnavailable"));
    const generation = this.state.generation;
    await authoritativeRead(this.controller.signal);
    if (!this.current(generation)) throw cancelled();
    this.fences.delete(resource); this.publish({});
  }
  private async invalidateMutation(path: string, generation: number) {
    const root = path.split('/')[3];
    const affected = new Set([root]);
    // Scheduling/configuration actions also affect their Server projections.
    if (['projects', 'workers', 'nodes', 'executions', 'provisioning'].includes(root)) {
      affected.add('executions'); affected.add('workers'); affected.add('nodes');
    }
    await this.queries.invalidateQueries({ queryKey: queryKeys.session(generation),
      predicate: query => {
        const cachedPath = String(query.queryKey[2]);
        const category = cachedPath.split('/')[3]?.split('?')[0];
        if (!affected.has(category)) return false;
        const targetId = path.split('/')[4]?.split('?')[0];
        const cachedId = cachedPath.split('/')[4]?.split('?')[0];
        return !cachedId || category === root && (!targetId || cachedId === targetId);
      } });
  }
  /** One status round; collection invalidation never expands identifiers into detail reads. */
  async checkStatus() {
    const generation = this.state.generation;
    const status = await this.queries.fetchQuery({ queryKey: queryKeys.read(generation, '/api/status'),
      staleTime: 0, queryFn: ({ signal }) => this.read('/api/status', signal, serverStatus) });
    if (!this.current(generation)) return;
    if (!status.freshness || !status.persistenceAvailable) throw new ApiError(t('api.invalidServerDataRefreshOrCheckTheServerDeployment'));
    const next = [status.startedAtUtc, String(status.freshness.projects), String(status.freshness.workers), String(status.freshness.executions)];
    const previous = this.markers;
    this.markers = next;
    for (const [index, category] of ['projects', 'workers', 'executions'].entries()) {
      if (!previous || previous[0] === next[0] && previous[index + 1] === next[index + 1]) continue;
      // Details become stale for their next navigation, but only collections refresh now.
      await this.queries.invalidateQueries({ queryKey: queryKeys.session(generation), refetchType: 'none',
        predicate: query => String(query.queryKey[2]).split('?')[0].startsWith(`/api/v1/${category}/`) || String(query.queryKey[2]).split('?')[0] === `/api/v1/${category}` });
    }
    // Retry failed initial loads as well as invalidated refreshes. Both use the
    // status loop's backoff, even when the Server marker has not changed.
    await this.queries.refetchQueries({ queryKey: queryKeys.session(generation), type: 'active',
      predicate: query => (query.state.isInvalidated || query.state.status === 'error') && /^\/api\/v1\/(projects|workers|executions)$/.test(String(query.queryKey[2]).split('?')[0])
    }, { cancelRefetch: false, throwOnError: true });
  }
  stopLive() { this.pollGeneration++; clearTimeout(this.poll); this.visibilityCleanup?.(); this.visibilityCleanup = undefined; }
  private startLive() {
    this.stopLive();
    const generation = this.state.generation, pollGeneration = this.pollGeneration;
    const current = () => this.current(generation) && pollGeneration === this.pollGeneration;
    let failures = 0, running = false;
    const visible = () => typeof document === 'undefined' || document.visibilityState !== 'hidden';
    const poll = async () => {
      if (!current() || running || !visible()) return;
      running = true;
      try { await this.checkStatus(); failures = 0; if (current()) this.publish({ live: 'Live · connected' }); }
      catch { failures = Math.min(failures + 1, 5); if (current()) this.publish({ live: t('shared.liveInterrupted') }); }
      finally {
        running = false;
        if (current() && visible()) this.poll = setTimeout(() => { void poll(); },
          failures ? Math.min(60000, 5000 * 2 ** failures) * (0.8 + Math.random() * 0.4) : 5000);
      }
    };
    const visibility = () => { clearTimeout(this.poll); if (visible()) void poll(); };
    if (typeof document !== 'undefined') {
      document.addEventListener('visibilitychange', visibility);
      this.visibilityCleanup = () => document.removeEventListener('visibilitychange', visibility);
    }
    this.poll = setTimeout(() => { void poll(); }, 0);
  }
  suspend = () => { this.reset(t("api.checkingAdministrationSession")); };
  dispose() { this.reset(); }
}
