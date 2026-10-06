namespace CodexServer;

using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

// Ephemeral Server-owned sessions deliberately do not survive Server restart or backup.
public sealed class AdministrationSessions(ServerConfiguration configuration, TimeProvider clock) : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private string? tokenFingerprint;
    public const string CsrfHeader = "X-Codex-CSRF";
    public string CookieName => Secure ? "__Host-CodexAdministration" : "CodexLocalAdministration";
    private bool Secure => configuration.AdministrationOrigin?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true;
    public sealed record Session(string CsrfToken, DateTimeOffset ExpiresAtUtc, CancellationTokenSource Lifetime)
    {
        public CancellationToken CancellationToken { get; } = Lifetime.Token;
    }

    public bool BearerAuthorized(HttpContext context)
    {
        var expected = configuration.ManagementToken;
        var supplied = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return !string.IsNullOrWhiteSpace(expected) && supplied.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            Equal(supplied[prefix.Length..], expected);
    }

    public bool OriginAllowed(HttpContext context) => configuration.AdministrationOrigin is { } origin &&
        string.Equals(context.Request.Headers.Origin.ToString(), origin, StringComparison.Ordinal) && HostAllowed(context);

    private bool HostAllowed(HttpContext context) => configuration.AdministrationOrigin is { } origin &&
        string.Equals(context.Request.Host.Value, new Uri(origin).Authority, StringComparison.OrdinalIgnoreCase);

    public Session? Validate(HttpContext context, bool mutation = false)
    {
        lock (gate)
        {
            Prune();
            if (!HostAllowed(context) || !context.Request.Cookies.TryGetValue(CookieName, out var id) ||
                !sessions.TryGetValue(id, out var session)) return null;
            if (mutation && (!OriginAllowed(context) || !Equal(context.Request.Headers[CsrfHeader].ToString(), session.CsrfToken))) return null;
            return session;
        }
    }

    public bool Authorized(HttpContext context)
    {
        // An explicitly supplied bearer credential never falls back to a browser session.
        if (context.Request.Headers.ContainsKey("Authorization")) return BearerAuthorized(context);
        return Validate(context, !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)) is not null;
    }

    public Session? Create(HttpContext context)
    {
        lock (gate)
        {
            Prune();
            if (sessions.Count >= 256) return null;
            Remove(context);
            var duration = TimeSpan.FromHours(8);
            var session = new Session(RandomToken(), clock.GetUtcNow() + duration, new CancellationTokenSource(duration, clock));
            var id = RandomToken();
            sessions.Add(id, session);
            context.Response.Cookies.Append(CookieName, id, CookieOptions(session.ExpiresAtUtc));
            return session;
        }
    }

    public void Logout(HttpContext context)
    {
        lock (gate) Remove(context);
        context.Response.Cookies.Delete(CookieName, CookieOptions(null));
    }

    private CookieOptions CookieOptions(DateTimeOffset? expires) => new()
    {
        HttpOnly = true, Secure = Secure, SameSite = SameSiteMode.Strict, Path = "/", Expires = expires,
        IsEssential = true
    };

    private void Remove(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out var id) && sessions.Remove(id, out var session)) End(session);
    }

    private void Prune()
    {
        var token = configuration.ManagementToken;
        var fingerprint = string.IsNullOrWhiteSpace(token) ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        if (fingerprint != tokenFingerprint)
        {
            foreach (var session in sessions.Values) End(session);
            sessions.Clear();
            tokenFingerprint = fingerprint;
        }
        foreach (var pair in sessions.Where(pair => pair.Value.ExpiresAtUtc <= clock.GetUtcNow() || pair.Value.CancellationToken.IsCancellationRequested).ToArray())
        {
            sessions.Remove(pair.Key);
            End(pair.Value);
        }
    }

    private static void End(Session session) { session.Lifetime.Cancel(); session.Lifetime.Dispose(); }
    private static string RandomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static bool Equal(string actual, string expected) => CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));
    public void Dispose()
    {
        lock (gate)
        {
            foreach (var session in sessions.Values) End(session);
            sessions.Clear();
        }
    }
}
