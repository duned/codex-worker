namespace CodexWorker.Tests;

using CodexServer;
using System.Text.Json;

[Collection("ServerTokenEnvironment")]
public sealed class ServerCommandHelpTests
{
    public static TheoryData<string> Contexts => new()
    {
        "status", "diagnostics", "config", "config show", "config validate", "config set",
        "projects", "projects list", "projects show", "projects create", "projects update", "projects enable", "projects disable", "projects delete",
        "executions", "executions list", "executions show", "executions cancel", "executions reconcile",
        "worker", "worker list", "worker show", "worker enable", "worker drain", "worker disable", "worker revoke-token", "worker revoke-delivery-token",
        "worker-token", "worker-token create", "worker-token revoke", "worker-token revoke-worker",
        "backup", "backup export", "backup validate", "backup restore",
        "credential", "credential list", "credential show", "credential create", "credential assign", "credential replace", "credential revoke",
        "provision", "provision list", "provision show", "provision create", "provision cancel", "provision reconcile",
        "github", "github access", "github issues", "github issue", "github relationships", "github graph", "github enqueue", "github refresh",
        "github create", "github update", "github label", "github dependency", "github parent", "github sub-issues", "github dependency-batch"
    };

    [Theory]
    [MemberData(nameof(Contexts))]
    public async Task EveryContextSupportsBothAliasesWithoutReadingConfiguration(string context)
    {
        // Even invalid configuration must not prevent help or cause local state creation.
        var previous = Environment.GetEnvironmentVariable("Server__WorkerStaleAfterSeconds");
        Environment.SetEnvironmentVariable("Server__WorkerStaleAfterSeconds", "invalid");
        try
        {
            var longHelp = await RunAsync([.. context.Split(' '), "--help"]);
            var shortHelp = await RunAsync([.. context.Split(' '), "-h"]);
            Assert.Equal(0, longHelp.Code);
            Assert.Equal(longHelp, shortHelp);
            Assert.Empty(longHelp.Error);
            Assert.Contains($"Usage: codex-server {context} ", longHelp.Output);
            Assert.DoesNotContain("Commands:", longHelp.Output);
            Assert.DoesNotContain("Usage: CodexServer", longHelp.Output);
        }
        finally { Environment.SetEnvironmentVariable("Server__WorkerStaleAfterSeconds", previous); }
    }

    [Theory]
    [InlineData("worker", "show")]
    [InlineData("backup", "export")]
    [InlineData("projects", "update")]
    [InlineData("executions", "reconcile")]
    [InlineData("github", "access")]
    [InlineData("credential", "replace")]
    [InlineData("provision", "create")]
    [InlineData("worker-token", "revoke")]
    [InlineData("config", "set")]
    public async Task MissingLeafArgumentsPointToLeafHelp(string family, string operation)
    {
        var result = await RunAsync([family, operation]);
        Assert.Equal(2, result.Code);
        Assert.Contains($"codex-server {family} {operation} --help", result.Error);
        Assert.DoesNotContain("Usage: CodexServer", result.Error);
    }

    [Theory]
    [InlineData("worker")]
    [InlineData("backup")]
    [InlineData("projects")]
    [InlineData("executions")]
    [InlineData("github")]
    [InlineData("credential")]
    [InlineData("provision")]
    [InlineData("worker-token")]
    [InlineData("config")]
    public async Task UnknownOperationsPointToFamilyHelpWithoutEchoingArguments(string family)
    {
        var result = await RunAsync([family, "private-secret"]);
        Assert.Equal(2, result.Code);
        Assert.Contains($"codex-server {family} --help", result.Error);
        Assert.DoesNotContain("private-secret", result.Error);
    }

    [Fact]
    public async Task RootHelpIsAnIndexAndAliasesMatch()
    {
        var help = await RunAsync(["--help"]);
        Assert.Equal(0, help.Code);
        Assert.Equal(help, await RunAsync(["-h"]));
        Assert.Contains("Commands:", help.Output);
        Assert.Contains("worker", help.Output);
        Assert.Contains("<command> --help", help.Output);
        Assert.DoesNotContain("<archive-path>", help.Output);
        Assert.DoesNotContain("<expected-revision>", help.Output);
    }

