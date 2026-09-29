namespace CodexServer;

using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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
        return app;
    }

    public static string DisplayVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
}
