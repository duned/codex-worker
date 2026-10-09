namespace CodexWorker.Tests;

using CodexProvisioning;

[Collection("ServerTokenEnvironment")]
public sealed class ExecutionMaintenanceCliTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HelpDoesNotRequireService(bool managed)
    {
        using var output = new StringWriter();
        Assert.Equal(0, await ExecutionMaintenanceCli.RunAsync(managed, ["--help"], writer: output));
        Assert.Contains("--apply --confirm", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cleanup", "abc", "--apply")]
    [InlineData("cleanup", "abc", "--confirm")]
    [InlineData("inspect", "abc", "--apply", "--confirm")]
    [InlineData("inventory", "project", "--limit", "101")]
    [InlineData("cleanup", "123", "--json")]
    public async Task RejectsUnsafeOrAmbiguousArguments(params string[] args)
    {
        using var output = new StringWriter();
        Assert.Equal(2, await ExecutionMaintenanceCli.RunAsync(false, args, writer: output));
    }
    [Theory]
    [InlineData(200, "{\"outcome\":\"succeeded\"}", 0)]
    [InlineData(200, "[{\"outcome\":\"failed\"}]", 5)]
    [InlineData(401, "{}", 3)]
    [InlineData(409, "{\"reason\":\"worker-offline\"}", 3)]
    public async Task PreservesApiResultsAndDefaultsToPreview(int status, string body, int expected)
    {
        var previous = Environment.GetEnvironmentVariable("CODEX_ADMIN_URL");
        Environment.SetEnvironmentVariable("CODEX_ADMIN_URL", "http://localhost:1234");
        try
        {
            using var handler = new ResponseHandler(status, body);
            using var client = new HttpClient(handler);
            using var output = new StringWriter();
            Assert.Equal(expected, await ExecutionMaintenanceCli.RunAsync(false,
                ["cleanup", Guid.NewGuid().ToString()], client: client, writer: output));
            Assert.Contains("\"apply\":false", handler.Payload, StringComparison.Ordinal);
            Assert.Contains("status", output.ToString(), StringComparison.Ordinal);
        }
        finally { Environment.SetEnvironmentVariable("CODEX_ADMIN_URL", previous); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedDispatchUsesExplicitOwnershipAndRepeatOperation(bool apply)
    {
        var previous = Environment.GetEnvironmentVariable("CODEX_ADMIN_URL");
        var file = Path.GetTempFileName();
        Environment.SetEnvironmentVariable("CODEX_ADMIN_URL", "http://localhost:1234");
        try
        {
            var request = new ExecutionMaintenanceRequest(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"), Guid.NewGuid(), Guid.NewGuid().ToString("N"), 1, "cleanup", true);
            await File.WriteAllTextAsync(file, System.Text.Json.JsonSerializer.Serialize(request));
            using var handler = new ResponseHandler(201, "{\"status\":\"queued\"}");
            using var client = new HttpClient(handler);
            using var output = new StringWriter();
            string[] args = apply ? ["cleanup", file, "--apply", "--confirm"] : ["cleanup", file];
            Assert.Equal(0, await ExecutionMaintenanceCli.RunAsync(true, args, client: client, writer: output));
            var first = handler.Payload;
            Assert.Equal(0, await ExecutionMaintenanceCli.RunAsync(true, args, client: client, writer: output));
            Assert.Equal(first, handler.Payload);
            var sent = System.Text.Json.JsonSerializer.Deserialize<ExecutionMaintenanceRequest>(handler.Payload,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            Assert.NotNull(sent);
            Assert.Equal(apply, sent.Apply);
            Assert.Equal(request.OperationId, sent.OperationId);
            Assert.Equal(request.WorkerId, sent.WorkerId);
        }
        finally
        {
            File.Delete(file);
            Environment.SetEnvironmentVariable("CODEX_ADMIN_URL", previous);
        }
    }

    private sealed class ResponseHandler(int status, string body) : HttpMessageHandler
    {
        public string Payload { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is { } content) Payload = await content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent(body) };
        }
    }

}
