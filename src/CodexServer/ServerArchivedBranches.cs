namespace CodexServer;

using System.Globalization;
using CodexProvisioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>One archived branch on GitHub. Decision is eligible, recent, not-integrated or unknown.</summary>
public sealed record ArchivedBranchItem(string Branch, string Tip, DateTimeOffset? CommittedAtUtc, string Decision, string Reason);
public sealed record ArchivedBranchInventory(string ProjectId, string Repository, string BaseBranch, int OlderThanDays,
    int Offset, int Total, IReadOnlyList<ArchivedBranchItem> Branches);
public sealed record ArchivedBranchSelection(string Branch, string ExpectedTip);
public sealed record ArchivedBranchDeleteRequest(int OlderThanDays, IReadOnlyList<ArchivedBranchSelection>? Branches);
/// <summary>Outcome is deleted, missing, changed, retained, failed or not-attempted.</summary>
public sealed record ArchivedBranchDeleteItem(string Branch, string Outcome, string Reason);
public sealed record ArchivedBranchDeleteResult(string ProjectId, string Repository, IReadOnlyList<ArchivedBranchDeleteItem> Branches);

public sealed class ArchivedBranchUnavailableException(string code, string message, Exception? inner = null) : IOException(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// Finds and deletes archived feature branches directly on GitHub with the Server's own login.
/// A branch qualifies only when its exact tip is already contained in the project's base branch,
/// so deleting it can never lose a commit; age is a retention preference on top of that.
/// </summary>
public sealed class ServerArchivedBranchService(
    Func<IReadOnlyList<string>, CancellationToken, Task<GitHubReadCommandResult>>? run = null,
    TimeProvider? clock = null, ILogger<ServerArchivedBranchService>? logger = null)
{
    /// <summary>Branch prefixes Workers use when they preserve integrated work.</summary>
    public static readonly IReadOnlyList<string> Prefixes = ["done/feature/", "complete/feature/", "completed/"];
    public const int PageSize = 100;
    private const int MaximumRefs = 10_000;
    private const int Parallelism = 6;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<GitHubReadCommandResult>> _run = run ?? ServerGitHubReadService.RunGhAsync;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public static bool Archived(string branch) =>
        Prefixes.Any(prefix => branch.StartsWith(prefix, StringComparison.Ordinal) && branch.Length > prefix.Length) &&
        ExecutionMaintenanceProtocol.SafeBranchName(branch);

    public static string? RequestError(int olderThanDays, int offset) =>
        olderThanDays is < 1 or > 36500 ? "olderThanDays must be between 1 and 36500." :
        offset is < 0 or > MaximumRefs ? $"offset must be between 0 and {MaximumRefs}." : null;

    public static string? RequestError(ArchivedBranchDeleteRequest? request) =>
        request is null ? "A delete request is required." :
        RequestError(request.OlderThanDays, 0) is { } error ? error :
        request.Branches is not { Count: > 0 and <= PageSize } ? $"Select between 1 and {PageSize} branches." :
        request.Branches.Any(b => b is null || !Archived(b.Branch) || b.ExpectedTip is not { Length: 40 or 64 } || !b.ExpectedTip.All(char.IsAsciiHexDigit))
            ? "Every selection needs an archived branch name and its previewed commit." :
        request.Branches.Select(b => b.Branch).Distinct(StringComparer.Ordinal).Count() != request.Branches.Count
            ? "Each branch can be selected once." : null;

    public async Task<ArchivedBranchInventory> ListAsync(CentralProject project, int olderThanDays, int offset, CancellationToken ct)
    {
        ValidateProject(project);
        if (RequestError(olderThanDays, offset) is { } error) throw new InvalidDataException(error);
        var refs = new List<(string Branch, string Tip)>();
        foreach (var prefix in Prefixes)
            refs.AddRange(await ListRefsAsync(project.Repository, prefix, ct));
        var ordered = refs.DistinctBy(r => r.Branch).OrderBy(r => r.Branch, StringComparer.Ordinal).ToArray();
        var page = ordered.Skip(offset).Take(PageSize).ToArray();
        var items = new ArchivedBranchItem[page.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, page.Length), new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (index, token) => items[index] = await AssessAsync(project, page[index].Branch, page[index].Tip, olderThanDays, token));
        return new(project.Id, project.Repository, project.DefaultBranch, olderThanDays, offset, ordered.Length, items);
    }

    /// <summary>
    /// Re-reads and re-assesses every selected branch immediately before deleting it. A selection whose
    /// tip moved, that is no longer contained in the base or became too recent is kept. The first
    /// rejected or uncertain deletion stops the remaining ones so the operator can look first.
    /// </summary>
    public async Task<ArchivedBranchDeleteResult> DeleteAsync(CentralProject project, ArchivedBranchDeleteRequest request, CancellationToken ct)
    {
        ValidateProject(project);
        if (RequestError(request) is { } error) throw new InvalidDataException(error);
        var results = new List<ArchivedBranchDeleteItem>();
        var stopped = false;
        foreach (var selection in request.Branches!)
        {
            if (stopped || ct.IsCancellationRequested)
            {
                results.Add(new(selection.Branch, "not-attempted", stopped ? "stopped-after-failure" : "cancelled"));
                continue;
            }
            var tip = await ReadRefAsync(project.Repository, selection.Branch, ct);
            if (tip is null) { results.Add(new(selection.Branch, "missing", "branch-missing")); continue; }
            if (!string.Equals(tip, selection.ExpectedTip, StringComparison.OrdinalIgnoreCase))
            { results.Add(new(selection.Branch, "changed", "tip-changed")); continue; }
            var assessment = await AssessAsync(project, selection.Branch, tip, request.OlderThanDays, ct);
            if (assessment.Decision != "eligible") { results.Add(new(selection.Branch, "retained", assessment.Reason)); continue; }
            var deleted = await RunAsync(["api", "--method", "DELETE", RefPath(project.Repository, selection.Branch)], ct);
            if (deleted.ExitCode == 0)
            {
                logger?.LogInformation("Deleted archived branch {Branch} at {Tip} from {Repository}; the commit is contained in {Base}.",
                    selection.Branch, tip, project.Repository, project.DefaultBranch);
                results.Add(new(selection.Branch, "deleted", "deleted"));
                continue;
            }
            stopped = true;
            var reason = Classify(deleted.StandardError);
            logger?.LogWarning("GitHub refused to delete archived branch {Branch} from {Repository}: {Reason}.", selection.Branch, project.Repository, reason);
            results.Add(new(selection.Branch, "failed", reason));
        }
        return new(project.Id, project.Repository, results);
    }

    private async Task<ArchivedBranchItem> AssessAsync(CentralProject project, string branch, string tip, int olderThanDays, CancellationToken ct)
    {
        // base...tip: "behind" or "identical" means every commit of the branch is already in the base.
        var compare = await RunAsync(["api", $"repos/{project.Repository}/compare/{Uri.EscapeDataString(project.DefaultBranch)}...{tip}?per_page=1",
            "--jq", "[.status, .merge_base_commit.sha, .merge_base_commit.commit.committer.date] | @tsv"], ct);
        if (compare.ExitCode != 0)
            return new(branch, tip, null, "unknown", Classify(compare.StandardError) == "not-found" ? "commit-not-found" : "comparison-failed");
        var fields = compare.StandardOutput.Trim().Split('\t');
        if (fields.Length != 3) throw new ArchivedBranchUnavailableException("invalid-response", "GitHub returned an unexpected branch comparison.");
        if (fields[0] is not ("behind" or "identical"))
            return new(branch, tip, null, "not-integrated", "not-in-base");
        if (!string.Equals(fields[1], tip, StringComparison.OrdinalIgnoreCase) ||
            !DateTimeOffset.TryParse(fields[2], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var committed))
            throw new ArchivedBranchUnavailableException("invalid-response", "GitHub returned an unexpected branch comparison.");
        return committed > _clock.GetUtcNow().AddDays(-olderThanDays)
            ? new(branch, tip, committed, "recent", "newer-than-threshold")
            : new(branch, tip, committed, "eligible", "integrated");
    }

    private async Task<IReadOnlyList<(string Branch, string Tip)>> ListRefsAsync(string repository, string prefix, CancellationToken ct)
    {
        // matching-refs is a plain prefix match; the trailing slash is checked below.
        var result = await RunAsync(["api", "--paginate", $"repos/{repository}/git/matching-refs/heads/{prefix.TrimEnd('/')}?per_page=100",
            "--jq", ".[] | [.ref, .object.type, .object.sha] | @tsv"], ct);
        if (result.ExitCode != 0)
            throw new ArchivedBranchUnavailableException(Classify(result.StandardError),
                $"GitHub could not list branches of '{repository}'. Check the Server GitHub login and its access to the repository.");
        var refs = new List<(string, string)>();
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length != 3 || !fields[0].StartsWith("refs/heads/", StringComparison.Ordinal) || !IsCommitId(fields[2]))
                throw new ArchivedBranchUnavailableException("invalid-response", "GitHub returned an unexpected branch list.");
            var branch = fields[0]["refs/heads/".Length..];
            if (fields[1] == "commit" && branch.StartsWith(prefix, StringComparison.Ordinal) && Archived(branch)) refs.Add((branch, fields[2]));
            if (refs.Count > MaximumRefs) throw new ArchivedBranchUnavailableException("too-many-branches", $"More than {MaximumRefs} archived branches.");
        }
        return refs;
    }

    private async Task<string?> ReadRefAsync(string repository, string branch, CancellationToken ct)
    {
        var result = await RunAsync(["api", RefPath(repository, branch).Replace("/git/refs/", "/git/ref/", StringComparison.Ordinal),
            "--jq", "[.ref, .object.type, .object.sha] | @tsv"], ct);
        if (result.ExitCode != 0)
            return Classify(result.StandardError) == "not-found" ? null
                : throw new ArchivedBranchUnavailableException(Classify(result.StandardError), $"GitHub could not read branch '{branch}'.");
        var fields = result.StandardOutput.Trim().Split('\t');
        return fields.Length == 3 && fields[0] == "refs/heads/" + branch && fields[1] == "commit" && IsCommitId(fields[2]) ? fields[2]
            : throw new ArchivedBranchUnavailableException("invalid-response", $"GitHub returned an unexpected ref for '{branch}'.");
    }

    private async Task<GitHubReadCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        try { return await _run(arguments, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new ArchivedBranchUnavailableException("github-unavailable",
                "GitHub could not be reached with the Server login. Check the Server GitHub CLI authentication.", exception);
        }
    }

    private static string RefPath(string repository, string branch) =>
        $"repos/{repository}/git/refs/heads/{string.Join('/', branch.Split('/').Select(Uri.EscapeDataString))}";

    private static bool IsCommitId(string value) => value is { Length: 40 or 64 } && value.All(char.IsAsciiHexDigit);

    private static string Classify(string error) =>
        error.Contains("404", StringComparison.Ordinal) || error.Contains("not found", StringComparison.OrdinalIgnoreCase) ? "not-found" :
        error.Contains("401", StringComparison.Ordinal) || error.Contains("authentication", StringComparison.OrdinalIgnoreCase) ? "authentication" :
        error.Contains("403", StringComparison.Ordinal) || error.Contains("forbidden", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("resource not accessible", StringComparison.OrdinalIgnoreCase) ? "permission" :
        error.Contains("422", StringComparison.Ordinal) || error.Contains("protected", StringComparison.OrdinalIgnoreCase) ? "rejected" :
        "github-failed";

    private static void ValidateProject(CentralProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (GitHubRepositoryValidation.Error(project) is { } error) throw new InvalidDataException(error);
        if (!ExecutionMaintenanceProtocol.SafeBranchName(project.DefaultBranch)) throw new InvalidDataException("Project base branch is invalid.");
    }
}

