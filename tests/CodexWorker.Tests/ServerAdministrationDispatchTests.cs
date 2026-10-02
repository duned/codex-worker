namespace CodexWorker.Tests;

using CodexServer;

public sealed class ServerAdministrationDispatchTests
{
    [Fact]
    public async Task ProgramDispatchesAdministrationRootsToTheirHandlers()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = Path.Combine(temporary.Path, "codex-server.db");
        await new SqliteRegistryStore(databasePath).InitializeAsync();
        var dataDirectory = $"--Server:DataDirectory={temporary.Path}";
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);

        try
        {
            await AssertCommandAsync(["status", "--json", dataDirectory], ServerAdministrationExitCodes.Success);
            await AssertCommandAsync(["diagnostics", "--json", dataDirectory], ServerAdministrationExitCodes.Success);
            await AssertCommandAsync(["config", "show", "--json", dataDirectory], ServerAdministrationExitCodes.Success);
            await AssertCommandAsync(["config", "validate", "--json", dataDirectory], ServerAdministrationExitCodes.Success);
            await AssertCommandAsync(["projects", "list", "--json", dataDirectory], ServerAdministrationExitCodes.Success);
            await AssertCommandAsync(["executions", "list", "--json", dataDirectory], ServerAdministrationExitCodes.Success);

            // Invalid operations stop at each command family's own argument validation, without
            // reaching GitHub or credential providers.
            await AssertCommandAsync(["github", "unsupported", "owner/repository"],
                ServerAdministrationExitCodes.InvalidArguments);
            await AssertCommandAsync(["credential", "unsupported"], ServerAdministrationExitCodes.InvalidArguments);
            await AssertCommandAsync(["provision", "list", "--json", dataDirectory], ServerAdministrationExitCodes.Success);
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }

        async Task AssertCommandAsync(string[] arguments, int expectedExitCode)
        {
            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            Assert.Equal(expectedExitCode, await Program.Main(arguments));
            Assert.DoesNotContain("Missing Server administration command", error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Invalid Server administration command", error.ToString(), StringComparison.Ordinal);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"server-admin-dispatch-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
