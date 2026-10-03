namespace CodexServer;

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;

public sealed record GitHubIssueIdentity(string NodeId, long DatabaseId);

public sealed record GitHubIssueSnapshot(GitHubIssueIdentity Identity, ManagedGitHubIssue Issue,
    GitHubIssueRelationships Relationships, IReadOnlyDictionary<int, GitHubIssueIdentity> Identities);

/// <summary>Raw transport seam. A batch returns metadata and relationships together.</summary>
public interface IGitHubIssueSnapshotSource : IServerGitHubReadService
{
    Task<IReadOnlyList<int>> ListIssueNumbersAsync(CentralProject project, GitHubIssueQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<GitHubIssueSnapshot>> ReadSnapshotsAsync(CentralProject project,
        IReadOnlyDictionary<int, string?> identities, CancellationToken cancellationToken);
}

/// <summary>Repository-scoped durable identities and snapshots; operation scopes also deduplicate forced refreshes.</summary>
public sealed class GitHubIssueReadProvider(IGitHubIssueSnapshotSource source, string databasePath,
    TimeProvider? timeProvider = null) : IServerGitHubReadService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncLocal<Operation?> _operation = new();

    public IDisposable BeginOperation(bool refresh = false)
    {
        var previous = _operation.Value;
        _operation.Value = new Operation(refresh);
        return new Scope(() => _operation.Value = previous);
    }

    public IAsyncDisposable BeginMutationOperation()
    {
        var scope = BeginOperation(refresh: true);
        var operation = _operation.Value ?? throw new InvalidOperationException("Read operation is missing.");
        return new FreshScope(this, operation, scope);
    }

