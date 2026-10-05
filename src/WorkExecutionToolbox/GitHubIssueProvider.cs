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
public sealed partial class GitHubIssueProvider : IIssueRelationshipProvider, IIssueGraphProvider, ICacheAwareIssueProvider
{
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _getToken;
    private readonly GitHubIssueCache _cache;
    private readonly AsyncLocal<ReadOperation?> _operation = new();

    public GitHubIssueProvider(HttpClient http, Func<CancellationToken, Task<string>> getToken,
        GitHubIssueCacheOptions? cacheOptions = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(getToken);
        _http = http;
        _getToken = getToken;
        cacheOptions ??= new();
        ArgumentNullException.ThrowIfNull(cacheOptions.TimeProvider);
        if (cacheOptions.DirectoryPath is { } directory) ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _cache = new(cacheOptions);
    }

    public IDisposable BeginReadOperation(bool refresh = false)
    {
        var previous = _operation.Value;
        _operation.Value = new(refresh, previous);
        return new ReadScope(() => _operation.Value = previous);
    }

    private IDisposable EnsureReadOperation() => _operation.Value is null
        ? BeginReadOperation() : new ReadScope(() => { });

    public async Task<IssueSummary?> GetIssueAsync(IssueReference issue, CancellationToken cancellationToken = default)
    {
        using var operation = EnsureReadOperation();
        return (await ResolveAsync(issue, cancellationToken))?.Summary;
    }

    // Database IDs remain inside the provider boundary for subsequent relationship operations.
    internal async Task<ResolvedIssue?> ResolveAsync(IssueReference issue, CancellationToken cancellationToken, Action? beforeRequest = null)
    {
        ArgumentNullException.ThrowIfNull(issue);
        var repository = GitHubRepositoryContext.Create(issue.Repository.Repository);
        var entry = await CachedReadAsync(issue, "issue", async () =>
        {
            beforeRequest?.Invoke();
            using var document = await SendAsync(HttpMethod.Get,
                $"repos/{repository.Repository}/issues/{issue.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                null, allowMissing: true, readBody: true, cancellationToken);
            var resolved = document is null ? null : ParseIssue(document.RootElement, repository, issue.Number, allowPullRequest: true);
            return (resolved is null ? Array.Empty<ResolvedIssue>() : new[] { resolved }, 0);
        }, cancellationToken);
        return entry.Issues.SingleOrDefault();
    }

    private async Task<GitHubIssueCache.Entry> CachedReadAsync(IssueReference issue, string kind,
        Func<Task<(ResolvedIssue[] Issues, int Pages)>> read, CancellationToken token,
        Func<DateTimeOffset?>? snapshotAt = null, Func<DateTimeOffset?>? fetchedAt = null)
    {
        token.ThrowIfCancellationRequested();
        var operation = _operation.Value;
        var key = (issue.Repository.Repository.ToLowerInvariant(), issue.Number, kind);
        var result = operation is not null
            ? await operation.Reads.GetOrAdd(key, _ => new Lazy<Task<GitHubIssueCache.Entry>>(FetchAsync)).Value
            : await FetchAsync();
        token.ThrowIfCancellationRequested();
        return result;

        async Task<GitHubIssueCache.Entry> FetchAsync()
        {
            if (operation?.Refresh != true && await _cache.ReadAsync(issue, kind, token) is { } cached)
                return cached;
            var generation = await _cache.CaptureAsync(issue, token);
            var (issues, pages) = await read();
            foreach (var item in issues) await _cache.RememberIdentityAsync(item, token);
            var entry = new GitHubIssueCache.Entry(1, issue.Repository.Repository, issue.Number, kind, fetchedAt?.Invoke() ?? _cache.Now, issues, pages, snapshotAt?.Invoke());
            // Missing or invisible Issues are not negative-cached across operations.
            if (kind != "issue" || issues.Length > 0) await _cache.StoreAsync(generation, entry, token);
            return entry;
        }
    }

    private async Task InvalidateCacheAsync(IssueReference issue)
    {
        for (var operation = _operation.Value; operation is not null; operation = operation.Parent)
            operation.Reads.Clear();
        await _cache.InvalidateAsync(issue);
    }

    private sealed class ReadOperation(bool refresh, ReadOperation? parent)
    {
        public bool Refresh { get; } = refresh;
        public ReadOperation? Parent { get; } = parent;
        public System.Collections.Concurrent.ConcurrentDictionary<(string Repository, int Number, string Kind),
            Lazy<Task<GitHubIssueCache.Entry>>> Reads { get; } = new();
    }

    private sealed class ReadScope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private Task<JsonDocument?> SendAsync(RepositoryContext repository, int number, string suffix,
        HttpMethod method, object? body, bool allowMissing, bool readBody, CancellationToken cancellationToken)
    {
        var path = $"repos/{repository.Repository}/issues/{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}{suffix}";
        var content = body is null ? null : System.Net.Http.Json.JsonContent.Create(body);
        return method == HttpMethod.Get
            ? SendAsync(method, path, content, allowMissing, readBody, cancellationToken)
            : SendMutationAsync(new(repository, number), method, path, content, cancellationToken);
    }

    private async Task<JsonDocument?> SendMutationAsync(IssueReference issue, HttpMethod method, string path,
        HttpContent? content, CancellationToken cancellationToken)
    {
        await InvalidateCacheAsync(issue);
        try
        {
            return await SendAsync(method, path, content, allowMissing: false, readBody: false, cancellationToken);
        }
        finally { await InvalidateCacheAsync(issue); }
    }

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
