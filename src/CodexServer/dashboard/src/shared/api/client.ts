import { t } from '../i18n';
export type Validator<T> = (value: unknown) => T;
export class ApiError extends Error {
  constructor(message: string, readonly status?: number) { super(message); }
}
export const cancelled = () => new DOMException('Administration request cancelled.', 'AbortError');
const diagnostics: Record<string, string> = {
  'administration-origin-missing': t("api.originMissing"),
  'administration-host-mismatch': t("api.hostMismatch"),
  'administration-origin-mismatch': t("api.originMismatch"),
  'administration-token-invalid': t("api.tokenInvalid"),
  'administration-session-invalid': t("api.sessionInvalid")
};
export class HttpClient {
  constructor(private readonly fetcher: typeof fetch = globalThis.fetch.bind(globalThis)) {}
  async response(path: string, options: { method?: string; body?: unknown; token?: string; csrf?: string; signal: AbortSignal; stream?: boolean; session?: boolean }): Promise<Response> {
    // API paths are local and bounded; reject URL authority changes and fragments.
    if (!(path.startsWith('/api/v1/') || ['/api/status', '/api/version'].includes(path) && (options.method ?? 'GET') === 'GET') || /[\\#\r\n]/.test(path) || path.length > 2048) throw new ApiError(t("api.unsupportedAPIPath"));
    const headers = new Headers();
    if (options.token) headers.set('Authorization', `Bearer ${options.token}`);
    if (options.csrf) headers.set('X-Codex-CSRF', options.csrf);
    if (options.body !== undefined) headers.set('Content-Type', 'application/json');
    const signal = options.stream ? options.signal : AbortSignal.any([options.signal, AbortSignal.timeout(15000)]);
    try {
      const response = await this.fetcher(path, { method: options.method ?? 'GET', headers,
        body: options.body === undefined ? undefined : JSON.stringify(options.body), credentials: 'same-origin', cache: 'no-store', signal });
      if (signal.aborted) { await response.body?.cancel(); throw cancelled(); }
      if (!response.ok) {
        await response.body?.cancel();
        const diagnostic = response.headers.get('X-Codex-Administration-Error') ?? '';
        throw new ApiError(options.session && [401, 403].includes(response.status)
          ? Object.hasOwn(diagnostics, diagnostic) ? diagnostics[diagnostic] : t("api.accessRejectedWithoutAServerDiagnosticCheckManagementNetworkPolicyAndProxy")
          : t('api.httpFailed', { status: response.status }), response.status);
      }
      return response;
    } catch (error) {
      if (options.signal.aborted) throw cancelled();
      if (error instanceof ApiError) throw error;
      throw new ApiError(t("api.serverConnectionUnavailableStateMayBeStaleCheckTheConnectionAndRefresh"));
    }
  }
  async request<T>(path: string, options: Parameters<HttpClient['response']>[1], validate: Validator<T>): Promise<T> {
    const response = await this.response(path, options);
    try {
      const value: unknown = response.status === 204 ? null : await response.json();
      if (options.signal.aborted) throw cancelled();
      return validate(value);
    } catch (error) {
      if (options.signal.aborted) throw cancelled();
      if (error instanceof TypeError || error instanceof DOMException && ['AbortError', 'TimeoutError'].includes(error.name))
        throw new ApiError(t("api.serverConnectionUnavailableStateMayBeStaleCheckTheConnectionAndRefresh"));
      throw new ApiError(t("api.invalidServerDataRefreshOrCheckTheServerDeployment"));
    }
  }
}