    [Fact]
    public async Task WorkerListProvidesBoundedIdentitySummariesAndPagination()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "registry.db");
        var registry = new SqliteRegistryStore(database);
        await registry.InitializeAsync();
        var first = new string('a', 32);
        var second = new string('b', 32);
        await registry.RegisterWorkerAsync(new(2, second, "second", "1.0", "test", 1, [new("tool", "git")]));
        await registry.RegisterWorkerAsync(new(2, first, "first\tworker", "1.0", "test", 1, []));
        await registry.SetWorkerSchedulingPolicyAsync(first, WorkerSchedulingPolicy.Draining);
        var text = await RunAsync(["worker", "list", database]);
        Assert.Equal(0, text.Code);
        Assert.Contains(first, text.Output);
        Assert.Contains(second, text.Output);
        Assert.Contains("Draining", text.Output);
        Assert.Contains("first worker", text.Output);
        var json = await RunAsync(["worker", "list", "--json", "--limit", "1", database]);
        Assert.Equal(0, json.Code);
        using var document = JsonDocument.Parse(json.Output);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("contractVersion").GetInt32());
        Assert.Equal(1, root.GetProperty("limit").GetInt32());
        Assert.Equal(0, root.GetProperty("offset").GetInt32());
        Assert.True(root.GetProperty("hasMore").GetBoolean());
        var item = Assert.Single(root.GetProperty("workers").EnumerateArray());
        Assert.Equal(first, item.GetProperty("workerId").GetString());
        Assert.Equal("Draining", item.GetProperty("schedulingPolicy").GetString());
        Assert.Equal(new[] { "workerId", "displayName", "workerVersion", "platform", "availability", "lifecycleState", "schedulingPolicy", "activeAssignments", "lastSeenAtUtc" },
            item.EnumerateObject().Select(property => property.Name).ToArray());
        var next = await RunAsync(["worker", "list", "--json", "--limit", "1", "--offset", "1", database]);
        using var nextDocument = JsonDocument.Parse(next.Output);
        Assert.False(nextDocument.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.Equal(second, Assert.Single(nextDocument.RootElement.GetProperty("workers").EnumerateArray()).GetProperty("workerId").GetString());
        Assert.Equal(WorkerSchedulingPolicy.Draining, (await registry.GetWorkerAsync(first))?.SchedulingPolicy);
    }

    [Fact]
    public async Task WorkerListUsesConfiguredServiceDatabaseWhenPathIsOmitted()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "configured.db");
        var registry = new SqliteRegistryStore(database);
        await registry.InitializeAsync();
        var workerId = new string('c', 32);
        await registry.RegisterWorkerAsync(new(2, workerId, "configured worker", "1.0", "test", 1, []));
        var previousDirectory = Environment.GetEnvironmentVariable("Server__DataDirectory");
        var previousDatabase = Environment.GetEnvironmentVariable("Server__DatabasePath");
        try
        {
            Environment.SetEnvironmentVariable("Server__DataDirectory", temporary.Path);
            Environment.SetEnvironmentVariable("Server__DatabasePath", database);
            var result = await RunAsync(["worker", "list", "--json"]);
            Assert.Equal(0, result.Code);
            using var document = JsonDocument.Parse(result.Output);
            Assert.Equal(workerId, Assert.Single(document.RootElement.GetProperty("workers").EnumerateArray()).GetProperty("workerId").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("Server__DataDirectory", previousDirectory);
            Environment.SetEnvironmentVariable("Server__DatabasePath", previousDatabase);
        }
    }

    [Fact]
    public async Task EmptyWorkerListIsClearAndDoesNotCreateMissingDatabase()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "registry.db");
        var missing = await RunAsync(["worker", "list", database]);
        Assert.Equal(1, missing.Code);
        Assert.False(File.Exists(database));
        await new SqliteRegistryStore(database).InitializeAsync();
        var text = await RunAsync(["worker", "list", database]);
        Assert.Equal(0, text.Code);
        Assert.Contains("No registered Workers", text.Output);
        var json = await RunAsync(["worker", "list", "--json", database]);
        Assert.Equal(0, json.Code);
        using var document = JsonDocument.Parse(json.Output);
        Assert.Empty(document.RootElement.GetProperty("workers").EnumerateArray());
        Assert.False(document.RootElement.GetProperty("hasMore").GetBoolean());
    }

    [Theory]
    [InlineData("--limit", "0")]
    [InlineData("--limit", "101")]
    [InlineData("--offset", "-1")]
    [InlineData("--offset", "10001")]
    [InlineData("--limit", "private-secret")]
    public async Task InvalidWorkerListOptionsFailBeforeOpeningState(string option, string value)
    {
        var result = await RunAsync(["worker", "list", option, value]);
        Assert.Equal(2, result.Code);
        Assert.Contains("worker list --help", result.Error);
        Assert.DoesNotContain("private-secret", result.Error);
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(string[] arguments)
    {
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var code = await Program.Main(arguments);
            return (code, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"server-help-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Path, recursive: true);
        }
    }
}
