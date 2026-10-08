import { t } from '../i18n';
import { QueryClient } from '@tanstack/react-query';
import { ApiError, cancelled, HttpClient, type Validator } from './client';
import { sessionDocument, workers } from './validation';
export const queryKeys = { session: (generation: number) => ['private', generation] as const,
  read: (generation: number, path: string) => ['private', generation, path] as const };
export function createQueryClient() {
  return new QueryClient({ defaultOptions: {
    queries: { retry: false, staleTime: 0, gcTime: 0, refetchOnWindowFocus: false, refetchOnReconnect: false, refetchInterval: false },
    mutations: { retry: false }
  } });
}
export interface SessionState { authenticated: boolean; pending: boolean; message: string; generation: number; live: string; logoutAvailable: boolean }
// One owner for a React root/tab. Cache, requests, live stream and transient
// mutation fences all share this generation; observations never grant authority.
export class DashboardRuntime {
  private controller = new AbortController();
  private csrf = '';
  private logoutCsrf = '';
  private expiry?: ReturnType<typeof setTimeout>;
  private liveController?: AbortController;
  private liveDone: Promise<void> = Promise.resolve();
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
    this.stopLive(); clearTimeout(this.expiry); this.csrf = '';
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
      await this.queries.cancelQueries();
      void this.queries.invalidateQueries({ queryKey: queryKeys.session(generation) });
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
  stopLive() { this.liveController?.abort(); clearTimeout(this.poll); }
  private startLive() {
    this.stopLive();
    const previous = this.liveDone, generation = this.state.generation, controller = new AbortController();
    this.liveController = controller;
    const signal = AbortSignal.any([controller.signal, this.controller.signal]);
    const current = () => this.current(generation) && !signal.aborted;
    // Sequential polling of active query observers; no per-component timers.
    const poll = async () => {
      if (!current()) return;
      await this.queries.refetchQueries({ queryKey: queryKeys.session(generation), type: 'active', predicate: query => query.queryKey[2] !== '/api/v1/workers' || this.state.live !== 'Live · connected' }, { cancelRefetch: false });
      if (current()) this.poll = setTimeout(() => { void poll(); }, 5000);
    };
    this.poll = setTimeout(() => { void poll(); }, 5000);
    this.liveDone = (async () => {
      await previous; // Cancelled reader must release before a replacement opens.
      while (current()) {
        let reader: ReadableStreamDefaultReader<Uint8Array> | undefined, processing = false;
        const cancelReader = () => { void reader?.cancel().catch(() => {}); };
        try {
          const response = await this.http.response('/api/v1/events/stream', { signal, stream: true });
          if (!current()) { await response.body?.cancel(); break; }
          if (!response.body) throw new ApiError(t("api.liveStreamUnavailable"));
          reader = response.body.getReader(); signal.addEventListener('abort', cancelReader, { once: true });
          this.publish({ live: 'Live · connected' });
          const decoder = new TextDecoder(); let buffer = '';
          while (current()) {
            const chunk = await reader.read();
            if (!current() || chunk.done) break;
            buffer += decoder.decode(chunk.value, { stream: true }).replace(/\r\n/g, '\n');
            if (buffer.length > 1024 * 1024) { processing = true; throw new ApiError(t("api.eventBoundExceeded")); }
            let boundary;
            while ((boundary = buffer.indexOf('\n\n')) >= 0) {
              const event = buffer.slice(0, boundary); buffer = buffer.slice(boundary + 2);
              const data = event.split('\n').filter(line => line.startsWith('data:')).map(line => line.slice(5).trimStart()).join('\n');
              if (!data) continue;
              processing = true;
              const value = workers(JSON.parse(data));
              // Cancel an older GET so it cannot overwrite the newer snapshot.
              await this.queries.cancelQueries({ queryKey: queryKeys.read(generation, '/api/v1/workers'), exact: true });
              if (!current()) break;
              this.queries.setQueryData(queryKeys.read(generation, '/api/v1/workers'), value); processing = false;
            }
          }
        } catch (error) {
          this.rejectSession(error, generation);
          if (current() && processing) {
            this.publish({ live: t("shared.liveProcessingFailed") }); break;
          }
        } finally {
          signal.removeEventListener('abort', cancelReader);
          if (reader) { try { await reader.cancel(); } catch { /* Aborted stream already errored. */ } reader.releaseLock(); }
        }
        if (current()) {
          this.publish({ live: t("shared.liveInterrupted") });
          await new Promise<void>(resolve => {
            const finish = () => { clearTimeout(timer); signal.removeEventListener('abort', finish); resolve(); };
            const timer = setTimeout(finish, 3000); signal.addEventListener('abort', finish, { once: true });
            if (signal.aborted) finish();
          });
        }
      }
    })();
  }
  suspend = () => { this.reset(t("api.checkingAdministrationSession")); };
  dispose() { this.reset(); }
}
