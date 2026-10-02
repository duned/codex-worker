namespace CodexServer;

using System.Diagnostics;
using System.Text;
using System.Text.Json;

public sealed record GitHubIssueQuery(string State = "open", int Limit = 50, string? Label = null);
public sealed record GitHubBlockingIssue(int Number, string Title, string State, string Url);
public sealed record ManagedGitHubIssue(int Number, string Title, string Body, string State,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, string Url, IReadOnlyList<string> Labels,
    IReadOnlyList<GitHubBlockingIssue> BlockedBy, bool IsEligible, IReadOnlyList<string> EligibilityReasons);
public sealed record GitHubRepositoryAccess(string Repository, bool CliAuthenticated, bool RepositoryReadable,
    string? Diagnostic, DateTimeOffset CheckedAtUtc);
public sealed record GitHubReadCommandResult(int ExitCode, string StandardOutput, string StandardError);

public static class GitHubIssueQueryValidation
{
    public static string? Error(GitHubIssueQuery? query) => query is null || query.State is not ("open" or "closed" or "all") ||
        query.Limit is < 1 or > 100 || query.Label is { } label &&
        (string.IsNullOrWhiteSpace(label) || label.Length > 100 || label.Any(char.IsControl))
        ? "GitHub Issue query must use state open, closed, or all; limit 1 to 100; and an optional printable label up to 100 characters."
        : null;
}

public sealed class GitHubReadUnavailableException(string repository, string message, string code, Exception? inner = null)
    : IOException(message, inner)
{
    public string Repository { get; } = repository;
    public string Code { get; } = code;
}

public sealed class GitHubIssueNotFoundException(string repository, int issueNumber)
    : KeyNotFoundException($"GitHub Issue #{issueNumber} was not found or is not visible in '{repository}'.");

public sealed class ManagedIssueIneligibleException(ManagedGitHubIssue issue)
    : InvalidOperationException($"GitHub Issue #{issue.Number} is not eligible for managed work.")
{
    public ManagedGitHubIssue Issue { get; } = issue;
}

