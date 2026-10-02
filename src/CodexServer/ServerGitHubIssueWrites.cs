namespace CodexServer;

using System.Text.Json;
using System.Text.RegularExpressions;

public sealed record GitHubIssueCreateRequest(string? Title, string? Body, bool PreviewOnly = false);
public sealed record GitHubIssueUpdateRequest(string? Title = null, string? Body = null, bool PreviewOnly = false);
public sealed record GitHubIssueLabelRequest(string? Label, bool Applied, bool PreviewOnly = false);
public sealed record GitHubIssueDependencyRequest(int BlockerIssueNumber, bool Applied, bool PreviewOnly = false);
public sealed record GitHubIssueMutationResult(string Operation, string Repository, bool PreviewOnly, bool Changed,
    int? IssueNumber, string? Url, string? Title, string? Body, string? Label, bool? Applied, int? RelatedIssueNumber,
    int ContractVersion = 1);
public sealed record GitHubIssueParentRequest(int? ParentIssueNumber, bool PreviewOnly = false);
public sealed record GitHubIssueSubIssueBatchRequest(IReadOnlyList<int>? ChildIssueNumbers, bool Applied = true,
    bool PreviewOnly = false);
public sealed record GitHubIssueDependencyBatchRequest(IReadOnlyList<int>? BlockerIssueNumbers, bool Applied,
    bool PreviewOnly = false);
public sealed record GitHubIssueWriteResponse(int Number, string Title, string Body, string Url);

public sealed class GitHubIssueWriteUnavailableException(string repository, string code, string message, Exception? inner = null)
    : IOException(message, inner)
{
    public string Repository { get; } = repository;
    public string Code { get; } = code;
}

public interface IServerGitHubIssueWriteService
{
    Task<GitHubIssueWriteResponse> CreateIssueAsync(CentralProject project, string title, string body,
        CancellationToken cancellationToken = default);
    Task<GitHubIssueWriteResponse> UpdateIssueAsync(CentralProject project, int issueNumber, string? title, string? body,
        CancellationToken cancellationToken = default);
    Task AddLabelAsync(CentralProject project, int issueNumber, string label, CancellationToken cancellationToken = default);
    Task RemoveLabelAsync(CentralProject project, int issueNumber, string label, CancellationToken cancellationToken = default);
    Task AddBlockedByAsync(CentralProject project, int issueNumber, int blockerIssueNumber,
        CancellationToken cancellationToken = default);
    Task RemoveBlockedByAsync(CentralProject project, int issueNumber, int blockerIssueNumber,
        CancellationToken cancellationToken = default);
    Task AddSubIssueAsync(CentralProject project, int parentIssueNumber, int childIssueNumber,
        CancellationToken cancellationToken = default);
    Task RemoveSubIssueAsync(CentralProject project, int parentIssueNumber, int childIssueNumber,
        CancellationToken cancellationToken = default);
}

public static partial class GitHubRepositoryValidation
{
    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?/(?!\\.{1,2}$)[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();

    public static string? Error(CentralProject? project) => project is null ||
        string.IsNullOrWhiteSpace(project.Repository) || !RepositoryPattern().IsMatch(project.Repository)
        ? "Project repository is not a supported GitHub owner/repository identifier."
        : null;
}

public static class GitHubIssueMutationValidation
{
    private const int MaximumTitleLength = 256;
    private const int MaximumBodyLength = 65_536;
    private const int MaximumLabelLength = 100;

    public static string? CreateError(GitHubIssueCreateRequest? request)
    {
        if (request is null) return "Issue creation request is required.";
        if (TitleError(request.Title) is { } titleError) return titleError;
        return BodyError(request.Body);
    }

    public static string? UpdateError(GitHubIssueUpdateRequest? request)
    {
        if (request is null) return "Issue update request is required.";
        if (request.Title is null && request.Body is null) return "Supply a title, a body, or both to update.";
        if (request.Title is not null && TitleError(request.Title) is { } titleError) return titleError;
        return request.Body is not null ? BodyError(request.Body) : null;
    }

