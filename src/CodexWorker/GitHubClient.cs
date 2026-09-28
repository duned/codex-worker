using System.Text.Json;

namespace CodexWorker;

public sealed record GitHubIssue(int Number, string Title, string Body, DateTimeOffset CreatedAt);
public sealed record RequiredGitHubLabel(string Name, string Color, string Description);

public sealed class GitHubClient : IGitHubClient, IGitHubLabelClient
{
    private readonly string repository;
    private readonly Func<IEnumerable<string>, CancellationToken, Task<ProcessResult>> runCommand;

    public GitHubClient(ProcessRunner runner, string repository, int timeoutSeconds)
        : this(repository, (arguments, ct) => runner.RunAsync("gh", arguments,
            Environment.CurrentDirectory, TimeSpan.FromSeconds(timeoutSeconds), ct)) { }

    internal GitHubClient(string repository,
        Func<IEnumerable<string>, CancellationToken, Task<ProcessResult>> runCommand)
    {
        this.repository = repository;
        this.runCommand = runCommand;
    }

    public async Task<GitHubIssue?> FindOldestReadyAsync(string label, CancellationToken cancellationToken)
    {
        var result = await RunGhAsync(["issue", "list", "--repo", repository, "--state", "open", "--label", label,
            "--search", "sort:created-asc", "--limit", "1000", "--json", "number,title,body,createdAt,blockedBy"], cancellationToken,
            allowGracefulCancellation: true);
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var candidates = document.RootElement.EnumerateArray()
                .Select(e => (Issue: new GitHubIssue(e.GetProperty("number").GetInt32(), e.GetProperty("title").GetString() ?? "",
                    e.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                    e.GetProperty("createdAt").GetDateTimeOffset()),
                    Dependencies: e.GetProperty("blockedBy")))
                .OrderBy(candidate => candidate.Issue.CreatedAt);

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DependenciesAreClosed(candidate.Dependencies)) return candidate.Issue;
            }
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new WorkerInfrastructureException($"Could not reliably read ready Issue dependencies for repository '{repository}': {ex.Message}", ex);
        }
    }

    private static bool DependenciesAreClosed(JsonElement blockedBy)
    {
        if (blockedBy.ValueKind != JsonValueKind.Object ||
            !blockedBy.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array ||
            !blockedBy.TryGetProperty("totalCount", out var totalCount) || !totalCount.TryGetInt32(out var count))
            throw new JsonException("GitHub 'blockedBy' data did not include dependency nodes and their total count.");
        if (count != nodes.GetArrayLength())
            throw new JsonException($"GitHub returned {nodes.GetArrayLength()} of {count} blocking dependencies; dependency state is incomplete.");

        foreach (var dependency in nodes.EnumerateArray())
        {
            if (dependency.TryGetProperty("closed", out var closed) &&
                (closed.ValueKind == JsonValueKind.True || closed.ValueKind == JsonValueKind.False))
            {
                if (!closed.GetBoolean()) return false;
                continue;
            }
            if (dependency.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String)
            {
                var value = state.GetString();
                if (string.Equals(value, "CLOSED", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(value, "OPEN", StringComparison.OrdinalIgnoreCase)) return false;
            }
            throw new JsonException("GitHub 'blockedBy' entry did not include a usable dependency state.");
        }
        return true;
    }

    public Task ReplaceLabelAsync(int issueNumber, string remove, string add, CancellationToken ct) =>
        RunGhAsync(["issue", "edit", issueNumber.ToString(), "--repo", repository, "--remove-label", remove, "--add-label", add], ct);

    public Task CommentAsync(int issueNumber, string comment, CancellationToken ct) =>
        RunGhAsync(["issue", "comment", issueNumber.ToString(), "--repo", repository, "--body", comment], ct);

    public Task CloseAsync(int issueNumber, CancellationToken ct) =>
        RunGhAsync(["issue", "close", issueNumber.ToString(), "--repo", repository], ct);

    public async Task<IReadOnlyList<RequiredGitHubLabel>> FindMissingLabelsAsync(IReadOnlyList<RequiredGitHubLabel> required, CancellationToken ct)
    {
        var result = await RunLabelGhAsync(["label", "list", "--repo", repository, "--limit", "1000", "--json", "name"],
            "query", null, ct, readOnly: true);
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var existing = document.RootElement.EnumerateArray()
                .Select(e => e.GetProperty("name").GetString() ?? "")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return required.Where(label => !existing.Contains(label.Name)).ToArray();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new WorkerInfrastructureException($"Could not parse GitHub labels for repository '{repository}': {ex.Message}", ex);
        }
    }

    public async Task CreateLabelAsync(RequiredGitHubLabel label, CancellationToken ct)
    {
        await RunLabelGhAsync(["label", "create", label.Name, "--repo", repository, "--color", label.Color, "--description", label.Description],
            "create", label.Name, ct, readOnly: false);
    }

    private async Task<ProcessResult> RunGhAsync(IEnumerable<string> args, CancellationToken ct, bool allowGracefulCancellation = false)
    {
        try
        {
            var result = await runCommand(args, ct);
            if (result.ExitCode != 0)
                throw new WorkerInfrastructureException($"GitHub CLI command failed (exit {result.ExitCode}); Issue state may require manual reconciliation. {Tail(result.StandardError)}");
            return result;
        }
        catch (OperationCanceledException) when (allowGracefulCancellation && ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex) { throw new WorkerInfrastructureException("GitHub operation was cancelled; remote Issue state may be uncertain.", ex); }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) { throw new WorkerInfrastructureException($"GitHub CLI operation failed or timed out; GitHub state may require manual reconciliation: {ex.Message}", ex); }
    }

    private async Task<ProcessResult> RunLabelGhAsync(IEnumerable<string> args, string action, string? label, CancellationToken ct, bool readOnly)
    {
        try
        {
            var result = await runCommand(args, ct);
            if (result.ExitCode != 0)
            {
                var target = label is null ? "configured GitHub labels" : $"GitHub label '{label}'";
                var uncertainty = action == "create" ? " Remote label state may be uncertain." : "";
                throw new WorkerInfrastructureException($"Could not {action} {target} for repository '{repository}' (exit {result.ExitCode}).{uncertainty} {Tail(result.StandardError)}");
            }
            return result;
        }
        catch (OperationCanceledException) when (readOnly && ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex)
        {
            var target = label is null ? "configured GitHub labels" : $"GitHub label '{label}'";
            throw new WorkerInfrastructureException($"Cancellation interrupted {action} of {target} for repository '{repository}'; remote state may be uncertain.", ex);
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex)
        {
            var target = label is null ? "configured GitHub labels" : $"GitHub label '{label}'";
            throw new WorkerInfrastructureException($"Could not {action} {target} for repository '{repository}': {ex.Message}", ex);
        }
    }

    private static string Tail(string value) => value.Length <= 1000 ? value : value[^1000..];
}