public interface IServerGitHubReadService
{
    Task<GitHubRepositoryAccess> CheckAccessAsync(CentralProject project, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ManagedGitHubIssue>> ListIssuesAsync(CentralProject project, GitHubIssueQuery query,
        CancellationToken cancellationToken = default);
    Task<ManagedGitHubIssue?> GetIssueAsync(CentralProject project, int issueNumber,
        CancellationToken cancellationToken = default);
}

public interface IServerGitHubAdministrationService
{
    Task<GitHubRepositoryAccess?> CheckAccessAsync(string projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ManagedGitHubIssue>?> ListIssuesAsync(string projectId, GitHubIssueQuery query,
        CancellationToken cancellationToken = default);
    Task<ManagedGitHubIssue?> GetIssueAsync(string projectId, int issueNumber, CancellationToken cancellationToken = default);
    Task<GitHubIssueMutationResult> CreateIssueAsync(string projectId, GitHubIssueCreateRequest request,
        CancellationToken cancellationToken = default);
    Task<GitHubIssueMutationResult> UpdateIssueAsync(string projectId, int issueNumber, GitHubIssueUpdateRequest request,
        CancellationToken cancellationToken = default);
    Task<GitHubIssueMutationResult> SetIssueLabelAsync(string projectId, int issueNumber, GitHubIssueLabelRequest request,
        CancellationToken cancellationToken = default);
    Task<GitHubIssueMutationResult> SetIssueBlockedByAsync(string projectId, int issueNumber, GitHubIssueDependencyRequest request,
        CancellationToken cancellationToken = default);
    Task<ExecutionRequest> EnqueueIssueAsync(string projectId, WorkReference workReference,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExecutionRequest>?> RefreshQueuedEligibilityAsync(string projectId, int issueNumber,
        CancellationToken cancellationToken = default);
    Task<WorkAssignmentResponse> RequestAssignmentAsync(WorkerAssignmentRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Read-only Server GitHub integration using the Server service account's gh authentication.</summary>
public sealed class ServerGitHubReadService : IServerGitHubReadService
{
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<GitHubReadCommandResult>> _run;
    private readonly TimeProvider _timeProvider;

    public ServerGitHubReadService(
        Func<IReadOnlyList<string>, CancellationToken, Task<GitHubReadCommandResult>>? run = null,
        TimeProvider? timeProvider = null)
    {
        _run = run ?? RunGhAsync;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<GitHubRepositoryAccess> CheckAccessAsync(CentralProject project, CancellationToken cancellationToken = default)
    {
        ValidateProject(project);
        var checkedAt = _timeProvider.GetUtcNow();
        GitHubReadCommandResult authentication;
        try { authentication = await _run(["auth", "status", "--hostname", "github.com"], cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new(project.Repository, false, false,
                "GitHub CLI authentication could not be checked in the Server service-account context.", checkedAt);
        }
        if (authentication.ExitCode != 0)
            return new(project.Repository, false, false,
                "GitHub CLI authentication is unavailable in the Server service-account context.", checkedAt);

        GitHubReadCommandResult repository;
        try { repository = await _run(["api", $"repos/{project.Repository}", "--jq", ".full_name"], cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new(project.Repository, true, false,
                $"The Server GitHub login could not read repository '{project.Repository}'.", checkedAt);
        }
        if (repository.ExitCode != 0 || !string.Equals(repository.StandardOutput.Trim(), project.Repository, StringComparison.OrdinalIgnoreCase))
            return new(project.Repository, true, false,
                $"The Server GitHub login could not read repository '{project.Repository}'. Issue read access is separate from Issue write and Git push access.", checkedAt);
        return new(project.Repository, true, true,
            "Repository read access is available. This does not establish Issue write or Git push access.", checkedAt);
    }

    public async Task<IReadOnlyList<ManagedGitHubIssue>> ListIssuesAsync(CentralProject project, GitHubIssueQuery query,
        CancellationToken cancellationToken = default)
    {
        ValidateProject(project);
        if (GitHubIssueQueryValidation.Error(query) is { } queryError) throw new InvalidDataException(queryError);
        var arguments = new List<string> { "issue", "list", "--repo", project.Repository, "--state", query.State,
            "--limit", query.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json",
            "number,title,body,state,createdAt,updatedAt,url,labels" };
        if (query.Label is not null) { arguments.Add("--label"); arguments.Add(query.Label); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var result = await RunReadAsync(project.Repository, arguments, linked.Token);
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > query.Limit)
                throw new JsonException("GitHub Issue query returned an invalid result count.");
            var issues = new List<ManagedGitHubIssue>(document.RootElement.GetArrayLength());
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var issue = ReadIssue(item, project.Repository);
                var blockers = await ReadBlockingIssuesAsync(project.Repository, issue.Number, linked.Token);
                issues.Add(Evaluate(project, issue, blockers));
            }
            return issues;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new GitHubReadUnavailableException(project.Repository,
                $"GitHub Issue query exceeded its 45 second deadline for repository '{project.Repository}'.", "query-timeout");
        }
        catch (GitHubReadUnavailableException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new GitHubReadUnavailableException(project.Repository,
                $"GitHub Issue results could not be parsed for repository '{project.Repository}'.", "invalid-response", exception);
        }
    }

    public async Task<ManagedGitHubIssue?> GetIssueAsync(CentralProject project, int issueNumber,
        CancellationToken cancellationToken = default)
    {
        ValidateProject(project);
        if (issueNumber <= 0) throw new InvalidDataException("Issue number must be positive.");
        var number = issueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        GitHubReadCommandResult result;
        try
        {
            result = await _run(["issue", "view", number, "--repo", project.Repository, "--json",
                "number,title,body,state,createdAt,updatedAt,url,labels"], cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new GitHubReadUnavailableException(project.Repository,
                $"GitHub read access is unavailable for repository '{project.Repository}'. Check Server service-account authentication and repository read access.",
                "read-unavailable", exception);
        }
        if (result.ExitCode != 0)
        {
            if (IsNotFound(result.StandardError)) return null;
            throw CreateReadFailure(project.Repository, result.ExitCode, "Issue detail");
        }
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var issue = ReadIssue(document.RootElement, project.Repository);
            if (issue.Number != issueNumber)
                throw new JsonException("GitHub returned a different Issue number.");
            var blockers = await ReadBlockingIssuesAsync(project.Repository, issueNumber, cancellationToken);
            return Evaluate(project, issue, blockers);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (GitHubReadUnavailableException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new GitHubReadUnavailableException(project.Repository,
                $"GitHub Issue #{issueNumber} could not be parsed for repository '{project.Repository}'.", "invalid-response", exception);
        }
    }

    private async Task<IReadOnlyList<GitHubBlockingIssue>> ReadBlockingIssuesAsync(string repository, int issueNumber,
        CancellationToken cancellationToken)
    {
        var endpoint = $"repos/{repository}/issues/{issueNumber}/dependencies/blocked_by";
        var result = await RunReadAsync(repository, ["api", "--paginate", "-F", "per_page=100", endpoint], cancellationToken);
        try
        {
            var pages = ParsePages(result.StandardOutput);
            var blockers = new List<GitHubBlockingIssue>();
            foreach (var page in pages)
            {
                if (page.ValueKind != JsonValueKind.Array) throw new JsonException("GitHub dependency page was not an array.");
                foreach (var item in page.EnumerateArray())
                {
                    var state = RequiredString(item, "state");
                    var number = item.GetProperty("number").GetInt32();
                    var title = RequiredString(item, "title");
                    var url = RequiredString(item, "html_url");
                    if (number <= 0 || state is not ("open" or "closed") || !ValidIssueUrl(url, repository, number))
                        throw new JsonException("GitHub dependency fields were invalid.");
                    blockers.Add(new(number, title, state, url));
                    if (blockers.Count > 2500) throw new JsonException("GitHub returned too many blocking Issues.");
                }
            }
            return blockers;
        }
        catch (JsonException exception)
        {
            throw new GitHubReadUnavailableException(repository,
                $"GitHub Issue dependency results could not be parsed for repository '{repository}'.", "invalid-dependencies", exception);
        }
    }

    private async Task<GitHubReadCommandResult> RunReadAsync(string repository, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        GitHubReadCommandResult result;
        try { result = await _run(arguments, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new GitHubReadUnavailableException(repository,
                $"GitHub read access is unavailable for repository '{repository}'. Check Server service-account authentication and repository read access.",
                "read-unavailable", exception);
        }
        if (result.ExitCode != 0) throw CreateReadFailure(repository, result.ExitCode, "GitHub read operation");
        return result;
    }

    private static GitHubReadUnavailableException CreateReadFailure(string repository, int exitCode, string operation) =>
        new(repository, $"{operation} failed for repository '{repository}' (gh exit {exitCode}). Check Server service-account authentication and repository read access.",
            "read-failed");

    private static ManagedGitHubIssue Evaluate(CentralProject project, IssueFields issue,
        IReadOnlyList<GitHubBlockingIssue> blockers)
    {
        var reasons = new List<string>();
        if (!string.Equals(issue.State, "OPEN", StringComparison.OrdinalIgnoreCase)) reasons.Add("Issue is closed.");
        if (project.IssueReadyLabel is { Length: > 0 } ready &&
            !issue.Labels.Contains(ready, StringComparer.OrdinalIgnoreCase)) reasons.Add($"Issue is missing ready label '{ready}'.");
        if (project.IssueBlockedLabel is { Length: > 0 } blocked &&
            issue.Labels.Contains(blocked, StringComparer.OrdinalIgnoreCase)) reasons.Add($"Issue has blocked label '{blocked}'.");
        var openBlockers = blockers.Where(blocker => string.Equals(blocker.State, "open", StringComparison.OrdinalIgnoreCase))
            .Select(blocker => blocker.Number).ToArray();
        if (openBlockers.Length > 0)
        {
            var blockerSummary = string.Join(", ", openBlockers.Take(10)
                .Select(number => "#" + number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var remaining = openBlockers.Length - 10;
            reasons.Add("Issue is blocked by open Issue(s): " + blockerSummary +
                (remaining > 0 ? $" (+{remaining.ToString(System.Globalization.CultureInfo.InvariantCulture)} more)." : "."));
        }
        return new(issue.Number, issue.Title, issue.Body, issue.State, issue.CreatedAtUtc, issue.UpdatedAtUtc, issue.Url,
            issue.Labels, blockers, reasons.Count == 0, reasons);
    }

    private static IssueFields ReadIssue(JsonElement item, string repository)
    {
        var number = item.GetProperty("number").GetInt32();
        if (number <= 0) throw new JsonException("GitHub Issue number must be positive.");
        var labels = item.TryGetProperty("labels", out var labelArray) && labelArray.ValueKind == JsonValueKind.Array
            ? labelArray.EnumerateArray().Select(label => RequiredString(label, "name")).ToArray()
            : throw new JsonException("GitHub Issue labels were missing.");
        var url = RequiredString(item, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var issueUrl) || !ValidIssueUrl(url, repository, number))
            throw new JsonException("GitHub Issue URL was invalid.");
        var state = RequiredString(item, "state");
        if (state is not ("OPEN" or "CLOSED")) throw new JsonException("GitHub Issue state was invalid.");
        return new(number, RequiredString(item, "title"), item.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String
                ? body.GetString() ?? string.Empty : string.Empty,
            state, item.GetProperty("createdAt").GetDateTimeOffset(),
            item.GetProperty("updatedAt").GetDateTimeOffset(), issueUrl.AbsoluteUri, labels);
    }

    private static bool ValidIssueUrl(string value, string repository, int issueNumber) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) && uri.Port == 443 &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
        string.Equals(uri.AbsolutePath, $"/{repository}/issues/{issueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}", StringComparison.OrdinalIgnoreCase);

    private static string RequiredString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            ? text : throw new JsonException($"GitHub response is missing '{property}'.");

    private static IReadOnlyList<JsonElement> ParsePages(string output)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(output), new JsonReaderOptions { AllowMultipleValues = true });
        var pages = new List<JsonElement>();
        while (reader.Read())
        {
            using var page = JsonDocument.ParseValue(ref reader);
            pages.Add(page.RootElement.Clone());
            if (pages.Count > 25) throw new JsonException("GitHub returned too many dependency pages.");
        }
        if (pages.Count == 0) throw new JsonException("GitHub returned no dependency pages.");
        return pages;
    }

    private static bool IsNotFound(string error) => error.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("could not resolve to an issue", StringComparison.OrdinalIgnoreCase);

    private static void ValidateProject(CentralProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (GitHubRepositoryValidation.Error(project) is { } error) throw new InvalidDataException(error);
    }

    internal static async Task<GitHubReadCommandResult> RunGhAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("gh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new IOException("GitHub CLI could not be started.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new IOException("GitHub CLI is not available to the Server service account.", exception);
        }
        var stdout = ReadBoundedAsync(process.StandardOutput, 1_000_000);
        var stderr = ReadBoundedAsync(process.StandardError, 16_000);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new IOException("GitHub read operation timed out.");
        }
        return new(process.ExitCode, await stdout, await stderr);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumCharacters)
    {
        var buffer = new char[4096];
        var output = new StringBuilder(Math.Min(maximumCharacters, 8192));
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            var remaining = maximumCharacters - output.Length;
            if (remaining > 0) output.Append(buffer, 0, Math.Min(count, remaining));
        }
        return output.ToString();
    }

    private sealed record IssueFields(int Number, string Title, string Body, string State, DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc, string Url, IReadOnlyList<string> Labels);
}

/// <summary>Coordinates explicit Server Issue reads, eligibility checks, queueing, and assignment gating.</summary>
public sealed class ServerGitHubAdministrationService(IRegistryStore registry, IServerGitHubReadService github,
    TimeProvider? timeProvider = null, IServerGitHubIssueWriteService? issueWriter = null) : IServerGitHubAdministrationService
{
    private const int MaximumRejectedAssignmentsPerRequest = 100;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly IServerGitHubIssueWriteService _issueWriter = issueWriter ?? new ServerGitHubIssueWriteService();

    public async Task<GitHubRepositoryAccess?> CheckAccessAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var project = await registry.GetProjectAsync(projectId, cancellationToken);
        return project is null ? null : await github.CheckAccessAsync(project, cancellationToken);
    }

    public async Task<IReadOnlyList<ManagedGitHubIssue>?> ListIssuesAsync(string projectId, GitHubIssueQuery query,
        CancellationToken cancellationToken = default)
    {
        if (GitHubIssueQueryValidation.Error(query) is { } queryError) throw new InvalidDataException(queryError);
        var project = await registry.GetProjectAsync(projectId, cancellationToken);
        return project is null ? null : await github.ListIssuesAsync(project, query, cancellationToken);
    }

    public async Task<ManagedGitHubIssue?> GetIssueAsync(string projectId, int issueNumber, CancellationToken cancellationToken = default)
    {
        var project = await registry.GetProjectAsync(projectId, cancellationToken);
        return project is null ? null : await github.GetIssueAsync(project, issueNumber, cancellationToken);
    }

    public async Task<GitHubIssueMutationResult> CreateIssueAsync(string projectId, GitHubIssueCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        var project = await RequireProjectAsync(projectId, cancellationToken);
        ValidateMutationProject(project);
        if (GitHubIssueMutationValidation.CreateError(request) is { } error) throw new InvalidDataException(error);
        if (request.PreviewOnly)
            return new("create", project.Repository, true, true, null, null, request.Title, request.Body, null, null, null);
        var title = request.Title ?? throw new InvalidDataException("Issue title is required.");
        var body = request.Body ?? throw new InvalidDataException("Issue body is required.");
        var created = await _issueWriter.CreateIssueAsync(project, title, body, cancellationToken);
        return new("create", project.Repository, false, true, created.Number, created.Url, created.Title, created.Body, null, null, null);
    }

    public async Task<GitHubIssueMutationResult> UpdateIssueAsync(string projectId, int issueNumber,
        GitHubIssueUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var project = await RequireProjectAsync(projectId, cancellationToken);
        ValidateMutationProject(project);
        ValidateIssueNumber(issueNumber);
        if (GitHubIssueMutationValidation.UpdateError(request) is { } error) throw new InvalidDataException(error);
        var issue = await github.GetIssueAsync(project, issueNumber, cancellationToken)
            ?? throw new GitHubIssueNotFoundException(project.Repository, issueNumber);
        var title = request.Title ?? issue.Title;
        var body = request.Body ?? issue.Body;
        var changed = !string.Equals(title, issue.Title, StringComparison.Ordinal) ||
            !string.Equals(body, issue.Body, StringComparison.Ordinal);
        if (request.PreviewOnly || !changed)
            return new("update", project.Repository, request.PreviewOnly, changed, issue.Number, issue.Url, title, body, null, null, null);
        var updated = await _issueWriter.UpdateIssueAsync(project, issueNumber, request.Title, request.Body, cancellationToken);
        return new("update", project.Repository, false, true, updated.Number, updated.Url, updated.Title, updated.Body, null, null, null);
    }

    public async Task<GitHubIssueMutationResult> SetIssueLabelAsync(string projectId, int issueNumber,
        GitHubIssueLabelRequest request, CancellationToken cancellationToken = default)
    {
        var project = await RequireProjectAsync(projectId, cancellationToken);
        ValidateMutationProject(project);
        ValidateIssueNumber(issueNumber);
        if (request is null) throw new InvalidDataException("Issue label request is required.");
        if (GitHubIssueMutationValidation.LabelError(project, request.Label) is { } error) throw new InvalidDataException(error);
        var label = request.Label ?? throw new InvalidDataException("Issue label is required.");
        var issue = await github.GetIssueAsync(project, issueNumber, cancellationToken)
            ?? throw new GitHubIssueNotFoundException(project.Repository, issueNumber);
        var contains = issue.Labels.Contains(label, StringComparer.OrdinalIgnoreCase);
        var changed = contains != request.Applied;
        if (request.PreviewOnly || !changed)
            return new("label", project.Repository, request.PreviewOnly, changed, issue.Number, issue.Url, issue.Title, null,
                label, request.Applied, null);
        if (request.Applied) await _issueWriter.AddLabelAsync(project, issueNumber, label, cancellationToken);
        else await _issueWriter.RemoveLabelAsync(project, issueNumber, label, cancellationToken);
        return new("label", project.Repository, false, true, issue.Number, issue.Url, issue.Title, null,
            label, request.Applied, null);
    }

    public async Task<GitHubIssueMutationResult> SetIssueBlockedByAsync(string projectId, int issueNumber,
        GitHubIssueDependencyRequest request, CancellationToken cancellationToken = default)
    {
        var project = await RequireProjectAsync(projectId, cancellationToken);
        ValidateMutationProject(project);
        if (request is null) throw new InvalidDataException("Issue dependency request is required.");
        if (GitHubIssueMutationValidation.DependencyError(issueNumber, request.BlockerIssueNumber) is { } error)
            throw new InvalidDataException(error);
        var issue = await github.GetIssueAsync(project, issueNumber, cancellationToken)
            ?? throw new GitHubIssueNotFoundException(project.Repository, issueNumber);
        var blocker = await github.GetIssueAsync(project, request.BlockerIssueNumber, cancellationToken)
            ?? throw new GitHubIssueNotFoundException(project.Repository, request.BlockerIssueNumber);
        var contains = issue.BlockedBy.Any(item => item.Number == blocker.Number);
        var changed = contains != request.Applied;
        if (request.PreviewOnly || !changed)
            return new("blocked-by", project.Repository, request.PreviewOnly, changed, issue.Number, issue.Url,
                issue.Title, null, null, request.Applied, blocker.Number);
        if (request.Applied) await _issueWriter.AddBlockedByAsync(project, issueNumber, blocker.Number, cancellationToken);
        else await _issueWriter.RemoveBlockedByAsync(project, issueNumber, blocker.Number, cancellationToken);
        return new("blocked-by", project.Repository, false, true, issue.Number, issue.Url, issue.Title, null,
            null, request.Applied, blocker.Number);
    }

    public async Task<ExecutionRequest> EnqueueIssueAsync(string projectId, WorkReference workReference,
        CancellationToken cancellationToken = default)
    {
        var project = await registry.GetProjectAsync(projectId, cancellationToken)
            ?? throw new KeyNotFoundException($"Project '{projectId}' was not found.");
        if (!project.Enabled) throw new ProjectDisabledException();
        var canonical = ExecutionRequestValidation.Canonicalize(workReference, project.Repository);
        var issueNumber = int.Parse(canonical.Id, System.Globalization.CultureInfo.InvariantCulture);
        var issue = await github.GetIssueAsync(project, issueNumber, cancellationToken)
            ?? throw new GitHubIssueNotFoundException(project.Repository, issueNumber);
        if (!issue.IsEligible) throw new ManagedIssueIneligibleException(issue);
        var url = $"https://github.com/{project.Repository}/issues/{canonical.Id}";
        var created = await registry.EnqueueExecutionAsync(new(projectId, new WorkReference("github-issue", canonical.Id, url)), cancellationToken);
        return await registry.UpdateManagedEligibilityAsync(created.Id,
            new("eligible", [], _clock.GetUtcNow()), cancellationToken) ?? created;
    }

    public async Task<IReadOnlyList<ExecutionRequest>?> RefreshQueuedEligibilityAsync(string projectId, int issueNumber,
        CancellationToken cancellationToken = default)
    {
        var project = await registry.GetProjectAsync(projectId, cancellationToken);
        if (project is null) return null;
        if (issueNumber <= 0) throw new InvalidDataException("Issue number must be positive.");
        var number = issueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var queued = await registry.ListQueuedByIssueAsync(projectId, number, cancellationToken);
        if (queued.Count == 0) return queued;
        var issue = await github.GetIssueAsync(project, issueNumber, cancellationToken);
        var update = EligibilityUpdate(issue, project.Repository, issueNumber);
        foreach (var execution in queued)
            await registry.UpdateManagedEligibilityAsync(execution.Id, update, cancellationToken);
        return await registry.ListQueuedByIssueAsync(projectId, number, cancellationToken);
    }

    public async Task<WorkAssignmentResponse> RequestAssignmentAsync(WorkerAssignmentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null) throw new InvalidDataException("Worker assignment request contract is invalid.");
        if (!request.WorkerEnabled || request.AvailableCapacity == 0 || request.ProjectCapacities is null)
            return await registry.RequestAssignmentAsync(request, cancellationToken);
        for (var attempt = 0; attempt < MaximumRejectedAssignmentsPerRequest; attempt++)
        {
            var response = await registry.RequestAssignmentAsync(request, cancellationToken);
            if (!response.HasWork || response.Assignment is null) return response;
            var assignment = response.Assignment;
            ManagedGitHubIssue? issue;
            try
            {
                issue = await github.GetIssueAsync(assignment.Project,
                    int.Parse(assignment.Work.Id, System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
                var update = EligibilityUpdate(issue, assignment.Project.Repository,
                    int.Parse(assignment.Work.Id, System.Globalization.CultureInfo.InvariantCulture));
                if (issue?.IsEligible == true)
                {
                    await registry.UpdateManagedEligibilityAsync(assignment.ServerExecutionId, update, CancellationToken.None);
                    return response;
                }
                await registry.RejectManagedAssignmentAsync(assignment.ServerExecutionId, assignment.AssignmentId,
                    assignment.WorkerId, update, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                await registry.RejectManagedAssignmentAsync(assignment.ServerExecutionId, assignment.AssignmentId, assignment.WorkerId,
                    new("unavailable", ["GitHub eligibility check was canceled."], _clock.GetUtcNow()), CancellationToken.None);
                throw;
            }
            catch
            {
                await registry.RejectManagedAssignmentAsync(assignment.ServerExecutionId, assignment.AssignmentId, assignment.WorkerId,
                    new("unavailable", ["GitHub eligibility could not be checked."], _clock.GetUtcNow()), CancellationToken.None);
                throw;
            }
        }
        return new(false, null);
    }

    private ManagedEligibilityUpdate EligibilityUpdate(ManagedGitHubIssue? issue, string repository, int issueNumber) => issue is null
        ? new("blocked", [$"GitHub Issue #{issueNumber} is missing or is no longer visible in '{repository}'."], _clock.GetUtcNow())
        : new(issue.IsEligible ? "eligible" : "blocked", issue.EligibilityReasons, _clock.GetUtcNow());

    private async Task<CentralProject> RequireProjectAsync(string projectId, CancellationToken cancellationToken) =>
        await registry.GetProjectAsync(projectId, cancellationToken)
        ?? throw new KeyNotFoundException($"Project '{projectId}' was not found.");

    private static void ValidateMutationProject(CentralProject project)
    {
        if (GitHubRepositoryValidation.Error(project) is { } error) throw new InvalidDataException(error);
    }

    private static void ValidateIssueNumber(int issueNumber)
    {
        if (issueNumber <= 0) throw new InvalidDataException("Issue number must be positive.");
    }
}
