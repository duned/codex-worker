using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WorkExecutionToolbox;

/// <summary>Optional durable read cache. Hosts choose its location independently of Server/Worker state.</summary>
public sealed record GitHubIssueCacheOptions
{
    public string? DirectoryPath { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wet", "github-issue-cache");
}

/// <summary>A command scope deduplicates reads, including explicit refreshes. Dispose in the calling context.</summary>
public interface ICacheAwareIssueProvider
{
    IDisposable BeginReadOperation(bool refresh = false);
}

// Only validated, minimal Issue data is serialized: no HTTP bodies, tokens, labels or CLI state.
internal sealed class GitHubIssueCache(GitHubIssueCacheOptions options)
{
    internal sealed record Entry(int Version, string Repository, int Number, string Kind,
        DateTimeOffset FetchedAt, GitHubIssueProvider.ResolvedIssue[] Issues, int Pages = 0, DateTimeOffset? SnapshotAt = null);
    private sealed record Identity(int Version, string Repository, int Number, long DatabaseId);

    public DateTimeOffset Now => options.TimeProvider.GetUtcNow();
    public static TimeSpan Lifetime(IssueState state) => state == IssueState.Closed
        ? TimeSpan.FromDays(7) : TimeSpan.FromSeconds(30);

    private string? RepositoryDirectory(IssueReference issue) => options.DirectoryPath is { } directory
        ? Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            issue.Repository.Repository.ToLowerInvariant())))) : null;

    private async Task<string> GenerationAsync(string directory, CancellationToken token)
    {
        var path = Path.Combine(directory, "generation");
        if (!File.Exists(path) || new FileInfo(path).Length != 32) return "invalid";
        var generation = await File.ReadAllTextAsync(path, token);
        return Guid.TryParseExact(generation, "N", out _) ? generation : "invalid";
    }

    public async Task<Entry?> ReadAsync(IssueReference issue, string kind, CancellationToken token, bool allowStaleListing = false)
    {
        if (RepositoryDirectory(issue) is not { } directory) return null;
        try
        {
            var generation = await GenerationAsync(directory, token);
            if (generation == "invalid") return null;
            var entry = await ReadFileAsync<Entry>(Path.Combine(directory, generation, $"{issue.Number}-{kind}.json"), token);
            var listing = kind is "repository-all" or "repository-open" or "repository-closed";
            if (entry is null || entry.Version != 1 || entry.Number != issue.Number || entry.Kind != kind ||
                !string.Equals(entry.Repository, issue.Repository.Repository, StringComparison.OrdinalIgnoreCase) || entry.Issues is null ||
                entry.Issues.Length > 10_000 || entry.Pages < 0 || entry.Pages > 100 ||
                entry.Issues.Any(item => !Valid(item, issue.Repository)) ||
                entry.Issues.Select(item => item.Summary.Issue.Number).Distinct().Count() != entry.Issues.Length ||
                entry.Issues.Select(item => item.DatabaseId).Distinct().Count() != entry.Issues.Length ||
                kind == "issue" && (entry.Issues.Length != 1 || entry.Issues[0].Summary.Issue.Number != issue.Number) ||
                kind != "issue" && (entry.Pages == 0 || !listing && entry.Issues.Any(item => item.Summary.Issue.Number == issue.Number)) ||
                kind == "parent" && entry.Issues.Length > 1) return null;
            var age = Now - entry.FetchedAt;
            if (listing)
            {
                var snapshotAge = Now - (entry.SnapshotAt ?? entry.FetchedAt);
                if (snapshotAge < TimeSpan.Zero || snapshotAge >= TimeSpan.FromDays(7) ||
                    entry.SnapshotAt > entry.FetchedAt ||
                    kind == "repository-open" && entry.Issues.Any(item => item.Summary.State != IssueState.Open) ||
                    kind == "repository-closed" && entry.Issues.Any(item => item.Summary.State != IssueState.Closed)) return null;
                // Membership can change even in a closed-only query (newly closed Issues).
                return age >= TimeSpan.Zero && (allowStaleListing || age < Lifetime(IssueState.Open)) ? entry : null;
            }
            var state = kind == "issue" && entry.Issues.Length == 1 ? entry.Issues[0].Summary.State : IssueState.Open;
            // Relation lifetime is recorded by its owner's summary, never by a closed related Issue.
            if (kind != "issue")
            {
                var owner = await ReadAsync(issue, "issue", token);
                if (owner?.Issues.Length == 1) state = owner.Issues[0].Summary.State;
            }
            return age >= TimeSpan.Zero && age < Lifetime(state) ? entry : null;
        }
        catch (Exception ex) when (IsCacheFailure(ex)) { return null; }
    }

    internal static bool Valid(GitHubIssueProvider.ResolvedIssue? item, RepositoryContext repository) =>
        item is { DatabaseId: > 0, Summary: { Title: not null, Issue: { Number: > 0 } issue } summary } &&
        issue.Repository is { } scope && string.Equals(scope.Repository, repository.Repository, StringComparison.OrdinalIgnoreCase) &&
        summary.State is IssueState.Open or IssueState.Closed && summary.Url is { IsAbsoluteUri: true } url &&
        url.AbsoluteUri.Equals($"https://github.com/{repository.Repository}/issues/{issue.Number}", StringComparison.OrdinalIgnoreCase);

    public async Task<long?> ReadIdentityAsync(IssueReference issue, CancellationToken token)
    {
        if (RepositoryDirectory(issue) is not { } directory) return null;
        try
        {
            var identity = await ReadFileAsync<Identity>(Path.Combine(directory, $"{issue.Number}-identity.json"), token);
            return identity is { Version: 1, DatabaseId: > 0 } && identity.Number == issue.Number &&
                string.Equals(identity.Repository, issue.Repository.Repository, StringComparison.OrdinalIgnoreCase) ? identity.DatabaseId : null;
        }
        catch (Exception ex) when (IsCacheFailure(ex)) { return null; }
    }

    public async Task RememberIdentityAsync(GitHubIssueProvider.ResolvedIssue item, CancellationToken token)
    {
        var issue = item.Summary.Issue;
        if (RepositoryDirectory(issue) is not { } directory) return;
        var known = await ReadIdentityAsync(issue, token);
        if (known is { } id && id != item.DatabaseId)
            throw new GitHubIssueException(GitHubIssueFailure.InvalidResponse, "GitHub returned an inconsistent Issue identity.");
        if (known is not null) return;
        await TryWriteAsync(Path.Combine(directory, $"{issue.Number}-identity.json"),
            new Identity(1, issue.Repository.Repository, issue.Number, item.DatabaseId), token);
    }

    // Capture the generation BEFORE a network read so concurrent mutations cannot publish an old
    // response into the new generation. Obsolete generations remain unreachable.
    public async Task<string?> CaptureAsync(IssueReference issue, CancellationToken token)
    {
        if (RepositoryDirectory(issue) is not { } directory) return null;
        try
        {
            var generation = await GenerationAsync(directory, token);
            if (generation == "invalid")
            {
                generation = Guid.NewGuid().ToString("N");
                await WriteAsync(Path.Combine(directory, "generation"), generation, token, raw: true);
            }
            return Path.Combine(directory, generation);
        }
        catch (Exception ex) when (IsCacheFailure(ex)) { return null; }
    }

    public Task StoreAsync(string? generationDirectory, Entry entry, CancellationToken token) =>
        generationDirectory is null ? Task.CompletedTask : TryWriteAsync(
            Path.Combine(generationDirectory, $"{entry.Number}-{entry.Kind}.json"), entry, token);

    public async Task InvalidateAsync(IssueReference issue)
    {
        if (RepositoryDirectory(issue) is not { } directory) return;
        try
        {
            // Durable invalidation must finish even when a mutation was cancelled after being sent.
            await WriteAsync(Path.Combine(directory, "generation"), Guid.NewGuid().ToString("N"), CancellationToken.None, raw: true);
        }
        catch (Exception ex) when (IsCacheFailure(ex))
        { throw new GitHubIssueException(GitHubIssueFailure.Provider, "GitHub Issue cache invalidation failed; refresh before retrying."); }
    }

    private static async Task<T?> ReadFileAsync<T>(string path, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 4_194_304) return default;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: token);
    }

    private static async Task TryWriteAsync<T>(string path, T value, CancellationToken token)
    {
        try { await WriteAsync(path, value, token); }
        catch (Exception ex) when (IsCacheFailure(ex)) { /* Cache availability is not GitHub availability. */ }
    }

    private static async Task WriteAsync<T>(string path, T value, CancellationToken token, bool raw = false)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Missing cache directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, raw ? value?.ToString() : JsonSerializer.Serialize(value), token);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool IsCacheFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or
        JsonException or ArgumentException or NotSupportedException;
}