    public static string? LabelError(CentralProject project, string? label)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length > MaximumLabelLength || label.Any(char.IsControl))
            return "Label must contain 1 to 100 printable characters.";
        if (!ConfiguredLabels(project).Any(value => string.Equals(value, label, StringComparison.OrdinalIgnoreCase)))
            return "Only this project's configured issueReadyLabel or issueBlockedLabel can be administered. Configure the label on the project first.";
        return null;
    }

    public static string? DependencyError(int issueNumber, int blockerIssueNumber) =>
        issueNumber <= 0 || blockerIssueNumber <= 0
            ? "Issue numbers must be positive."
            : issueNumber == blockerIssueNumber
                ? "An Issue cannot be blocked by itself."
                : null;

    public static string? ParentError(int childIssueNumber, int? parentIssueNumber) =>
        childIssueNumber <= 0 || parentIssueNumber is <= 0
            ? "Issue numbers must be positive."
            : parentIssueNumber == childIssueNumber
                ? "An Issue cannot be its own parent."
                : null;

    public static string? BatchIssueNumbersError(int issueNumber, IReadOnlyList<int>? relatedIssueNumbers,
        string relationshipName)
    {
        if (issueNumber <= 0) return "Issue number must be positive.";
        if (relatedIssueNumbers is null || relatedIssueNumbers.Count is < 1 or > 50)
            return $"A {relationshipName} batch must contain 1 to 50 Issue numbers.";
        if (relatedIssueNumbers.Any(number => number <= 0)) return "Issue numbers must be positive.";
        if (relatedIssueNumbers.Contains(issueNumber)) return $"An Issue cannot be related to itself as {relationshipName}.";
        if (relatedIssueNumbers.Distinct().Count() != relatedIssueNumbers.Count)
            return $"A {relationshipName} batch cannot contain duplicate Issue numbers.";
        return null;
    }

    private static string? TitleError(string? title) => string.IsNullOrWhiteSpace(title) || title.Length > MaximumTitleLength ||
        title.Any(char.IsControl) ? "Issue title must contain 1 to 256 printable characters." : null;

    private static string? BodyError(string? body) => body is null || body.Length > MaximumBodyLength
        ? "Issue body must be supplied and contain at most 65536 characters."
        : body.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'))
            ? "Issue body contains an unsupported control character."
            : null;

    private static IEnumerable<string> ConfiguredLabels(CentralProject project)
    {
        if (!string.IsNullOrWhiteSpace(project.IssueReadyLabel)) yield return project.IssueReadyLabel;
        if (!string.IsNullOrWhiteSpace(project.IssueBlockedLabel)) yield return project.IssueBlockedLabel;
    }
}

/// <summary>Runs the Server's typed GitHub Issue write operations through gh's argument-list API.</summary>
public sealed class ServerGitHubIssueWriteService : IServerGitHubIssueWriteService
{
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<GitHubReadCommandResult>> _run;

    public ServerGitHubIssueWriteService(
        Func<IReadOnlyList<string>, CancellationToken, Task<GitHubReadCommandResult>>? run = null) =>
        _run = run ?? ServerGitHubReadService.RunGhAsync;

    public async Task<GitHubIssueWriteResponse> CreateIssueAsync(CentralProject project, string title, string body,
        CancellationToken cancellationToken = default)
    {
        ValidateProject(project);
        var validation = GitHubIssueMutationValidation.CreateError(new(title, body));
        if (validation is not null) throw new InvalidDataException(validation);
        var result = await RunAsync(project.Repository,
            ["api", "--method", "POST", $"repos/{project.Repository}/issues", "-f", $"title={title}", "-f", $"body={body}",
                "--jq", "{number,title,body,html_url}"], "create Issue", cancellationToken);
        return ReadWriteResponse(result, project.Repository);
    }

    public async Task<GitHubIssueWriteResponse> UpdateIssueAsync(CentralProject project, int issueNumber, string? title, string? body,
        CancellationToken cancellationToken = default)
    {
        ValidateProject(project);
        var validation = GitHubIssueMutationValidation.UpdateError(new(title, body));
        if (validation is not null) throw new InvalidDataException(validation);
        ValidateIssueNumber(issueNumber);
        var arguments = new List<string> { "api", "--method", "PATCH", $"repos/{project.Repository}/issues/{issueNumber}" };
        if (title is not null) { arguments.Add("-f"); arguments.Add($"title={title}"); }
        if (body is not null) { arguments.Add("-f"); arguments.Add($"body={body}"); }
        arguments.AddRange(["--jq", "{number,title,body,html_url}"]);
        var result = await RunAsync(project.Repository, arguments, "update Issue", cancellationToken);
        return ReadWriteResponse(result, project.Repository);
    }

