namespace CodexServer;

using CodexProvisioning;
using Microsoft.Extensions.Configuration;

internal static class WorkerAdministrationCommand
{
    public static async Task RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var list = args.Length > 1 && args[1] == "list";
        var json = false;
        var limit = 100;
        var offset = 0;
        string? explicitDatabase = null;
        if (list)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 2; index < args.Length; index++)
            {
                var argument = args[index];
                if (argument == "--json" && seen.Add(argument)) json = true;
                else if (argument is "--limit" or "--offset" && seen.Add(argument) && index + 1 < args.Length &&
                    int.TryParse(args[++index], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value))
                {
                    if (argument == "--limit") limit = value;
                    else offset = value;
                }
                else if (!argument.StartsWith('-') && explicitDatabase is null) explicitDatabase = argument;
                else throw new ArgumentException("Invalid Worker list arguments.");
            }
            if (limit is < 1 or > 100 || offset is < 0 or > 10_000)
                throw new ArgumentException("Invalid Worker list page bounds.");
        }
        else
        {
            if (args.Length < 3 || args.Length > 4 || args[1] is not ("show" or "enable" or "drain" or "disable" or "revoke-token" or "revoke-delivery-token") ||
                !Guid.TryParseExact(args[2], "N", out _))
                throw new ArgumentException("Invalid Worker administration arguments.");
            if (args.Length == 4) explicitDatabase = args[3];
        }
        var configuration = new ServerConfiguration();
        var section = ServerApplication.CreateBuilder([]).Configuration.GetSection("Server");
        section.Bind(configuration);
        var serviceContext = Environment.GetEnvironmentVariable("CODEX_SERVER_OPERATOR_SERVICE_CONTEXT") == "1";
        if (explicitDatabase is null && !serviceContext && section["DataDirectory"] is null && section["DatabasePath"] is null)
            throw new InvalidDataException("Worker administration requires explicit service state configuration.");
        configuration.Validate();
        var database = explicitDatabase is not null ? Path.GetFullPath(explicitDatabase) : configuration.ResolveDatabasePath();
        var registry = new SqliteRegistryStore(database, configuration.WorkerStaleAfterSeconds,
            leaseDurationSeconds: configuration.ExecutionLeaseDurationSeconds,
            leaseRenewalIntervalSeconds: configuration.ExecutionLeaseRenewalIntervalSeconds);
        if (list)
        {
            // Listing must neither initialize nor migrate the running Server's database.
            if (!File.Exists(database)) throw new InvalidDataException("Server database is unavailable.");
            var page = await registry.GetWorkerPageAsync(limit + 1, offset, cancellationToken);
            var workers = page.Take(limit).Select(worker => new WorkerListItem(worker.WorkerId, worker.DisplayName,
                worker.WorkerVersion, worker.Platform, worker.Availability, worker.LifecycleState,
                worker.SchedulingPolicy, worker.ActiveAssignments, worker.LastSeenAtUtc)).ToArray();
            var document = new WorkerListDocument(1, limit, offset, page.Count > limit, workers);
            if (json)
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(document,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true }));
            else if (workers.Length == 0) Console.WriteLine("No registered Workers were found on this page.");
            else
            {
                Console.WriteLine("Worker ID\tName\tVersion\tPlatform\tAvailability\tLifecycle\tScheduling policy\tActive assignments\tLast seen (UTC)");
                foreach (var item in workers)
                    Console.WriteLine($"{item.WorkerId}\t{SafeCell(item.DisplayName)}\t{SafeCell(item.WorkerVersion)}\t{SafeCell(item.Platform)}\t{item.Availability}\t{SafeCell(item.LifecycleState)}\t{item.SchedulingPolicy}\t{item.ActiveAssignments}\t{item.LastSeenAtUtc:O}");
                if (document.HasMore) Console.WriteLine($"More Workers are available; use --offset {offset + limit}.");
            }
            return;
        }
        var credentials = new SqliteCredentialStore(database, Environment.GetEnvironmentVariable("CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY"));
        await registry.InitializeAsync(cancellationToken);
        await credentials.InitializeAsync(cancellationToken);
        var workerId = args[2];
        var worker = await registry.GetWorkerAsync(workerId, cancellationToken);
        if (worker is null)
        {
            Console.WriteLine("Worker was not found.");
            return;
        }
        switch (args[1])
        {
            case "show":
                var delivery = await credentials.GetWorkerDeliveryAuthorizationStatusAsync(workerId, cancellationToken);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Worker = worker, CredentialDeliveryAuthorization = delivery },
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true }));
                break;
            case "enable":
            case "drain":
            case "disable":
                var policy = args[1] switch
                {
                    "enable" => WorkerSchedulingPolicy.Enabled,
                    "drain" => WorkerSchedulingPolicy.Draining,
                    _ => WorkerSchedulingPolicy.Disabled
                };
                worker = await registry.SetWorkerSchedulingPolicyAsync(workerId, policy, cancellationToken) ?? worker;
                var drainState = worker.SchedulingPolicy == WorkerSchedulingPolicy.Draining
                    ? worker.ActiveAssignments == 0 ? "drained" : $"draining; {worker.ActiveAssignments} active assignments retain their leases"
                    : worker.SchedulingPolicy.ToLowerInvariant();
                Console.WriteLine($"Worker scheduling policy: {drainState}.");
                break;
            case "revoke-token":
                var apiTokenRevoked = await registry.RevokeWorkerTokenAsync(workerId, cancellationToken);
                Console.WriteLine(apiTokenRevoked
                    ? "Per-Worker API token revoked; calls using it are denied, and active leases may expire into recovery."
                    : "No active per-Worker API authentication token was found.");
                break;
            case "revoke-delivery-token":
                var deliveryTokenRevoked = await credentials.RevokeWorkerDeliveryTokenAsync(workerId, cancellationToken);
                Console.WriteLine(deliveryTokenRevoked
                    ? "Worker credential-delivery authorization revoked. Worker API authentication is unchanged."
                    : "No active Worker credential-delivery authorization was found.");
                break;
        }
    }

    private static string SafeCell(string value) => new(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray());

    private sealed record WorkerListItem(string WorkerId, string DisplayName, string WorkerVersion, string Platform,
        string Availability, string LifecycleState, string SchedulingPolicy, int ActiveAssignments, DateTimeOffset LastSeenAtUtc);
    private sealed record WorkerListDocument(int ContractVersion, int Limit, int Offset, bool HasMore, IReadOnlyList<WorkerListItem> Workers);
}

