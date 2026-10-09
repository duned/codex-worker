namespace CodexProvisioning;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

/// <summary>HTTP-only installed administration adapter; ownership remains with the service.</summary>
public static class ExecutionMaintenanceCli
{
    public const string Help = """
        maintenance <inventory|inspect|cleanup|archive> <target> [--apply --confirm] [--json]
        Worker target: exact execution GUID (inventory: project name).
        Server target: ExecutionMaintenanceRequest JSON file with explicit Worker, Server/Worker
        execution IDs, assignment and generation (inventory uses only Worker ID and filters).
        Worker show <execution-id> returns exact details. Server show <operation-id> to inspect dispatch and retained reports.
        Set CODEX_ADMIN_URL to the owning service HTTP origin; Server authentication uses
        CODEX_ADMIN_TOKEN. HTTP is permitted only on loopback. Output is always JSON.
        Destructive actions default to preview; apply requires both --apply and --confirm.
        Inventory is bounded to 50 by default, at most 100 via --limit. No force or bulk deletion.
        Exit codes: 0 accepted, 2 invalid arguments, 3 authority/API refusal, 4 unavailable,
        5 partial/refused report. Re-inspect before retry; reuse operation ID for idempotency.
        """;

    private static bool HasFailure(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array) return element.EnumerateArray().Any(HasFailure);
        if (element.ValueKind != JsonValueKind.Object) return false;
        return element.EnumerateObject().Any(property =>
            property.NameEquals("outcome") && property.Value.ValueKind == JsonValueKind.String &&
            property.Value.GetString() is "refused" or "failed" || HasFailure(property.Value));
    }

    public static async Task<int> RunAsync(bool managed, IReadOnlyList<string> args, CancellationToken ct = default,
        HttpClient? client = null, TextWriter? writer = null)
    {
        writer ??= Console.Out;
        if (args is ["--help"] or ["-h"]) { writer.WriteLine(Help); return 0; }
        try
        {
            if (args.Count < 2) throw new ArgumentException();
            var action = args[0];
            var apply = false;
            var confirm = false;
            var limit = 50;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 2; i < args.Count; i++)
            {
                if (!seen.Add(args[i])) throw new ArgumentException();
                switch (args[i])
                {
                    case "--json": break;
                    case "--apply": apply = true; break;
                    case "--confirm": confirm = true; break;
                    case "--limit" when i + 1 < args.Count && int.TryParse(args[++i], out limit) && limit is >= 1 and <= 100: break;
                    default: throw new ArgumentException();
                }
            }
            if (apply != confirm || apply && action is not ("cleanup" or "archive" or "retry-report") ||
                action is not ("inventory" or "inspect" or "cleanup" or "archive" or "retry-report" or "show")) throw new ArgumentException();
            if (!managed && action == "retry-report") throw new ArgumentException();
            string path;
            object? payload = null;
            if (managed && action != "show")
            {
                var request = JsonSerializer.Deserialize<ExecutionMaintenanceRequest>(await File.ReadAllTextAsync(args[1], ct),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new ArgumentException();
                request = request with { Action = action, Apply = apply, Limit = seen.Contains("--limit") ? limit : request.Limit };
                if (!ExecutionMaintenanceProtocol.Valid(request)) throw new ArgumentException();
                payload = request;
                path = "/api/v1/maintenance/executions";
            }
            else if (managed)
            {
                if (!Guid.TryParseExact(args[1], "N", out _)) throw new ArgumentException();
                path = "/api/v1/maintenance/executions/" + args[1];
            }
            else if (action == "inventory")
                path = $"/api/executions/inventory?project={Uri.EscapeDataString(args[1])}&limit={limit}";
            else
            {
                if (!Guid.TryParse(args[1], out var id) || id == Guid.Empty) throw new ArgumentException();
                path = action == "show" ? $"/api/executions/{id}" : $"/api/executions/{id}/cleanup-inspection";
                if (action is "cleanup" or "archive")
                {
                    path = "/api/executions/cleanup";
                    payload = new { executionId = id, action, apply, limit = 1 };
                }
            }
            if (!Uri.TryCreate(Environment.GetEnvironmentVariable("CODEX_ADMIN_URL"), UriKind.Absolute, out var origin) ||
                origin.Scheme is not ("http" or "https") || origin.Scheme == "http" && !origin.IsLoopback ||
                origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
                throw new ArgumentException();
            using var ownedClient = client is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { MaxResponseContentBufferSize = 1024 * 1024 } : null;
            var http = client ?? ownedClient ?? throw new InvalidOperationException();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using var message = new HttpRequestMessage(payload is null ? HttpMethod.Get : HttpMethod.Post, new Uri(origin, path));
            if (managed && Environment.GetEnvironmentVariable("CODEX_ADMIN_TOKEN") is { Length: > 0 } token)
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (payload is not null) message.Content = JsonContent.Create(payload);
            using var response = await http.SendAsync(message, deadline.Token);
            var body = await response.Content.ReadAsStringAsync(deadline.Token);
            writer.WriteLine(JsonSerializer.Serialize(new { status = (int)response.StatusCode, response = body,
                guidance = "Re-inspect target and operation; reconcile stale leases on Server, drain before apply. Never override authority." }));
            if (!response.IsSuccessStatusCode) return 3;
            using var document = JsonDocument.Parse(body);
            return HasFailure(document.RootElement) ? 5 : 0;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { writer.WriteLine("{\"reason\":\"service-timeout\"}"); return 4; }
        catch (HttpRequestException) { writer.WriteLine("{\"reason\":\"service-unavailable\"}"); return 4; }
        catch (Exception ex) when (ex is ArgumentException or JsonException or IOException or UnauthorizedAccessException)
        { writer.WriteLine("{\"reason\":\"invalid-maintenance-arguments\"}"); return 2; }
    }
}