    public async Task AddLabelAsync(CentralProject project, int issueNumber, string label, CancellationToken cancellationToken = default)
    {
        ValidateProject(project);
        ValidateIssueNumber(issueNumber);
        ValidateConfiguredLabel(project, label);
        await RunAsync(project.Repository,
            ["api", "--method", "POST", $"repos/{project.Repository}/issues/{issueNumber}/labels", "-f", $"labels[]={label}"],
            "add configured Issue label", cancellationToken);
    }

    public async Task RemoveLabelAsync(CentralProject project, int issueNumber, string label, CancellationToken cancellationToken = default)
    {
        ValidateProject(project);
        ValidateIssueNumber(issueNumber);
        ValidateConfiguredLabel(project, label);
        await RunAsync(project.Repository,
            ["api", "--method", "DELETE", $"repos/{project.Repository}/issues/{issueNumber}/labels/{Uri.EscapeDataString(label)}"],
            "remove configured Issue label", cancellationToken);
    }

    public async Task AddBlockedByAsync(CentralProject project, int issueNumber, int blockerIssueNumber,
        CancellationToken cancellationToken = default)
    {
        ValidateProject(project);
        ValidateDependency(issueNumber, blockerIssueNumber);
        var blockerId = await GetIssueIdAsync(project.Repository, blockerIssueNumber, cancellationToken);
        await RunAsync(project.Repository,
            ["api", "--method", "POST", $"repos/{project.Repository}/issues/{issueNumber}/dependencies/blocked_by", "-F", $"issue_id={blockerId}"],
            "add Issue blocked-by relationship", cancellationToken);
    }

    public async Task RemoveBlockedByAsync(CentralProject project, int issueNumber, int blockerIssueNumber,
        CancellationToken cancellationToken = default)
    {
        ValidateProject(project);
        ValidateDependency(issueNumber, blockerIssueNumber);
        var blockerId = await GetIssueIdAsync(project.Repository, blockerIssueNumber, cancellationToken);
        await RunAsync(project.Repository,
            ["api", "--method", "DELETE", $"repos/{project.Repository}/issues/{issueNumber}/dependencies/blocked_by/{blockerId}"],
            "remove Issue blocked-by relationship", cancellationToken);
    }

    public async Task AddSubIssueAsync(CentralProject project, int parentIssueNumber, int childIssueNumber,
        CancellationToken cancellationToken = default)
    {
        ValidateProject(project);
        ValidateParent(parentIssueNumber, childIssueNumber);
        var childId = await GetIssueIdAsync(project.Repository, childIssueNumber, cancellationToken);
        await RunAsync(project.Repository,
            ["api", "--method", "POST", $"repos/{project.Repository}/issues/{parentIssueNumber}/sub_issues", "-F", $"sub_issue_id={childId}"],
            "add Issue parent relationship", cancellationToken);
    }

    public async Task RemoveSubIssueAsync(CentralProject project, int parentIssueNumber, int childIssueNumber,
        CancellationToken cancellationToken = default)
    {
        ValidateProject(project);
        ValidateParent(parentIssueNumber, childIssueNumber);
        var childId = await GetIssueIdAsync(project.Repository, childIssueNumber, cancellationToken);
        await RunAsync(project.Repository,
            ["api", "--method", "DELETE", $"repos/{project.Repository}/issues/{parentIssueNumber}/sub_issue", "-F", $"sub_issue_id={childId}"],
            "remove Issue parent relationship", cancellationToken);
    }