public static partial class ServerApplication
{
    private static void MapArchivedBranches(WebApplication app)
    {
        app.MapGet("/api/v1/projects/{projectId}/archived-branches", async (string projectId, int? olderThanDays, int? offset,
            HttpContext context, ServerConfiguration settings, IRegistryStore registry, ServerArchivedBranchService branches) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            context.Response.Headers.CacheControl = "no-store";
            var project = await registry.GetProjectAsync(projectId, context.RequestAborted);
            if (project is null) return Results.NotFound();
            if (ServerArchivedBranchService.RequestError(olderThanDays ?? 30, offset ?? 0) is { } error) return Results.BadRequest(new { error });
            return await ArchivedBranchResultAsync(() => branches.ListAsync(project, olderThanDays ?? 30, offset ?? 0, context.RequestAborted));
        });
        app.MapPost("/api/v1/projects/{projectId}/archived-branches/delete", async (string projectId, ArchivedBranchDeleteRequest? request,
            HttpContext context, ServerConfiguration settings, IRegistryStore registry, ServerArchivedBranchService branches) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            var project = await registry.GetProjectAsync(projectId, context.RequestAborted);
            if (project is null) return Results.NotFound();
            if (ServerArchivedBranchService.RequestError(request) is { } error) return Results.BadRequest(new { error });
            return await ArchivedBranchResultAsync(() => branches.DeleteAsync(project, request!, context.RequestAborted));
        });
    }

    private static async Task<IResult> ArchivedBranchResultAsync<T>(Func<Task<T>> operation)
    {
        try { return Results.Ok(await operation()); }
        catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (ArchivedBranchUnavailableException ex)
        { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status503ServiceUnavailable); }
    }
}
