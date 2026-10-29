using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WorkExecutionToolbox.Cli;

/// <summary>Local stdio host: credentials and execution remain in the invoking user's context.</summary>
public static class ToolboxMcp
{
    public static async Task<int> RunAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders(); // stdout is exclusively MCP; never log provider payloads.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var provider = new GitHubIssueProvider(http, GhAuthentication.ReadTokenAsync,
            new GitHubIssueCacheOptions { DirectoryPath = GitHubIssueCacheOptions.DefaultDirectory });
        builder.Services.AddMcpServer().WithStdioServerTransport().WithTools(new ToolboxTools(provider, provider));
        using var host = builder.Build();
        await host.RunAsync();
        return 0;
    }
}

public sealed record ToolboxMcpResult(string OperationId, int ExitCode, string Output, string Error, bool Truncated);

[McpServerToolType]
public sealed class ToolboxTools(IIssueRelationshipProvider relationships, IIssueGraphProvider graphs, TimeProvider? clock = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, ToolboxMcpResult> results = new(StringComparer.Ordinal);

    [McpServerTool(Name = "wet_inspect", ReadOnly = true, Destructive = false)]
    [Description("Inspect repository roots, children, relationships or graph using WET. Explicit GitHub.com owner/name required. No local paths or ambient repository discovery.")]
    public Task<ToolboxMcpResult> Inspect(string repository, ToolboxInspection operation, int? issue = null,
        bool refresh = false, CancellationToken cancellationToken = default)
    {
        var command = operation switch
        {
            ToolboxInspection.Roots => "roots", ToolboxInspection.Children => "children",
            ToolboxInspection.Relationships => "relationships", ToolboxInspection.Graph => "graph",
            _ => throw new McpException("Unsupported inspection.")
        };
        if (operation == ToolboxInspection.Roots ? issue is not null : issue is null or <= 0)
            throw new McpException("Roots takes no Issue; other inspections require a positive Issue number.");
        return ExecuteAsync(repository, [command, .. (issue is { } n ? new[] { Number(n) } : Array.Empty<string>()),
            .. (refresh ? new[] { "--refresh" } : Array.Empty<string>())], cancellationToken);
    }

    [McpServerTool(Name = "wet_set_parent", ReadOnly = false, Destructive = true)]
    [Description("Assign native GitHub parent to 1–50 distinct children through WET. Existing different parents conflict; clear explicitly first. Refresh after any failed or cancelled write.")]
    public Task<ToolboxMcpResult> SetParent(string repository, int parent, int[] children, CancellationToken cancellationToken = default)
    {
        Validate(() => _ = new SetParentsRequest(new(GitHubRepositoryContext.Create(repository), parent), children));
        return ExecuteAsync(repository, ["parent", "set", .. children.Select(Number), Number(parent)], cancellationToken);
    }

    [McpServerTool(Name = "wet_clear_parent", ReadOnly = false, Destructive = true)]
    [Description("Clear a native GitHub Issue parent through WET. Refresh after any failed or cancelled write.")]
    public Task<ToolboxMcpResult> ClearParent(string repository, int child, CancellationToken cancellationToken = default)
    {
        Validate(() => _ = new IssueReference(GitHubRepositoryContext.Create(repository), child));
        return ExecuteAsync(repository, ["parent", "clear", Number(child)], cancellationToken);
    }

    [McpServerTool(Name = "wet_dependencies", ReadOnly = false, Destructive = true)]
    [Description("Add or remove native blocked-by prerequisites through WET. Applied=true adds, false removes. Refresh after any failed or cancelled write; do not blindly retry.")]
    public Task<ToolboxMcpResult> Dependencies(string repository, int issue, int[] blockers, bool applied,
        CancellationToken cancellationToken = default)
    {
        Validate(() => _ = new SetDependenciesRequest(new(GitHubRepositoryContext.Create(repository), issue), blockers, applied));
        return ExecuteAsync(repository, ["dependency", applied ? "add" : "remove", Number(issue), .. blockers.Select(Number)], cancellationToken);
    }

    [McpServerTool(Name = "wet_operation", ReadOnly = true, Destructive = false)]
    [Description("Inspect a completed WET operation by identifier. Last 32 results retained only for this local MCP process. Calls have a 30 second deadline; no detached work continues after return.")]
    public ToolboxMcpResult GetOperation(string operationId)
    {
        lock (results)
            return results.TryGetValue(operationId, out var result) ? result :
                throw new McpException("Unknown or expired operation identifier.");
    }

    private async Task<ToolboxMcpResult> ExecuteAsync(string repository, string[] command, CancellationToken token)
    {
        Validate(() => _ = GitHubRepositoryContext.Create(repository));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock ?? TimeProvider.System);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        using var output = new LimitedWriter();
        using var error = new LimitedWriter();
        var entered = false;
        int exit;
        try
        {
            await gate.WaitAsync(deadline.Token);
            entered = true;
            exit = await ToolboxCommand.RunAsync([.. command, "--repo", repository, "--json"], relationships, graphs,
                output, error, deadline.Token);
        }
        catch (OperationCanceledException) { exit = 130; }
        finally { if (entered) gate.Release(); }
        if (deadline.IsCancellationRequested)
        {
            exit = token.IsCancellationRequested ? 130 : 1;
            error.Reset(token.IsCancellationRequested ? "Cancelled. Refresh relationships before retrying a write."
                : "30 second deadline exceeded. Refresh relationships before retrying a write.");
        }
        var result = new ToolboxMcpResult(Guid.NewGuid().ToString("N"), exit, output.ToString(), error.ToString(), output.Truncated || error.Truncated);
        lock (results)
        {
            if (results.Count == 32) results.Remove(results.Keys.First());
            results.Add(result.OperationId, result);
        }
        return result;
    }

    private static void Validate(Action validate)
    {
        try { validate(); }
        catch (ArgumentException)
        {
            throw new McpException("Use an explicit GitHub.com owner/name and positive Issue numbers. Batches require 1–50 distinct numbers and cannot contain self-links.");
        }
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed class LimitedWriter : TextWriter
    {
        private readonly StringBuilder content = new();
        public override Encoding Encoding => Encoding.UTF8;
        public bool Truncated { get; private set; }
        public override void Write(char value)
        {
            if (content.Length < 16_384) content.Append(value);
            else Truncated = true;
        }
        public override Task WriteLineAsync(string? value)
        {
            foreach (var c in value ?? "") Write(c);
            Write('\n');
            return Task.CompletedTask;
        }
        public void Reset(string value)
        {
            content.Clear();
            foreach (var c in value) Write(c);
        }
        public override string ToString() => Regex.Replace(content.ToString(),
            @"(?:github_pat_|gh[pousr]_)[A-Za-z0-9_]+|Bearer\s+[^\s""\\]+",
            "[REDACTED]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }
}

public enum ToolboxInspection { Roots, Children, Relationships, Graph }
