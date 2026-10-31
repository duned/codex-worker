using System.Net.Sockets;
using System.Text.Json;
using CodexWorker;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace CodexWorker.Tests;

public sealed class RecoveryDiagnosticsCliTests
{
    [Trait("TestType", "Integration")]
    [Fact]
    public async Task InstalledCliListsFiltersAndSerializesRetainedDiagnostics()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"recovery-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var port = GetFreePort();
            var configPath = Path.Combine(directory, "worker.yml");
            await File.WriteAllTextAsync(configPath, $"projects:\n  ownership: standalone\napi:\n  enabled: true\n  listenUrl: http://127.0.0.1:{port}\n");
            using var history = new ExecutionHistoryStore(Path.Combine(directory, "history.db"));
            var id = Guid.NewGuid();
            await history.CreateAsync(new ExecutionHistoryEntry(id, "sample", "owner/repo", 42, "Issue title",
                "feature/42", "main", DateTimeOffset.UtcNow.AddMinutes(-1), null, "Cancelled", null, null, null, 0, [],
                null, null, null, "interrupted", RecoveryState: "uncertain",
                StandaloneRecoveryDiagnosticJson: JsonSerializer.Serialize(new StandaloneRecoveryDiagnostic(id, 42,
                    "process-inspection-unavailable", "Process inspection is unavailable.", "blocked", DateTimeOffset.UtcNow))));
            var model = new WorkerRuntimeReadModel(new GlobalWorkerConfiguration(), [], history);
            var app = await ManagementApi.StartAsync(model, new ManagementApiSettings { ListenUrl = $"http://127.0.0.1:{port}" }, CancellationToken.None);
            Assert.NotNull(app);
            try
            {
                using var writer = new StringWriter();
                var command = new WorkerCommandLine("recovery", configPath,
                    ["blocked", "--issue", "42", "--reason", "process-inspection-unavailable"]);
                Assert.Equal(ProcessExitCodes.Success, await RecoveryDiagnosticsCli.RunAsync(command,
                    new WorkerConsole(writer, interactive: false, errorWriter: writer), CancellationToken.None, writer));
                Assert.Contains(id.ToString(), writer.ToString());
                Assert.Contains("process-inspection-unavailable", writer.ToString());

                writer.GetStringBuilder().Clear();
                command = new WorkerCommandLine("recovery", configPath, ["blocked", "--json"]);
                Assert.Equal(ProcessExitCodes.Success, await RecoveryDiagnosticsCli.RunAsync(command,
                    new WorkerConsole(writer, interactive: false, errorWriter: writer), CancellationToken.None, writer));
                using var json = JsonDocument.Parse(writer.ToString());
                Assert.Equal(id.ToString(), json.RootElement.GetProperty("items")[0].GetProperty("executionId").GetString());
                Assert.Equal("process-inspection-unavailable", json.RootElement.GetProperty("items")[0].GetProperty("reasonCode").GetString());

                writer.GetStringBuilder().Clear();
                command = new WorkerCommandLine("recovery", configPath, ["blocked", "--issue", "7"]);
                Assert.Equal(ProcessExitCodes.Success, await RecoveryDiagnosticsCli.RunAsync(command,
                    new WorkerConsole(writer, interactive: false, errorWriter: writer), CancellationToken.None, writer));
                Assert.Contains("No retained blocked recovery diagnostics", writer.ToString());
            }
            finally { await app.StopAsync(); await app.DisposeAsync(); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