    private async Task<long> GetIssueIdAsync(string repository, int issueNumber, CancellationToken cancellationToken)
    {
        var result = await RunAsync(repository, ["api", $"repos/{repository}/issues/{issueNumber}", "--jq", ".id"],
            "read Issue relationship identity", cancellationToken);
        if (!long.TryParse(result.StandardOutput.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new GitHubIssueWriteUnavailableException(repository, "invalid-response",
                $"GitHub returned an invalid Issue identity for repository '{repository}'. Check the Issue number and repository access.");
        return id;
    }

    private async Task<GitHubReadCommandResult> RunAsync(string repository, IReadOnlyList<string> arguments, string operation,
        CancellationToken cancellationToken)
    {
        GitHubReadCommandResult result;
        try { result = await _run(arguments, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new GitHubIssueWriteUnavailableException(repository, "write-unavailable",
                $"GitHub could not {operation} in repository '{repository}'. Check Server service-account authentication and scoped Issue write access, then refresh the target before retrying.", exception);
        }
        if (result.ExitCode != 0)
        {
            var (code, guidance) = ClassifyFailure(result.StandardError);
            throw new GitHubIssueWriteUnavailableException(repository, code,
                $"GitHub could not {operation} in repository '{repository}' (gh exit {result.ExitCode}). {guidance} Refresh the target before retrying.");
        }
        return result;
    }

    private static GitHubIssueWriteResponse ReadWriteResponse(GitHubReadCommandResult result, string repository)
    {
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var item = document.RootElement;
            var number = item.GetProperty("number").GetInt32();
            var title = item.GetProperty("title").GetString();
            var bodyElement = item.GetProperty("body");
            var body = bodyElement.ValueKind switch
            {
                JsonValueKind.String => bodyElement.GetString() ?? string.Empty,
                JsonValueKind.Null => string.Empty,
                _ => throw new JsonException("GitHub Issue body was not a string.")
            };
            var url = item.GetProperty("html_url").GetString();
            if (number <= 0 || title is null || GitHubIssueMutationValidation.CreateError(new(title, body)) is not null ||
                !Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) || parsedUrl.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(parsedUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase) || parsedUrl.Port != 443 ||
                !string.IsNullOrEmpty(parsedUrl.UserInfo) || !string.IsNullOrEmpty(parsedUrl.Query) ||
                !string.IsNullOrEmpty(parsedUrl.Fragment) || !string.Equals(parsedUrl.AbsolutePath,
                    $"/{repository}/issues/{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}", StringComparison.OrdinalIgnoreCase))
                throw new JsonException("GitHub Issue mutation response fields did not match the requested repository.");
            return new(number, title, body, parsedUrl.AbsoluteUri);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new GitHubIssueWriteUnavailableException(repository, "invalid-response",
                $"GitHub accepted the Issue mutation in repository '{repository}' but returned an invalid response. Refresh the Issue before retrying.", exception);
        }
    }

    private static (string Code, string Guidance) ClassifyFailure(string error)
    {
        if (error.Contains("401", StringComparison.OrdinalIgnoreCase) || error.Contains("not logged", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("authentication", StringComparison.OrdinalIgnoreCase))
            return ("authentication", "Check the Server service-account GitHub CLI login.");
        if (error.Contains("403", StringComparison.OrdinalIgnoreCase) || error.Contains("forbidden", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("resource not accessible", StringComparison.OrdinalIgnoreCase))
            return ("write-permission", "Grant the Server login scoped Issue write permission for this repository.");
        if (error.Contains("404", StringComparison.OrdinalIgnoreCase) || error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return ("not-found", "Check the project repository and Issue numbers are visible to the Server login.");
        if (error.Contains("422", StringComparison.OrdinalIgnoreCase) || error.Contains("validation", StringComparison.OrdinalIgnoreCase))
            return ("rejected", "GitHub rejected the requested values; refresh the Issue and review the title, body, label, or relationship.");
        return ("write-failed", "Check Server service-account authentication, Issue write access, and the requested Issue state.");
    }

    private static void ValidateProject(CentralProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (GitHubRepositoryValidation.Error(project) is { } error) throw new InvalidDataException(error);
    }

    private static void ValidateIssueNumber(int issueNumber)
    {
        if (issueNumber <= 0) throw new InvalidDataException("Issue number must be positive.");
    }

    private static void ValidateConfiguredLabel(CentralProject project, string label)
    {
        if (GitHubIssueMutationValidation.LabelError(project, label) is { } error) throw new InvalidDataException(error);
    }

    private static void ValidateDependency(int issueNumber, int blockerIssueNumber)
    {
        if (GitHubIssueMutationValidation.DependencyError(issueNumber, blockerIssueNumber) is { } error)
            throw new InvalidDataException(error);
    }

    private static void ValidateParent(int parentIssueNumber, int childIssueNumber)
    {
        if (GitHubIssueMutationValidation.ParentError(childIssueNumber, parentIssueNumber) is { } error)
            throw new InvalidDataException(error);
    }
}
