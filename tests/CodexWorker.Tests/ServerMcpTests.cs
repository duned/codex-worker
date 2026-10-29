namespace CodexWorker.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodexServer;
using CodexProvisioning;
using Microsoft.Extensions.DependencyInjection;

[Collection("ServerTokenEnvironment")]
public sealed class ServerMcpTests
{
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task RemoteDiscoveryAuthorizationBoundsAndReadOnlyResults()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"server-mcp-{Guid.NewGuid():N}");
        var previousToken = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "mcp-test-management");
        try
        {
            await using var app = await ServerApplication.BuildAsync(
                ["--Server:ListenUrl=http://127.0.0.1:5090", $"--Server:DatabasePath={Path.Combine(directory, "mcp.db")}",
                 "--Logging:LogLevel:Default=Warning"], githubReadService: new AlwaysEligibleServerGitHubReadService());
            // Let Kestrel choose the test port without a reservation/rebind race.
            app.Urls.Clear();
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)), Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Delete })
            {
                using var unauthorized = await client.SendAsync(new HttpRequestMessage(method, "/mcp"));
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
                Assert.Equal("no-store", unauthorized.Headers.CacheControl?.ToString());
                Assert.Equal("Bearer", Assert.Single(unauthorized.Headers.WwwAuthenticate).Scheme);
            }
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
            using (var denied = await client.GetAsync("/mcp")) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "mcp-test-management");
            client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
            using (var denied = await client.GetAsync("/mcp")) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            client.DefaultRequestHeaders.Remove("Origin");
            using (var oversized = await client.PostAsync("/mcp", new StringContent(new string(' ', 17 * 1024))))
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);

            var initialize = await Rpc(client, "initialize", new
            {
                protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "test-client", version = "1" }
            });
            Assert.Equal(ServerApplication.DisplayVersion, initialize.GetProperty("result").GetProperty("serverInfo").GetProperty("version").GetString());
            client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-11-25");
            var discovery = await Rpc(client, "tools/list", new { });
            var tools = discovery.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
            Assert.Equal(new[] { "cancel_execution", "dispatch_execution", "get_execution", "get_execution_operation", "list_executions", "list_projects", "list_workers", "retry_execution_report", "server_health" },
                tools.Select(tool => tool.GetProperty("name").GetString()).Order(StringComparer.Ordinal));
            Assert.All(tools, tool =>
            {
                var name = tool.GetProperty("name").GetString();
                Assert.Equal(name is not ("dispatch_execution" or "cancel_execution" or "retry_execution_report"),
                    tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
                Assert.Equal(name is "cancel_execution" or "retry_execution_report",
                    tool.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
                Assert.DoesNotContain("cancellationToken", tool.GetProperty("inputSchema").GetRawText(), StringComparison.OrdinalIgnoreCase);
            });

            foreach (var name in new[] { "list_workers", "list_projects", "list_executions" })
            {
                foreach (var arguments in new object[] { new { limit = 0 }, new { limit = 101 }, new { offset = -1 }, new { offset = 10001 } })
                    AssertToolError(await Rpc(client, "tools/call", new { name, arguments }));
            }
            AssertToolError(await Rpc(client, "tools/call", new { name = "list_executions", arguments = new { state = "unsupported" } }));
            AssertToolError(await Rpc(client, "tools/call", new { name = "list_executions", arguments = new { projectId = new string('p', 81) } }));
            AssertToolError(await Rpc(client, "tools/call", new { name = "list_executions", arguments = new { workId = "-1" } }));
            AssertToolError(await Rpc(client, "tools/call", new { name = "get_execution", arguments = new { executionRequestId = "../private" } }));

            var store = app.Services.GetRequiredService<IRegistryStore>();
            var workerId = Guid.NewGuid().ToString("N");
            WorkerCapability[] capabilities =
            [
                new("tool", "git", "2.0", "/private/node-scope"),
                new("authentication", "github-api", Scope: "example/repo"),
                new("authentication", "git-repository", Scope: "example/repo"),
                new("agent-provider", "codex")
            ];
            const string workerToken = "mcp-test-worker-with-sufficient-entropy";
            var bootstrap = await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(1));
            Assert.True(await store.BootstrapWorkerAsync(bootstrap,
                new(2, workerId, "worker token=private-value", "1.2.3", "/private/platform", 1, capabilities), workerToken));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", workerToken);
            using (var denied = await client.GetAsync("/mcp")) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "mcp-test-management");
            var project = await store.CreateProjectAsync(new("project", "example/repo", "main", "secret=private-description",
                ValidationCommands: ["echo private-command"]));
            await store.HeartbeatWorkerAsync(new(2, workerId, "1.2.3", "running", 0, 1, capabilities, [project.Id]));
            var dispatched = await Call(client, "dispatch_execution", new { projectId = project.Id, issueNumber = 42 });
            Assert.Equal("accepted", dispatched.GetProperty("outcome").GetString());
            var queued = Assert.IsType<ExecutionRequest>(await store.GetExecutionAsync(dispatched.GetProperty("executionRequestId").GetString() ?? ""));
            Assert.Equal("rejected", (await Call(client, "dispatch_execution", new { projectId = project.Id, issueNumber = 42 })).GetProperty("outcome").GetString());
            AssertToolError(await Rpc(client, "tools/call", new { name = "dispatch_execution", arguments = new { projectId = project.Id, issueNumber = -1 } }));
            Assert.Equal("project-not-found", (await Call(client, "dispatch_execution", new { projectId = "missing", issueNumber = 42 })).GetProperty("reason").GetString());
            var cancellable = await Call(client, "dispatch_execution", new { projectId = project.Id, issueNumber = 43 });
            var cancelArguments = new { executionRequestId = cancellable.GetProperty("executionRequestId").GetString() };
            Assert.Equal("Cancelled", (await Call(client, "cancel_execution", cancelArguments)).GetProperty("state").GetString());
            Assert.Equal("rejected", (await Call(client, "cancel_execution", cancelArguments)).GetProperty("outcome").GetString());
            Assert.Equal("worker-execution-proof-missing", (await Call(client, "retry_execution_report", new { executionRequestId = queued.Id, operationId = Guid.NewGuid().ToString("N") })).GetProperty("reason").GetString());
            await store.UpdateProjectLifecycleAsync(project.Id, false, project.Revision);
            Assert.Equal("project-disabled", (await Call(client, "dispatch_execution", new { projectId = project.Id, issueNumber = 44 })).GetProperty("reason").GetString());
            var disabledProject = Assert.IsType<CentralProject>(await store.GetProjectAsync(project.Id));
            await store.UpdateProjectLifecycleAsync(project.Id, true, disabledProject.Revision);
            var assignmentResponse = await store.RequestAssignmentAsync(new(workerId, true, 1,
                new Dictionary<string, int> { [project.Id] = 1 }));
            Assert.True(assignmentResponse.HasWork, assignmentResponse.Reason);
            var assignment = Assert.IsType<WorkAssignment>(assignmentResponse.Assignment);
            var lease = Assert.IsType<ExecutionLease>(assignment.Lease);
            await store.ReportExecutionAsync(queued.Id, new(workerId, assignment.AssignmentId, Guid.NewGuid().ToString(), "Running", "Validation",
                ValidationResult: "passed", IntegrationResult: "secret=private-result /private/node Bearer private-authorization " + new string('a', 400),
                Summary: "private raw output /private/path",
                Generation: lease.Generation));

            Assert.Equal("execution-not-queued", (await Call(client, "cancel_execution", new { executionRequestId = queued.Id })).GetProperty("reason").GetString());
            Assert.Equal("worker-maintenance-protocol-unavailable", (await Call(client, "retry_execution_report", new { executionRequestId = queued.Id, operationId = Guid.NewGuid().ToString("N") })).GetProperty("reason").GetString());
            capabilities = [.. capabilities, new("protocol", ExecutionMaintenanceProtocol.Capability)];
            await store.HeartbeatWorkerAsync(new(2, workerId, "1.2.3", "running", 1, 1, capabilities, [project.Id]));
            await store.SetWorkerSchedulingPolicyAsync(workerId, WorkerSchedulingPolicy.Draining);
            var retryArguments = new { executionRequestId = queued.Id, operationId = Guid.NewGuid().ToString("N") };
            var retry = await Call(client, "retry_execution_report", retryArguments);
            Assert.Equal("pending", retry.GetProperty("status").GetString());
            Assert.Equal(retry.GetRawText(), (await Call(client, "retry_execution_report", retryArguments)).GetRawText());
            Assert.Equal("pending", (await Call(client, "get_execution_operation", new { retryArguments.operationId })).GetProperty("status").GetString());
            Assert.Equal("operation-scope-conflict", (await Call(client, "retry_execution_report", new { executionRequestId = Guid.NewGuid().ToString("N"), retryArguments.operationId })).GetProperty("reason").GetString());
            var health = await Call(client, "server_health", new { });
            Assert.True(health.GetProperty("persistenceAvailable").GetBoolean());
            var workers = await Call(client, "list_workers", new { limit = 1 });
            var worker = Assert.Single(workers.GetProperty("items").EnumerateArray());
            Assert.Equal(workerId, worker.GetProperty("workerId").GetString());
            Assert.Equal("1.2.3", worker.GetProperty("version").GetString());
            Assert.Equal("git", worker.GetProperty("capabilities")[0].GetProperty("name").GetString());
            Assert.Equal(queued.Id, worker.GetProperty("currentExecutions")[0].GetProperty("id").GetString());
            var projects = await Call(client, "list_projects", new { });
            Assert.Equal(project.Id, projects.GetProperty("items")[0].GetProperty("id").GetString());
            Assert.Empty((await Call(client, "list_projects", new { offset = 1 })).GetProperty("items").EnumerateArray());
            var executions = await Call(client, "list_executions", new { projectId = project.Id, state = "Running", workId = "42", limit = 1 });
            Assert.Equal(queued.Id, executions.GetProperty("items")[0].GetProperty("id").GetString());
            Assert.Empty((await Call(client, "list_executions", new { state = "Completed" })).GetProperty("items").EnumerateArray());
            var detail = await Call(client, "get_execution", new { executionRequestId = queued.Id });
            Assert.Equal("Validation", detail.GetProperty("stage").GetString());
            Assert.Equal("passed", detail.GetProperty("validationResult").GetString());
            Assert.True(detail.GetProperty("integrationResult").GetString()?.Length <= 200);
            Assert.NotEmpty(detail.GetProperty("stageHistory").EnumerateArray());
            Assert.False((await Call(client, "get_execution", new { executionRequestId = Guid.NewGuid().ToString("N") })).GetProperty("found").GetBoolean());
            foreach (var result in new[] { workers, projects, executions, detail })
            {
                var json = result.GetRawText();
                Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("environment", json, StringComparison.OrdinalIgnoreCase);
            }
            var unknown = await Rpc(client, "tools/call", new { name = "dispatch", arguments = new { } });
            Assert.True(unknown.TryGetProperty("error", out _) || unknown.GetProperty("result").GetProperty("isError").GetBoolean());
            Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "rotated-management-token");
            using (var rotated = await client.GetAsync("/mcp")) Assert.Equal(HttpStatusCode.Unauthorized, rotated.StatusCode);
            await app.StopAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", previousToken);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertToolError(JsonElement response) => Assert.True(response.GetProperty("result").GetProperty("isError").GetBoolean());

    private static async Task<JsonElement> Call(HttpClient client, string name, object arguments)
    {
        var response = await Rpc(client, "tools/call", new { name, arguments });
        var result = response.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean(), result.GetRawText());
        using var document = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString() ?? "null");
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> Rpc(HttpClient client, string method, object parameters)
    {
        using var response = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method, @params = parameters });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
        var json = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? body.Split('\n').Last(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..] : body;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