    public async Task<long> GetDatabaseIdentityAsync(CentralProject project, int issueNumber, CancellationToken cancellationToken)
    {
        // Identity-only access never expires or observes refresh policy.
        if (GitHubRepositoryValidation.Error(project) is { } error) throw new InvalidDataException(error);
        if (issueNumber <= 0) throw new InvalidDataException("Issue number must be positive.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
            await connection.OpenAsync(cancellationToken);
            await using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='github_issue_reads'";
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 0)
            {
                await using var read = connection.CreateCommand();
                read.CommandText = "SELECT database_id FROM github_issue_reads WHERE repository=$repository AND number=$number";
                read.Parameters.AddWithValue("$repository", project.Repository.ToLowerInvariant());
                read.Parameters.AddWithValue("$number", issueNumber);
                if (await read.ExecuteScalarAsync(cancellationToken) is long identity)
                {
                    if (identity <= 0) throw new InvalidDataException("Cached GitHub Issue identity was invalid.");
                    return identity;
                }
            }
        }
        finally { _gate.Release(); }
        var snapshot = (await GetSnapshotsAsync(project, [issueNumber], cancellationToken)).GetValueOrDefault(issueNumber)
            ?? throw new GitHubIssueNotFoundException(project.Repository, issueNumber);
        return snapshot.Identity.DatabaseId;
    }

    private async Task InvalidateAsync(Operation operation)
    {
        if (operation.Snapshots.IsEmpty) return;
        // Writes may have completed even when the caller was canceled; only Issue data is invalidated.
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None))
            throw new IOException("GitHub Issue cache invalidation timed out.");
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
            await connection.OpenAsync(CancellationToken.None);
            foreach (var repository in operation.Snapshots.Keys.Select(key => key.Repository).Distinct())
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE github_issue_reads SET snapshot=NULL,fetched_at=NULL WHERE repository=$repository";
                command.Parameters.AddWithValue("$repository", repository);
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
        finally { _gate.Release(); }
    }

    public Task<GitHubRepositoryAccess> CheckAccessAsync(CentralProject project, CancellationToken cancellationToken = default) =>
        source.CheckAccessAsync(project, cancellationToken);

    public async Task<ManagedGitHubIssue?> GetIssueAsync(CentralProject project, int issueNumber, CancellationToken cancellationToken = default) =>
        (await GetSnapshotsAsync(project, [issueNumber], cancellationToken)).GetValueOrDefault(issueNumber)?.Issue;

    public async Task<GitHubIssueRelationships?> GetIssueRelationshipsAsync(CentralProject project, int issueNumber,
        CancellationToken cancellationToken = default) =>
        (await GetSnapshotsAsync(project, [issueNumber], cancellationToken)).GetValueOrDefault(issueNumber)?.Relationships;

    public async Task<IReadOnlyList<ManagedGitHubIssue>> ListIssuesAsync(CentralProject project, GitHubIssueQuery query,
        CancellationToken cancellationToken = default)
    {
        var numbers = await source.ListIssueNumbersAsync(project, query, cancellationToken);
        var snapshots = await GetSnapshotsAsync(project, numbers, cancellationToken);
        return numbers.Where(snapshots.ContainsKey).Select(number => snapshots[number].Issue).ToArray();
    }

    public async Task PrefetchAsync(CentralProject project, IReadOnlyList<int> numbers, CancellationToken cancellationToken) =>
        _ = await GetSnapshotsAsync(project, numbers, cancellationToken);

    private async Task<Dictionary<int, GitHubIssueSnapshot>> GetSnapshotsAsync(CentralProject project,
        IReadOnlyList<int> numbers, CancellationToken cancellationToken)
    {
        if (GitHubRepositoryValidation.Error(project) is { } error) throw new InvalidDataException(error);
        if (numbers.Any(number => number <= 0)) throw new InvalidDataException("Issue number must be positive.");
        // Eligibility is project policy, so only transport snapshots are reused across project aliases.
        var repository = project.Repository.ToLowerInvariant();
        var operation = _operation.Value;
        var result = new Dictionary<int, GitHubIssueSnapshot>();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
            await connection.OpenAsync(cancellationToken);
            await using (var schema = connection.CreateCommand())
            {
                schema.CommandText = """
                    CREATE TABLE IF NOT EXISTS github_issue_reads (
                      repository TEXT NOT NULL, number INTEGER NOT NULL, node_id TEXT NOT NULL, database_id INTEGER NOT NULL,
                      fetched_at TEXT, snapshot TEXT, PRIMARY KEY(repository, number));
                    """;
                await schema.ExecuteNonQueryAsync(cancellationToken);
            }
            var missing = new Dictionary<int, string?>();
            foreach (var number in numbers.Distinct())
            {
                var key = (repository, number);
                if (operation is not null && operation.Snapshots.TryGetValue(key, out var reused))
                {
                    if (reused is not null) result[number] = reused;
                    continue;
                }
                await using var read = connection.CreateCommand();
                read.CommandText = "SELECT node_id, fetched_at, snapshot FROM github_issue_reads WHERE repository=$repository AND number=$number";
                read.Parameters.AddWithValue("$repository", repository);
                read.Parameters.AddWithValue("$number", number);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                string? identity = null;
                if (await reader.ReadAsync(cancellationToken))
                {
                    identity = reader.GetString(0);
                    if (operation?.Refresh != true && !reader.IsDBNull(2))
                    {
                        var cached = JsonSerializer.Deserialize<GitHubIssueSnapshot>(reader.GetString(2))
                            ?? throw new InvalidDataException("GitHub snapshot cache was invalid.");
                        var age = _clock.GetUtcNow() - DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture);
                        var historical = cached.Issue.State.Equals("closed", StringComparison.OrdinalIgnoreCase) &&
                            cached.Issue.Labels.Contains("codex-done", StringComparer.OrdinalIgnoreCase);
                        if (age >= TimeSpan.Zero && age < (historical ? TimeSpan.FromDays(30) : TimeSpan.FromSeconds(30)))
                        {
                            result[number] = cached;
                            if (operation is not null) operation.Snapshots[key] = cached;
                            continue;
                        }
                    }
                }
                missing[number] = identity;
            }
            foreach (var batch in missing.Chunk(10))
            {
                var batchIdentities = batch.ToDictionary();
                // Earlier batches can discover identities through relationship references.
                foreach (var number in batchIdentities.Where(pair => pair.Value is null).Select(pair => pair.Key).ToArray())
                {
                    await using var identityRead = connection.CreateCommand();
                    identityRead.CommandText = "SELECT node_id FROM github_issue_reads WHERE repository=$repository AND number=$number";
                    identityRead.Parameters.AddWithValue("$repository", repository);
                    identityRead.Parameters.AddWithValue("$number", number);
                    if (await identityRead.ExecuteScalarAsync(cancellationToken) is string identity)
                        batchIdentities[number] = identity;
                }
                var fetched = await source.ReadSnapshotsAsync(project, batchIdentities, cancellationToken);
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                foreach (var snapshot in fetched)
                {
                    var number = snapshot.Issue.Number;
                    if (!batch.Any(item => item.Key == number) || snapshot.Relationships.IssueNumber != number ||
                        !snapshot.Relationships.Repository.Equals(project.Repository, StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(snapshot.Identity.NodeId) || snapshot.Identity.DatabaseId <= 0)
                        throw new InvalidDataException("GitHub snapshot did not match the requested repository Issue.");
                    foreach (var identity in snapshot.Identities.Append(new(number, snapshot.Identity)))
                    {
                        if (identity.Key <= 0 || identity.Value.DatabaseId <= 0 || string.IsNullOrWhiteSpace(identity.Value.NodeId))
                            throw new InvalidDataException("GitHub returned an invalid Issue identity.");
                        await using var writeIdentity = connection.CreateCommand();
                        writeIdentity.Transaction = transaction;
                        writeIdentity.CommandText = "INSERT INTO github_issue_reads(repository,number,node_id,database_id) VALUES($repository,$number,$id,$databaseId) ON CONFLICT(repository,number) DO NOTHING";
                        writeIdentity.Parameters.AddWithValue("$repository", repository);
                        writeIdentity.Parameters.AddWithValue("$number", identity.Key);
                        writeIdentity.Parameters.AddWithValue("$id", identity.Value.NodeId);
                        writeIdentity.Parameters.AddWithValue("$databaseId", identity.Value.DatabaseId);
                        await writeIdentity.ExecuteNonQueryAsync(cancellationToken);
                    }
                    await using var write = connection.CreateCommand();
                    write.Transaction = transaction;
                    write.CommandText = "UPDATE github_issue_reads SET fetched_at=$time,snapshot=$snapshot WHERE repository=$repository AND number=$number";
                    write.Parameters.AddWithValue("$repository", repository);
                    write.Parameters.AddWithValue("$number", number);
                    write.Parameters.AddWithValue("$time", _clock.GetUtcNow().ToString("O"));
                    var cachedSnapshot = snapshot with { Issue = snapshot.Issue with { IsEligible = false, EligibilityReasons = [] } };
                    write.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(cachedSnapshot));
                    await write.ExecuteNonQueryAsync(cancellationToken);
                    result[number] = cachedSnapshot;
                }
                await transaction.CommitAsync(cancellationToken);
                if (operation is not null)
                    foreach (var item in batch) operation.Snapshots[(repository, item.Key)] = result.GetValueOrDefault(item.Key);
            }
        }
        finally { _gate.Release(); }
        // Never persist eligibility decisions: labels/configuration may differ between aliases.
        return result.ToDictionary(pair => pair.Key, pair => pair.Value with
        {
            Issue = ManagedGitHubIssueEligibility.Evaluate(project, pair.Value.Issue)
        });
    }

    private sealed class Operation(bool refresh)
    {
        public bool Refresh { get; } = refresh;
        public ConcurrentDictionary<(string Repository, int Number), GitHubIssueSnapshot?> Snapshots { get; } = new();
    }

    private sealed class FreshScope(GitHubIssueReadProvider provider, Operation operation, IDisposable scope) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            // Restore the ambient scope synchronously in the caller's execution context.
            scope.Dispose();
            return new ValueTask(provider.InvalidateAsync(operation));
        }
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
