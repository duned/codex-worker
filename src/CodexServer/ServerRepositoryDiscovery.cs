namespace CodexServer;

using System.Globalization;
using System.Text.Json;

public sealed record ServerRepository(string Repository, string Name, string DefaultBranch, string Description);
public sealed record ServerRepositoryPage(IReadOnlyList<ServerRepository> Repositories, int? NextPage);
public sealed record ProjectRepositoryCheck(string Repository, string DefaultBranch, bool RepositoryReadable,
    bool BranchExists, string Diagnostic);

public sealed partial class ServerGitHubReadService
{
    public async Task<ServerRepositoryPage> ListRepositoriesAsync(int page, CancellationToken cancellationToken = default)
    {
        if (page is < 1 or > 1000) throw new InvalidDataException("page must be between 1 and 1000.");
        // /user/repos includes repositories accessible to the actual CLI authentication context,
        // including organization and collaborator repositories. Fetch exactly one bounded page.
        var result = await RunReadAsync("Server repository discovery", ["api", "--method", "GET",
            $"user/repos?per_page=50&page={page.ToString(CultureInfo.InvariantCulture)}&sort=full_name"], cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 50)
                throw new JsonException();
            var repositories = document.RootElement.EnumerateArray().Select(ReadRepository).ToArray();
            return new(repositories, repositories.Length == 50 && page < 1000 ? page + 1 : null);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new GitHubReadUnavailableException("Server repository discovery", "Repository discovery returned invalid data.", "invalid-response");
        }
    }

    public async Task<ProjectRepositoryCheck> VerifyRepositoryAsync(CentralProjectDefinition definition,
        CancellationToken cancellationToken = default)
    {
        if (CentralProjectValidation.Error(definition) is { } error) throw new InvalidDataException(error);
        var repository = definition.Repository;
        var result = await RunReadAsync(repository, ["api", $"repos/{repository}"], cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (!string.Equals(ReadRepository(document.RootElement).Repository, repository, StringComparison.OrdinalIgnoreCase))
                throw new JsonException();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new GitHubReadUnavailableException(repository, "Repository readability could not be verified.", "invalid-response");
        }
        GitHubReadCommandResult branch;
        try
        {
            branch = await RunReadAsync(repository,
                ["api", $"repos/{repository}/branches/{Uri.EscapeDataString(definition.DefaultBranch)}"], cancellationToken);
        }
        catch (GitHubReadUnavailableException exception)
        {
            throw new GitHubReadUnavailableException(repository,
                $"Base branch '{definition.DefaultBranch}' could not be verified. Check the branch name and Server repository read access.", exception.Code);
        }
        try
        {
            using var document = JsonDocument.Parse(branch.StandardOutput);
            if (document.RootElement.GetProperty("name").GetString() != definition.DefaultBranch) throw new JsonException();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new GitHubReadUnavailableException(repository, "Base branch existence could not be verified.", "invalid-response");
        }
        return new(repository, definition.DefaultBranch, true, true,
            "Server repository read access and base branch verified. Worker authentication, Issue write and Git push authorization remain separate.");
    }

    private static ServerRepository ReadRepository(JsonElement item)
    {
        var repository = item.GetProperty("full_name").GetString() ?? "";
        var name = item.GetProperty("name").GetString() ?? "";
        var branch = item.GetProperty("default_branch").GetString() ?? "";
        var description = item.TryGetProperty("description", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";
        if (CentralProjectValidation.Error(new(name, repository, branch, description)) is not null) throw new JsonException();
        return new(repository, name, branch, description);
    }
}
