namespace CodexWorker.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using CodexProvisioning;
using CodexServer;
using Microsoft.Data.Sqlite;

/// <summary>Worker-run archived branch cleanup was removed; state written by earlier builds must still load.</summary>
[Trait("TestType", "Integration")]
[Trait("Duration", "Fast")]
public sealed class ArchivedBranchMigrationTests
{
    private static ExecutionMaintenanceCommand Inventory() => new(new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
        null, null, null, null, Action: "inventory"), "succeeded", DateTimeOffset.UtcNow, "server-management:test",
        Report: new("succeeded", "inventory-observed", []));

    /// <summary>The members earlier builds always wrote, as null unless they were a branch command.</summary>
    private static JsonObject Legacy(JsonNode node, string requestPath, params string[] reportPaths)
    {
        var root = node.AsObject();
        var request = (JsonObject)requestPath.Split('.').Aggregate((JsonNode)root, (current, part) => current[part]!);
        foreach (var name in new[] { "OlderThanDays", "Branches", "Repository" }) request[name] = null;
        foreach (var path in reportPaths)
            ((JsonObject)path.Split('.').Aggregate((JsonNode)root, (current, part) => current[part]!))["Branches"] = null;
        return root;
    }

    private static JsonObject LegacyBranchCommand(string worker)
    {
        var command = Legacy(JsonSerializer.SerializeToNode(Inventory())!, "Request", "Report");
        command["Request"]!["WorkerId"] = worker;
        command["Request"]!["Action"] = "archived-branches";
        command["Request"]!["Project"] = "demo";
        command["Request"]!["Repository"] = "team/demo";
        command["Request"]!["OlderThanDays"] = 30;
        command["Request"]!["ProjectId"] = "p1";
        command["Request"]!["ProjectRevision"] = 3;
        command["Report"]!["Branches"] = new JsonArray(new JsonObject { ["Branch"] = "done/feature/1-x", ["Decision"] = "eligible" });
        return command;
    }

    [Fact]
    public async Task ServerKeepsOtherMaintenanceHistoryAndDropsBranchCommandsAndGrants()
    {
        var directory = Directory.CreateTempSubdirectory("maintenance-migration-");
        try
        {
            var database = Path.Combine(directory.FullName, "server.db");
            var kept = Inventory();
            await using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync();
                await using var setup = connection.CreateCommand();
                setup.CommandText = """
                    CREATE TABLE observer_branch_maintenance_grants (observer TEXT PRIMARY KEY, project TEXT NOT NULL, repository TEXT NOT NULL);
                    CREATE TABLE execution_maintenance_commands (id TEXT PRIMARY KEY, worker TEXT NOT NULL, active INTEGER NOT NULL, body TEXT NOT NULL);
                    INSERT INTO execution_maintenance_commands VALUES ($keptId, $worker, 0, $kept), ('branch', $worker, 1, $branch);
                    """;
                setup.Parameters.AddWithValue("$keptId", kept.Request.OperationId);
                setup.Parameters.AddWithValue("$worker", kept.Request.WorkerId);
                setup.Parameters.AddWithValue("$kept", Legacy(JsonSerializer.SerializeToNode(kept)!, "Request", "Report").ToJsonString());
                setup.Parameters.AddWithValue("$branch", LegacyBranchCommand(kept.Request.WorkerId).ToJsonString());
                await setup.ExecuteNonQueryAsync();
            }

            var store = new ExecutionMaintenanceStore(database);
            await store.InitializeAsync();
            await store.InitializeAsync();

            var listed = Assert.Single(await store.ListAsync(null, 10, 0));
            Assert.Equal(kept.Request, listed.Request);
            Assert.Null(await store.ClaimAsync(kept.Request.WorkerId));
            await using var check = new SqliteConnection($"Data Source={database}");
            await check.OpenAsync();
            await using var tables = check.CreateCommand();
            tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='observer_branch_maintenance_grants'";
            Assert.Equal(0L, await tables.ExecuteScalarAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WorkerReceiptsFromEarlierBuildsStillReadAndBranchReceiptsAreDropped()
    {
        var directory = Directory.CreateTempSubdirectory("receipt-migration-");
        try
        {
            var database = Path.Combine(directory.FullName, "history.db");
            const string endpoint = "https://server.example";
            var kept = Inventory() with { Status = "running", Report = null };
            using (var history = new ExecutionHistoryStore(database))
                await history.SaveMaintenanceReceiptAsync(endpoint, new(kept), acknowledged: false, CancellationToken.None);
            await using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync();
                await using var rewrite = connection.CreateCommand();
                rewrite.CommandText = "UPDATE execution_maintenance_receipts SET body=$body; INSERT INTO execution_maintenance_receipts VALUES('branch', $endpoint, 0, $branch);";
                var legacy = Legacy(JsonSerializer.SerializeToNode(new ExecutionMaintenanceReceipt(kept, new("succeeded", "inventory-observed", [])))!,
                    "Command.Request", "Report");
                rewrite.Parameters.AddWithValue("$body", legacy.ToJsonString());
                rewrite.Parameters.AddWithValue("$endpoint", endpoint);
                rewrite.Parameters.AddWithValue("$branch", new JsonObject { ["Command"] = LegacyBranchCommand(kept.Request.WorkerId), ["Report"] = null }.ToJsonString());
                await rewrite.ExecuteNonQueryAsync();
            }
            SqliteConnection.ClearAllPools();

            using var reopened = new ExecutionHistoryStore(database);
            var pending = await reopened.ReadPendingMaintenanceAsync(endpoint, CancellationToken.None);
            Assert.Equal(kept.Request, pending?.Command.Request);
            Assert.Null(await reopened.ReadMaintenanceReceiptAsync(endpoint, "branch", CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void SnapshotsFromStandaloneWorkersThatAdvertisedBranchMaintenanceAreStillAccepted()
    {
        var json = $$"""
            {"ContractVersion":{{ObserverProtocol.Version}},"ObserverId":"obs_0123456789abcdef0123456789abcdef","SnapshotId":"{{Guid.NewGuid():N}}",
             "ObservedAtUtc":"{{DateTimeOffset.UtcNow:O}}","Lifecycle":"running","Executions":[],
             "BranchMaintenance":[{"Project":"demo","Repository":"team/demo"}]}
            """;
        var snapshot = JsonSerializer.Deserialize<ObserverStatusSnapshot>(json);
        Assert.True(snapshot?.IsValid());
    }
}
