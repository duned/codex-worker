namespace CodexWorker.Tests;

using System.Diagnostics;
using System.Text.Json;
using CodexServer;
using Xunit.Abstractions;

public sealed class ServerGrowthTests(ITestOutputHelper output)
{
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"server-growth-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Path, recursive: true);
        }
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    public async Task InventoryGrowthRetainsAllIdentitiesAcrossReregistrationAndRestart()
    {
        using var directory = new TemporaryDirectory();
        var database = Path.Combine(directory.Path, "registry.db");
        var store = new SqliteRegistryStore(database);
        await store.InitializeAsync();
        var seeded = 0;
        foreach (var size in new[] { 10, 100, 1000 })
        {
            for (; seeded < size; seeded++)
            {
                await store.RegisterWorkerAsync(new(2, seeded.ToString("x32"), $"Worker {seeded:D4}", "0.15.0", "linux", 2, []));
                await store.CreateProjectAsync(new($"Project {seeded:D4}", $"team/repository-{seeded}", "main", "Synthetic inventory sizing"));
            }
            // Re-registration updates an identity rather than accumulating history rows.
            await store.RegisterWorkerAsync(new(2, 0.ToString("x32"), "Reconnected", "0.15.0", "linux", 2, []));
            store = new SqliteRegistryStore(database);
            var timer = Stopwatch.StartNew();
            var workers = await store.GetWorkersAsync();
            var workerMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var projects = await store.GetProjectsAsync();
            var projectMs = timer.Elapsed.TotalMilliseconds;
            Assert.Equal(size, workers.Count);
            Assert.Equal(size, projects.Count);
            Assert.Equal(size, workers.Select(w => w.WorkerId).Distinct().Count());
            Assert.Equal(size, projects.Select(p => p.Id).Distinct().Count());
            output.WriteLine(JsonSerializer.Serialize(new { size, workerMs, projectMs,
                workerBytes = JsonSerializer.SerializeToUtf8Bytes(workers).Length,
                projectBytes = JsonSerializer.SerializeToUtf8Bytes(projects).Length,
                databaseBytes = new FileInfo(database).Length }));
        }
    }
}
