using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WorkExecutionToolbox;

public enum GitHubIssueFailure
{
    Authorization,
    RateLimited,
    InvalidResponse,
    Transport,
    Provider,
    LimitExceeded
}

/// <summary>Safe provider diagnostics, without response bodies or credential-bearing inner exceptions.</summary>
public sealed class GitHubIssueException(GitHubIssueFailure failure, string message) : Exception(message)
{
    public GitHubIssueFailure Failure { get; } = failure;
}

/// <summary>
/// GitHub.com Issue resolution. The host owns HTTP lifetime and supplies credentials;
/// the provider never reads CLI state, environment variables or ambient repository defaults.
/// </summary>
public sealed partial class GitHubIssueProvider : IIssueRelationshipProvider, IIssueGraphProvider
{
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _getToken;

    public GitHubIssueProvider(HttpClient http, Func<CancellationToken, Task<string>> getToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(getToken);
        _http = http;
        _getToken = getToken;
    }

    public async Task<IssueSummary?> GetIssueAsync(IssueReference issue, CancellationToken cancellationToken = default) =>
        (await ResolveAsync(issue, cancellationToken))?.Summary;

    // Database IDs remain inside the provider boundary for subsequent relationship operations.
    internal async Task<ResolvedIssue?> ResolveAsync(IssueReference issue, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issue);
        var repository = GitHubRepositoryContext.Create(issue.Repository.Repository);
        using var document = await SendAsync(HttpMethod.Get,
            $"repos/{repository.Repository}/issues/{issue.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            null, allowMissing: true, readBody: true, cancellationToken);
        if (document is null) return null;
        return ParseIssue(document.RootElement, repository, issue.Number, allowPullRequest: true);
    }

    private Task<JsonDocument?> SendAsync(RepositoryContext repository, int number, string suffix,
        HttpMethod method, object? body, bool allowMissing, bool readBody, CancellationToken cancellationToken) =>
        SendAsync(method,
            $"repos/{repository.Repository}/issues/{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}{suffix}",
            body is null ? null : System.Net.Http.Json.JsonContent.Create(body), allowMissing, readBody, cancellationToken);

    private async Task<JsonDocument?> SendAsync(HttpMethod method, string path, HttpContent? content,
        bool allowMissing, bool readBody, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string token;
        try { token = await _getToken(cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new GitHubIssueException(GitHubIssueFailure.Authorization, "GitHub credential acquisition failed."); }
        if (string.IsNullOrWhiteSpace(token) || token.Length > 16_384 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
            throw new GitHubIssueException(GitHubIssueFailure.Authorization, "GitHub credentials are missing or invalid.");

        using var request = new HttpRequestMessage(method, $"https://api.github.com/{path}");
        request.Content = content;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("WorkExecutionToolbox/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (allowMissing && response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.Forbidden &&
                (response.Headers.Contains("Retry-After") || response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0")))
                throw new GitHubIssueException(GitHubIssueFailure.RateLimited, "GitHub rate limit reached; wait before retrying.");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new GitHubIssueException(GitHubIssueFailure.Authorization, "GitHub authorization failed for the selected repository.");
            if (!response.IsSuccessStatusCode)
                throw new GitHubIssueException(GitHubIssueFailure.Provider, "GitHub Issue request failed.");

            if (!readBody) return null;

            // Bound even successful payloads; neither malformed bodies nor transport errors enter diagnostics.
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) != 0)
            {
                if (buffer.Length + read > 1_048_576) throw InvalidResponse();
                buffer.Write(chunk, 0, read);
            }
            return JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
        }
        catch (OperationCanceledException) { throw; }
        catch (GitHubIssueException) { throw; }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw InvalidResponse(); }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        { throw new GitHubIssueException(GitHubIssueFailure.Transport, "GitHub Issue transport failed."); }
    }

    private static ResolvedIssue? ParseIssue(JsonElement root, RepositoryContext repository,
        int? expectedNumber = null, bool allowPullRequest = false)
    {
        try
        {
            if (root.TryGetProperty("pull_request", out _))
            {
                if (allowPullRequest) return null;
                throw InvalidResponse();
            }
            var number = root.GetProperty("number").GetInt32();
            var id = root.GetProperty("id").GetInt64();
            var title = root.GetProperty("title").GetString();
            var state = root.GetProperty("state").GetString();
            var urlText = root.GetProperty("html_url").GetString();
            if (number <= 0 || expectedNumber is { } expected && number != expected || id <= 0 ||
                title is null || state is not ("open" or "closed") ||
                !Uri.TryCreate(urlText, UriKind.Absolute, out var url) ||
                !url.AbsoluteUri.Equals($"https://github.com/{repository.Repository}/issues/{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                    StringComparison.OrdinalIgnoreCase))
                throw InvalidResponse();
            return new(id, new(new(repository, number), title, state == "open" ? IssueState.Open : IssueState.Closed, url));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw InvalidResponse(); }
    }

    private static GitHubIssueException InvalidResponse() =>
        new(GitHubIssueFailure.InvalidResponse, "GitHub returned an invalid repository Issue response.");

    internal sealed record ResolvedIssue(long DatabaseId, IssueSummary Summary);
}
