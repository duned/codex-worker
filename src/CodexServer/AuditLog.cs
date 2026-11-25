namespace CodexServer;

using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>Who performed an administrative action. The management token is shared, so the name is what the
/// operator declared when signing in (or in <see cref="Header"/> for bearer clients): attribution, not authentication.</summary>
public sealed record OperatorIdentity(string Channel, string? Name)
{
    public const string Header = "X-Codex-Operator";
    public const int MaximumNameLength = 64;

    /// <summary>A bounded printable name, or null when none (or an unusable one) was supplied.</summary>
    public static string? Normalize(string? name)
    {
        var value = name?.Trim();
        return string.IsNullOrEmpty(value) || value.Length > MaximumNameLength || value.Any(char.IsControl) ? null : value;
    }

    /// <summary>The name in <see cref="Header"/>. Browsers percent-encode it so any name fits in a header.</summary>
    public static string? FromHeader(HttpContext context)
    {
        var value = context.Request.Headers[Header].ToString();
        try { return Normalize(Uri.UnescapeDataString(value)); }
        catch (UriFormatException) { return null; }
    }

    /// <summary>Compact form for records that keep a single actor string.</summary>
    public string Describe() => Name is null ? Channel : $"{Channel}:{Name}";
}

public sealed record AuditEvent(long Id, DateTimeOffset AtUtc, string Channel, string? Operator, string Method, string Action,
    int Status, string Outcome, string? RequestId);
public sealed record AuditPage(IReadOnlyList<AuditEvent> Events, long? NextBefore);

/// <summary>Append-only record of administrative changes: who, what, when and the result. Never request bodies,
/// query strings or credentials. Bounded by age and count.</summary>
public sealed partial class AuditLogStore(string databasePath, TimeProvider? clock = null)
{
    public const int RetainedEvents = 20_000;
    public static readonly TimeSpan RetainedAge = TimeSpan.FromDays(365);
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync(ct);
        return connection;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS audit_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT, at_utc TEXT NOT NULL, channel TEXT NOT NULL, operator TEXT NULL,
                method TEXT NOT NULL, action TEXT NOT NULL, status INTEGER NOT NULL, request_id TEXT NULL);
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordAsync(OperatorIdentity actor, string method, string action, int status, string? requestId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (at_utc, channel, operator, method, action, status, request_id)
                VALUES ($at, $channel, $operator, $method, $action, $status, $request);
            DELETE FROM audit_events WHERE at_utc < $cutoff OR id <= last_insert_rowid() - $retained;
            """;
        var now = Now;
        command.Parameters.AddWithValue("$at", now.ToString("O"));
        command.Parameters.AddWithValue("$channel", Bound(actor.Channel, 16));
        command.Parameters.AddWithValue("$operator", (object?)OperatorIdentity.Normalize(actor.Name) ?? DBNull.Value);
        command.Parameters.AddWithValue("$method", Bound(method, 10));
        command.Parameters.AddWithValue("$action", Bound(action, 300));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$request", (object?)(requestId is null ? null : Bound(requestId, 100)) ?? DBNull.Value);
        command.Parameters.AddWithValue("$cutoff", (now - RetainedAge).ToString("O"));
        command.Parameters.AddWithValue("$retained", RetainedEvents);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<AuditPage> ListAsync(int limit, long? before, string? operatorName, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, at_utc, channel, operator, method, action, status, request_id FROM audit_events
            WHERE ($before IS NULL OR id < $before) AND ($operator IS NULL OR operator = $operator COLLATE NOCASE)
            ORDER BY id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$before", (object?)before ?? DBNull.Value);
        command.Parameters.AddWithValue("$operator", (object?)OperatorIdentity.Normalize(operatorName) ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit + 1);
        var events = new List<AuditEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var status = reader.GetInt32(6);
            events.Add(new(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5),
                status, Outcome(status), reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        var more = events.Count > limit;
        if (more) events.RemoveAt(events.Count - 1);
        return new(events, more ? events[^1].Id : null);
    }

    public static string Outcome(int status) => status switch { < 400 => "succeeded", < 500 => "refused", _ => "failed" };

    private static string Bound(string value, int length)
    {
        var printable = new string(value.Where(character => !char.IsControl(character)).ToArray());
        return printable.Length <= length ? printable : printable[..length];
    }

    /// <summary>Records every authorized administrative mutation under <c>/api</c> after it runs. Worker,
    /// observer and sign-in routes have their own identity and are excluded; previews change nothing.</summary>
    public static async Task AuditAdministrationAsync(HttpContext context, Func<Task> next)
    {
        var request = context.Request;
        var path = request.Path.Value ?? "/";
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method) ||
            !request.Path.StartsWithSegments("/api") || request.Path.StartsWithSegments("/api/v1/administration/session") ||
            path.EndsWith("/verify", StringComparison.Ordinal))
        {
            await next();
            return;
        }
        var actor = Actor(context);
        if (actor is null || await IsPreviewAsync(context, path))
        {
            await next();
            return;
        }
        var status = StatusCodes.Status500InternalServerError;
        try
        {
            await next();
            status = context.Response.StatusCode;
        }
        finally
        {
            await RecordSafelyAsync(context, actor, request.Method, path, status);
        }
    }

    /// <summary>The identity of an authorized management caller, or null for anyone else (Workers, observers, anonymous).</summary>
    public static OperatorIdentity? Actor(HttpContext context)
    {
        var sessions = context.RequestServices.GetRequiredService<AdministrationSessions>();
        if (context.Request.Headers.ContainsKey("Authorization"))
            return sessions.BearerAuthorized(context)
                ? new OperatorIdentity("api", OperatorIdentity.FromHeader(context)) : null;
        return sessions.Validate(context, mutation: true) is { } session ? new OperatorIdentity("dashboard", session.Operator) : null;
    }

    internal static async Task RecordSafelyAsync(HttpContext context, OperatorIdentity actor, string method, string action, int status)
    {
        try
        {
            // Recorded even when the caller disconnects: the change may already have happened.
            await context.RequestServices.GetRequiredService<AuditLogStore>()
                .RecordAsync(actor, method, action, status, context.TraceIdentifier, CancellationToken.None);
        }
        catch (SqliteException ex)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger<AuditLogStore>()
                .LogWarning("Audit event for {Method} {Action} could not be recorded: SQLite error {Code}", method, action, ex.SqliteErrorCode);
        }
    }

    // GitHub Issue previews post a body with previewOnly: true and have no durable effect.
    private static async Task<bool> IsPreviewAsync(HttpContext context, string path)
    {
        const int limit = 256 * 1024;
        if (!IssueWritePath().IsMatch(path) || context.Request.ContentLength is 0 or > limit) return false;
        // Buffered (and bounded) so the endpoint still reads the whole body afterwards.
        context.Request.EnableBuffering(bufferThreshold: 32 * 1024, bufferLimit: limit);
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("previewOnly", out var preview) && preview.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return false; }
        finally { if (context.Request.Body.CanSeek) context.Request.Body.Position = 0; }
    }

    [GeneratedRegex("^/api/v1/projects/[^/]+/github/issues(?:/[0-9]+(?:/.*)?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex IssueWritePath();
}
