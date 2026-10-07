export type Validator<T> = (value: unknown) => T;
export class ApiError extends Error {
  constructor(message: string, readonly status?: number) { super(message); }
}
export const cancelled = () => new DOMException('Administration request cancelled.', 'AbortError');
const diagnostics: Record<string, string> = {
  'administration-origin-missing': 'Browser login is not configured. Set Server AdministrationOrigin to the exact external HTTPS origin and restart the Server. Token rotation will not fix this.',
  'administration-host-mismatch': 'Server administration Host mismatch. Match the proxy upstream Host to AdministrationOrigin. Token rotation will not fix this.',
  'administration-origin-mismatch': 'Browser origin does not match AdministrationOrigin. Use the configured HTTPS URL or correct the Server configuration and restart. Token rotation will not fix this.',
  'administration-token-invalid': 'Management token was rejected. Retrieve the existing token privately on the Server.',
  'administration-session-invalid': 'No valid administration session. Sign in with the existing management token.'
};
export class HttpClient {
  constructor(private readonly fetcher: typeof fetch = globalThis.fetch.bind(globalThis)) {}
  async response(path: string, options: { method?: string; body?: unknown; token?: string; csrf?: string; signal: AbortSignal; stream?: boolean; session?: boolean }): Promise<Response> {
    // API paths are local and bounded; reject URL authority changes and fragments.
    if (!(path.startsWith('/api/v1/') || path === '/api/status' && (options.method ?? 'GET') === 'GET') || /[\\#\r\n]/.test(path) || path.length > 2048) throw new ApiError('Unsupported API path.');
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
          ? Object.hasOwn(diagnostics, diagnostic) ? diagnostics[diagnostic] : 'Access rejected without a Server diagnostic. Check management network policy and proxy routing.'
          : `Request failed (HTTP ${response.status}). State may be stale; refresh before retrying.`, response.status);
      }
      return response;
    } catch (error) {
      if (options.signal.aborted) throw cancelled();
      if (error instanceof ApiError) throw error;
      throw new ApiError('Server connection unavailable. State may be stale; check the connection and refresh.');
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
        throw new ApiError('Server connection unavailable. State may be stale; check the connection and refresh.');
      throw new ApiError('Invalid Server data. Refresh or check the Server deployment.');
    }
  }
}
