using System.Globalization;
using System.Text.Json;

namespace CodexWorker;

public sealed record LegacyWorkspaceEvidence(string Directory, string Head);
internal sealed record LegacySessionMatch(string? SessionId, string? Rejection);

/// <summary>Compatibility boundary for Codex rollout session_meta v1. Reads only the
/// bounded first record, never conversation text, authentication storage or arbitrary files.</summary>
internal sealed class LegacyCodexSessionStore(string codexHome)
{
    internal async Task<LegacySessionMatch> FindAsync(ExecutionHistoryEntry source, LegacyWorkspaceEvidence workspace, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (source.CompletedAtUtc is not { } end || end < source.StartedAtUtc || end - source.StartedAtUtc > TimeSpan.FromDays(31))
            return new(null, "execution time window is missing or unsupported");
        var sessions = Path.Combine(codexHome, "sessions");
        var matches = new HashSet<string>(StringComparer.Ordinal);
        var examined = 0;
        try
        {
            for (var date = source.StartedAtUtc.UtcDateTime.Date; date <= end.UtcDateTime.Date; date = date.AddDays(1))
            {
                ct.ThrowIfCancellationRequested();
                var directory = sessions;
                foreach (var segment in date.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture).Split('/'))
                {
                    if (Directory.Exists(directory) && IsLink(directory)) return new(null, "session storage contains an unsupported link");
                    directory = Path.Combine(directory, segment);
                }
                if (!Directory.Exists(directory)) continue;
                if (IsLink(directory)) return new(null, "session storage contains an unsupported link");
                foreach (var file in Directory.EnumerateFiles(directory, "rollout-*.jsonl"))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++examined > 10000) return new(null, "session discovery limit exceeded");
                    if (IsLink(file)) return new(null, "session storage contains an unsupported link");
                    if (new FileInfo(file).Length == 0) return new(null, "malformed or partial session metadata");
                    var header = await ReadHeaderAsync(file, ct);
                    if (header is null) return new(null, "malformed or unsupported session metadata");
                    using var document = JsonDocument.Parse(header);
                    var root = document.RootElement;
                    if (HasDuplicateKeys(root)) return new(null, "malformed or unsupported session metadata");
                    if (Text(root, "type") != "session_meta" || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                        return new(null, "malformed or unsupported session metadata");
                    // Structured source must be exactly exec, excluding Guardian/reviewer
                    // source objects and all parent-bearing sessions, even at the same CWD.
                    if (Text(payload, "source") != "exec" || Text(payload, "originator") != "codex_exec" ||
                        payload.TryGetProperty("parent_thread_id", out var parent) && parent.ValueKind != JsonValueKind.Null) continue;
                    if (Text(payload, "cwd") != workspace.Directory) continue;
                    if (!Guid.TryParse(Text(payload, "id"), out var id) || id == Guid.Empty ||
                        !DateTimeOffset.TryParse(Text(payload, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp) ||
                        !payload.TryGetProperty("git", out var git) || git.ValueKind != JsonValueKind.Object)
                        return new(null, "malformed or partial root session metadata");
                    if (timestamp < source.StartedAtUtc || timestamp > end) continue;
                    if (Text(git, "branch") != source.FeatureBranch || Text(git, "commit_hash") != workspace.Head) continue;
                    if (git.TryGetProperty("repository_url", out var repositoryUrl) &&
                        (repositoryUrl.ValueKind != JsonValueKind.String ||
                         !GitRepository.OriginMatchesRepository(repositoryUrl.GetString() ?? "", source.Repository))) continue;
                    matches.Add(id.ToString());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Exceptions can contain paths or JSON content. Only emit fixed diagnostics.
            return new(null, "session metadata is unreadable or malformed");
        }
        return matches.Count switch
        {
            1 => new(matches.Single(), null),
            0 => new(null, "no root Codex session matches preserved worktree and branch/base metadata"),
            _ => new(null, "multiple root Codex sessions match preserved execution")
        };
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool HasDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!keys.Add(property.Name) || HasDuplicateKeys(property.Value)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var value in element.EnumerateArray())
                if (HasDuplicateKeys(value)) return true;
        return false;
    }
    private static string? Text(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task<string?> ReadHeaderAsync(string file, CancellationToken ct)
    {
        using var reader = new StreamReader(file);
        var buffer = new char[65536];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(length, Math.Min(1024, buffer.Length - length)), ct);
            if (read == 0) return length == 0 ? null : new string(buffer, 0, length);
            var newline = Array.IndexOf(buffer, '\n', length, read);
            if (newline >= 0) return new string(buffer, 0, newline);
            length += read;
        }
        return null;
    }
}
