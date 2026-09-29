namespace CodexServer;

using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;

public sealed record ServerStatus(string State, string Version, DateTimeOffset StartedAtUtc);
public sealed record ServerVersion(string Version, string Product);
public sealed record ServerHealth(string Status, bool PersistenceAvailable);

public interface IServerHealthService
{
    Task<ServerHealth> GetHealthAsync(CancellationToken cancellationToken);
}

public sealed class ServerHealthService(IRegistryStore registryStore) : IServerHealthService
{
    public async Task<ServerHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        var available = await registryStore.IsAvailableAsync(cancellationToken);
        return new ServerHealth(available ? "healthy" : "unhealthy", available);
    }
}

public static class ServerApplication
{
    public static async Task<WebApplication> BuildAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder(args);
        var configuration = new ServerConfiguration();
        builder.Configuration.GetSection("Server").Bind(configuration);
        configuration.Validate();
        builder.WebHost.UseUrls(configuration.ListenUrl);
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton<IRegistryStore>(_ => new SqliteRegistryStore(configuration.ResolveDatabasePath()));
        builder.Services.AddSingleton<IServerHealthService, ServerHealthService>();
        builder.Services.AddSingleton(new ServerStatus("ready", DisplayVersion, DateTimeOffset.UtcNow));

        var app = builder.Build();
        var persistence = app.Services.GetRequiredService<IRegistryStore>();
        await persistence.InitializeAsync(cancellationToken);
        app.MapGet("/api/status", (ServerStatus status) => Results.Ok(status));
        app.MapGet("/api/version", () => Results.Ok(new ServerVersion(DisplayVersion, "Codex Server")));
        app.MapGet("/health", async (IServerHealthService healthService, HttpContext context) =>
        {
            try
            {
                var health = await healthService.GetHealthAsync(context.RequestAborted);
                return health.PersistenceAvailable ? Results.Ok(health) : Results.Json(health, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
            {
                return Results.Json(new ServerHealth("unhealthy", false), statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
        app.MapPut("/api/v1/workers/{workerId}", async (string workerId, WorkerRegistrationRequest request,
            HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!Authorized(context, settings)) return Results.Unauthorized();
            if (!string.Equals(workerId, request.WorkerId, StringComparison.Ordinal) || !Valid(request))
                return Results.BadRequest(new { error = "Invalid worker registration contract." });
            await store.RegisterWorkerAsync(request, context.RequestAborted);
            return Results.Ok(await store.GetWorkerAsync(workerId, context.RequestAborted));
        });
        app.MapGet("/api/v1/workers", async (IRegistryStore store, CancellationToken ct) => Results.Ok(await store.GetWorkersAsync(ct)));
        app.MapGet("/api/v1/workers/{workerId}", async (string workerId, IRegistryStore store, CancellationToken ct) =>
        {
            var worker = await store.GetWorkerAsync(workerId, ct);
            return worker is null ? Results.NotFound() : Results.Ok(worker);
        });
        return app;
    }

    private static bool Authorized(HttpContext context, ServerConfiguration configuration)
    {
        var expected = configuration.RegistrationToken;
        var supplied = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (string.IsNullOrWhiteSpace(expected) || !supplied.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var actualBytes = Encoding.UTF8.GetBytes(supplied[prefix.Length..]);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return actualBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    private static bool Valid(WorkerRegistrationRequest request) => request.ContractVersion == 1 &&
        Guid.TryParseExact(request.WorkerId, "N", out _) && !string.IsNullOrWhiteSpace(request.DisplayName) &&
        request.DisplayName.Length <= 200 && !string.IsNullOrWhiteSpace(request.WorkerVersion) && request.WorkerVersion.Length <= 100 &&
        !string.IsNullOrWhiteSpace(request.Platform) && request.Platform.Length <= 300 && request.Capacity is >= 1 and <= 8 &&
        request.Capabilities is not null && request.Capabilities.Count <= 32 &&
        request.Capabilities.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 100);

    public static string DisplayVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
}
