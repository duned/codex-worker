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
            "--search", "sort:created-asc", "--limit", "1000", "--json", "number,title,body,createdAt"], cancellationToken,
            allowGracefulCancellation: true);
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var candidates = document.RootElement.EnumerateArray()
                .Select(e => new GitHubIssue(e.GetProperty("number").GetInt32(), e.GetProperty("title").GetString() ?? "",
                    e.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                    e.GetProperty("createdAt").GetDateTimeOffset()))
                .OrderBy(candidate => candidate.CreatedAt);

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var dependencies = await GetBlockingDependenciesAsync(candidate.Number, cancellationToken);
                if (DependenciesAreClosed(dependencies)) return candidate;
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

    private async Task<JsonDocument> GetBlockingDependenciesAsync(int issueNumber, CancellationToken ct)
    {
        ProcessResult result;
        try
        {
            result = await RunGhAsync(["api", "--paginate", "--slurp", $"repos/{repository}/issues/{issueNumber}/dependencies/blocked_by"], ct,
                allowGracefulCancellation: true);
        }
        catch (WorkerInfrastructureException ex)
        {
            throw new WorkerInfrastructureException($"GitHub Issue Dependencies API unavailable for '{repository}' Issue #{issueNumber}: {ex.Message}", ex);
        }
        try { return JsonDocument.Parse(result.StandardOutput); }
        catch (JsonException ex) { throw new WorkerInfrastructureException($"Could not parse GitHub Issue dependencies for #{issueNumber} in '{repository}': {ex.Message}", ex); }
    }

    private static bool DependenciesAreClosed(JsonDocument pages)
    {
        var root = pages.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            throw new JsonException("GitHub dependency API did not return paginated JSON arrays.");

        // gh api --paginate --slurp wraps each page in an array. Inspect every page
        // so a later unresolved dependency can never be hidden by the first page.
        foreach (var page in root.EnumerateArray())
        {
            if (page.ValueKind != JsonValueKind.Array)
                throw new JsonException("GitHub dependency API returned a page that was not an array.");
            foreach (var dependency in page.EnumerateArray())
            {
                if (!dependency.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String)
                    throw new JsonException("GitHub dependency entry did not include a usable state.");
                var value = state.GetString();
                if (string.Equals(value, "open", StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.Equals(value, "closed", StringComparison.OrdinalIgnoreCase))
                    throw new JsonException("GitHub dependency entry returned an unknown state.");
            }
        }
        return true;
    }

    /// <summary>Validates the read-only GitHub capabilities needed before queue polling.</summary>
    public async Task ValidateCapabilitiesAsync(CancellationToken ct)
    {
        await RunCapabilityAsync(["--version"], "GitHub CLI", ct);
        await RunCapabilityAsync(["auth", "status"], "GitHub authentication", ct);
        await RunCapabilityAsync(["api", $"repos/{repository}"], "GitHub repository access", ct);
        var issues = await RunCapabilityAsync(["issue", "list", "--repo", repository, "--state", "all", "--limit", "1", "--json", "number"], "GitHub Issue access", ct);
        try
        {
            using var document = JsonDocument.Parse(issues.StandardOutput);
            var existingIssue = document.RootElement.EnumerateArray().FirstOrDefault();
            if (existingIssue.ValueKind == JsonValueKind.Object)
            {
                var number = existingIssue.GetProperty("number").GetInt32();
                using var dependencies = await GetBlockingDependenciesAsync(number, ct);
                _ = DependenciesAreClosed(dependencies);
            }
            // A repository without Issues has no valid Issue number against which the
            // dependency endpoint can be called. Repository/API and Issue-list access
            // above are still checked without creating test data.
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new WorkerInfrastructureException($"GitHub Issue dependency capability validation failed for '{repository}': {ex.Message}", ex);
        }
    }

    private async Task<ProcessResult> RunCapabilityAsync(IEnumerable<string> args, string capability, CancellationToken ct)
    {
        try
        {
            var result = await runCommand(args, ct);
            if (result.ExitCode != 0)
                throw new WorkerInfrastructureException($"{capability} unavailable for repository '{repository}' (exit {result.ExitCode}). {Tail(result.StandardError)}");
            return result;
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) { throw new WorkerInfrastructureException($"{capability} unavailable for repository '{repository}': {ex.Message}", ex); }
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
