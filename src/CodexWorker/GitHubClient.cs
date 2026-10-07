using System.Text.Json;

namespace CodexWorker;

public sealed record GitHubIssue(int Number, string Title, string Body, DateTimeOffset CreatedAt,
    IReadOnlyList<string>? Labels = null, string CommentContext = "");
public sealed record GitHubIssueState(bool IsOpen, IReadOnlyList<string> Labels);
public sealed record RequiredGitHubLabel(string Name, string Color, string Description);

public sealed class GitHubClient : IGitHubClient, IGitHubLabelClient
{
    private readonly string repository;
    private readonly GitHubRetryPolicy retry;
    private readonly Func<IEnumerable<string>, CancellationToken, Task<ProcessResult>> runCommand;
    private readonly CodexProvisioning.GeneratedMessageOrigin origin;

    public GitHubClient(ProcessRunner runner, string repository, int timeoutSeconds, bool requireManagedAuthentication = false)
        : this(repository, async (arguments, ct) => await runner.RunAsync("gh", arguments,
            Environment.CurrentDirectory, TimeSpan.FromSeconds(timeoutSeconds), ct,
            environment: await CodexProvisioning.NodeGitHubSetup.GitHubEnvironmentAsync(ct,
                requireManagedAuthentication: requireManagedAuthentication))) { }

    internal GitHubClient(string repository,
        Func<IEnumerable<string>, CancellationToken, Task<ProcessResult>> runCommand,
        CodexProvisioning.GeneratedMessageOrigin? origin = null, GitHubRetryPolicy? retry = null)
    {
        this.repository = repository;
        this.retry = retry ?? new GitHubRetryPolicy();
        this.runCommand = runCommand;
        this.origin = origin ?? new(CodexProvisioning.CodexComponent.Worker, WorkerIdentity.DisplayName);
    }

    public Task<GitHubIssue?> FindOldestReadyAsync(string label, CancellationToken cancellationToken) =>
        FindOldestReadyAsync(label, new HashSet<int>(), cancellationToken);

    public async Task<GitHubIssue?> FindOldestReadyAsync(string label, IReadOnlySet<int> excludedIssueNumbers,
        CancellationToken cancellationToken)
    {
        var result = await RunGhAsync(["issue", "list", "--repo", repository, "--state", "open", "--label", label,
            "--search", "sort:created-asc", "--limit", "1000", "--json", "number,title,body,createdAt,labels"], cancellationToken,
            allowGracefulCancellation: true);
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var candidates = document.RootElement.EnumerateArray()
                .Select(e => new GitHubIssue(e.GetProperty("number").GetInt32(), e.GetProperty("title").GetString() ?? "",
                    e.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                    e.GetProperty("createdAt").GetDateTimeOffset(),
                    e.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
                        ? labels.EnumerateArray().Select(item => item.GetProperty("name").GetString() ?? "").ToArray()
                        : []))
                .OrderBy(candidate => candidate.CreatedAt);

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (excludedIssueNumbers.Contains(candidate.Number)) continue;
                using var dependencies = await GetBlockingDependenciesAsync(candidate.Number, cancellationToken);
                if (DependenciesAreClosed(dependencies)) return candidate;
            }
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw ReadFailure("issue list", null,
                $"Could not reliably read ready Issues for repository '{repository}': {ex.Message}", ex);
        }
    }

    public Task<GitHubIssue?> GetIssueAsync(int issueNumber, CancellationToken cancellationToken) =>
        ReadIssueAsync(issueNumber, requireOpen: true, cancellationToken);

    public Task<GitHubIssue?> GetCompletionIssueAsync(int issueNumber, CancellationToken ct) =>
        ReadIssueAsync(issueNumber, requireOpen: false, ct);

    private async Task<GitHubIssue?> ReadIssueAsync(int issueNumber, bool requireOpen, CancellationToken cancellationToken)
    {
        if (issueNumber <= 0) throw new WorkerInfrastructureException("Assigned GitHub Issue number must be positive.");
        var result = await RunGhAsync(["issue", "view", issueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--repo", repository, "--json", "number,title,body,createdAt,state"], cancellationToken, allowGracefulCancellation: true);
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var issue = document.RootElement;
            if (issue.GetProperty("number").GetInt32() != issueNumber)
                throw new InvalidDataException("Issue response identity differs from the requested Issue.");
            if (requireOpen && !string.Equals(issue.GetProperty("state").GetString(), "OPEN", StringComparison.OrdinalIgnoreCase))
                throw new GitHubOperationException("issue view", issueNumber, false, GitHubFailureKind.DeterministicRequest,
                    GitHubRemoteState.NotApplicable, $"Assigned Issue #{issueNumber} in '{repository}' is not open.");
            return new GitHubIssue(issue.GetProperty("number").GetInt32(), issue.GetProperty("title").GetString() ?? "",
                issue.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "", issue.GetProperty("createdAt").GetDateTimeOffset());
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or InvalidDataException)
        {
            throw ReadFailure("issue view", issueNumber,
                $"Could not read assigned Issue #{issueNumber} in '{repository}': {ex.Message}", ex);
        }
    }

    public async Task<string> GetIssueCommentContextAsync(int issueNumber, CancellationToken cancellationToken,
        IReadOnlyList<string>? secretValues = null)
    {
        if (issueNumber <= 0) throw new WorkerInfrastructureException("GitHub Issue number must be positive.");
        var issuePath = $"repos/{repository}/issues/{issueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var countResult = await ReadCommentCommandAsync(["api", issuePath, "--jq", ".comments"], cancellationToken);
        if (!int.TryParse(countResult.StandardOutput.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var count) || count < 0)
            throw CommentReadFailure(issueNumber);
        if (count == 0) return "";

        // Project each small REST page before process capture. Inspect provenance before bounding bodies,
        // including markers beyond the body limit. Even JSON escaping fits the bounded process output.
        var projection = $$"""
            [.[] | (.body // "") as $body | {
              id, author: (.user.login // "[deleted]"), createdAt: .created_at,
              generated: ($body | split("\n") | map(rtrimstr("\r")) | any(. == "{{CodexProvisioning.GeneratedMessageOrigin.WorkerMarker}}" or . == "{{CodexProvisioning.GeneratedMessageOrigin.ServerMarker}}")),
              truncated: (($body | length) > {{IssueCommentContext.MaximumCommentCharacters}}),
              body: $body[:{{IssueCommentContext.MaximumCommentCharacters}}]
            }]
            """;
        var comments = new List<GitHubIssueComment>();
        var scanned = 0;
        var page = (count - 1) / IssueCommentContext.PageSize + 1;
        var unscanned = count;
        while (page > 0 && scanned < IssueCommentContext.MaximumScannedComments && comments.Count < IssueCommentContext.MaximumComments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ReadCommentCommandAsync(["api", $"{issuePath}/comments?per_page={IssueCommentContext.PageSize}&page={page}",
                "--jq", projection], cancellationToken);
            try
            {
                using var document = JsonDocument.Parse(result.StandardOutput);
                var items = document.RootElement;
                var expectedCount = unscanned - (page - 1) * IssueCommentContext.PageSize;
                // The newest page may have filled since the count read; use only the snapshot's prefix.
                if (items.GetArrayLength() < expectedCount || items.GetArrayLength() > IssueCommentContext.PageSize)
                    throw CommentReadFailure(issueNumber);
                var pageItems = items.EnumerateArray().Take(expectedCount)
                    .TakeLast(IssueCommentContext.MaximumScannedComments - scanned).ToArray();
                foreach (var item in pageItems)
                {
                    if (item.GetProperty("generated").GetBoolean()) continue;
                    var id = item.GetProperty("id").GetInt64();
                    var author = item.GetProperty("author").GetString();
                    var body = item.GetProperty("body").GetString();
                    var timestamp = item.GetProperty("createdAt").GetDateTimeOffset();
                    if (id <= 0 || string.IsNullOrWhiteSpace(author) || author.Length > 100 || author.Any(char.IsControl) || body is null)
                        throw CommentReadFailure(issueNumber);
                    comments.Add(new(id, author, timestamp, body, item.GetProperty("truncated").GetBoolean()));
                }
                scanned += pageItems.Length;
                unscanned -= pageItems.Length;
                page--;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                // Never attach malformed comment payloads or parse diagnostics to operational reports.
                throw CommentReadFailure(issueNumber);
            }
        }
        return IssueCommentContext.Build(comments, scanTruncated: unscanned > 0, secretValues: secretValues);
    }

    private static GitHubOperationException CommentReadFailure(int issueNumber) =>
        new("Issue comments read", issueNumber, false, GitHubFailureKind.Unknown, GitHubRemoteState.NotApplicable,
            $"Could not reliably read comment context for Issue #{issueNumber}; no incomplete or cached context will be used.");

    private async Task<ProcessResult> ReadCommentCommandAsync(IEnumerable<string> arguments, CancellationToken ct)
    {
        try { return await RunGhAsync(arguments, ct, allowGracefulCancellation: true); }
        catch (GitHubOperationException failure)
        {
            // Process timeouts retain captured stdout in their exception message. Keep typed transport
            // semantics, but never copy comment payloads into logs/reports through an exception chain.
            throw new GitHubOperationException("Issue comments read", failure.IssueNumber, false, failure.FailureKind,
                failure.RemoteState, $"GitHub comment context read failed for Issue #{failure.IssueNumber} ({failure.FailureKind}); no incomplete or cached context will be used.");
        }
    }

    public async Task<bool> IsIssueOpenAsync(int issueNumber, CancellationToken cancellationToken)
    {
        if (issueNumber <= 0) throw new WorkerInfrastructureException("GitHub Issue number must be positive.");
        var result = await RunGhAsync(["issue", "view", issueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--repo", repository, "--json", "state"], cancellationToken, allowGracefulCancellation: true);
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var state = document.RootElement.GetProperty("state").GetString();
            if (string.Equals(state, "OPEN", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(state, "CLOSED", StringComparison.OrdinalIgnoreCase)) return false;
            throw new InvalidDataException($"Unexpected GitHub Issue state '{state}'.");
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or InvalidDataException)
        {
            throw ReadFailure("issue view", issueNumber,
                $"Could not read state for GitHub Issue #{issueNumber} in '{repository}': {ex.Message}", ex);
        }
    }

    /// <summary>Reads authoritative scheduling state without making any Issue mutation.</summary>
    public async Task<GitHubIssueState> ReadIssueStateAsync(int issueNumber, CancellationToken cancellationToken)
    {
        if (issueNumber <= 0) throw new WorkerInfrastructureException("GitHub Issue number must be positive.");
        var result = await RunGhAsync(["issue", "view", issueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--repo", repository, "--json", "state,labels"], cancellationToken, allowGracefulCancellation: true);
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var issue = document.RootElement;
            var state = issue.GetProperty("state").GetString();
            if (!string.Equals(state, "OPEN", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(state, "CLOSED", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unexpected GitHub Issue state '{state}'.");
            var labels = issue.GetProperty("labels").EnumerateArray()
                .Select(label => label.GetProperty("name").GetString() ?? "").ToArray();
            return new GitHubIssueState(string.Equals(state, "OPEN", StringComparison.OrdinalIgnoreCase), labels);
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or InvalidDataException)
        {
            throw ReadFailure("issue view", issueNumber,
                $"Could not verify scheduling state for GitHub Issue #{issueNumber} in '{repository}': {ex.Message}", ex);
        }
    }

    private async Task<JsonDocument> GetBlockingDependenciesAsync(int issueNumber, CancellationToken ct)
    {
        ProcessResult result;
        try
        {
            result = await RunGhAsync(DependencyApiArguments(repository, issueNumber), ct,
                allowGracefulCancellation: true);
        }
        catch (WorkerInfrastructureException ex)
        {
            throw new WorkerInfrastructureException($"GitHub Issue Dependencies API unavailable for '{repository}' Issue #{issueNumber}: {ex.Message}", ex);
        }
        try { return JsonDocument.Parse(ParseApiPages(result.StandardOutput)); }
        catch (JsonException ex)
        {
            throw ReadFailure("Issue dependency API", issueNumber,
                $"Could not parse GitHub Issue dependencies for #{issueNumber} in '{repository}': {ex.Message}", ex);
        }
    }

    internal static string[] DependencyApiArguments(string repository, int issueNumber) =>
        ["api", "--paginate", $"repos/{repository}/issues/{issueNumber}/dependencies/blocked_by"];

    private static string ParseApiPages(string output)
    {
        // gh api --paginate writes each response page as a separate top-level
        // JSON value. --slurp is unnecessary and is not supported by all deployed
        // versions, so accept multiple values directly and combine the pages.
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(output),
            new JsonReaderOptions { AllowMultipleValues = true });
        var pages = new List<JsonElement>();
        while (reader.Read())
        {
            using var page = JsonDocument.ParseValue(ref reader);
            pages.Add(page.RootElement.Clone());
        }
        if (pages.Count == 0) throw new JsonException("GitHub API returned no JSON pages.");
        return JsonSerializer.Serialize(pages);
    }

    private static bool DependenciesAreClosed(JsonDocument pages)
    {
        var root = pages.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            throw new JsonException("GitHub dependency API did not return paginated JSON arrays.");

        // Inspect every page so a later unresolved dependency can never be hidden by the first.
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
        var permissions = await RunCapabilityAsync(["api", $"repos/{repository}", "--jq", ".permissions"], "GitHub repository access", ct);
        ValidateIssueWritePermission(permissions.StandardOutput, repository);
        ValidateGitWritePermission(permissions.StandardOutput);
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

    internal static void ValidateIssueWritePermission(string output, string? repository = null)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var permissions = document.RootElement;
            if (permissions.ValueKind != JsonValueKind.Object ||
                !HasPermission(permissions, "triage") && !HasPermission(permissions, "push") &&
                !HasPermission(permissions, "maintain") && !HasPermission(permissions, "admin"))
                throw new WorkerInfrastructureException("GitHub API authentication lacks permission to manage Issues and required repository labels.");
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (JsonException ex)
        {
            var context = repository is null ? "" : $" for '{repository}'";
            throw new WorkerInfrastructureException($"Could not determine GitHub Issue permissions{context}: {ex.Message}", ex);
        }
    }

    private static bool HasPermission(JsonElement permissions, string name) =>
        permissions.TryGetProperty(name, out var permission) && permission.ValueKind == JsonValueKind.True;

    internal static void ValidateGitWritePermission(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !HasPermission(document.RootElement, "push"))
                throw new WorkerInfrastructureException("GitHub repository authentication lacks Git push/write permission.");
        }
        catch (JsonException)
        {
            throw new WorkerInfrastructureException("Could not determine GitHub repository Git push/write permission.");
        }
    }

    private async Task<ProcessResult> RunCapabilityAsync(IEnumerable<string> args, string capability, CancellationToken ct)
    {
        try
        {
            var result = await runCommand(args, ct);
            if (result.ExitCode != 0)
                throw new WorkerInfrastructureException($"{capability} unavailable for repository '{repository}' (exit {result.ExitCode}). {Sanitize(Tail(result.StandardError), 1000)}");
            return result;
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) { throw new WorkerInfrastructureException($"{capability} unavailable for repository '{repository}': {ex.Message}", ex); }
    }

    public Task ReplaceLabelAsync(int issueNumber, string remove, string add, CancellationToken ct) =>
        RunGhAsync(["issue", "edit", issueNumber.ToString(), "--repo", repository, "--remove-label", remove, "--add-label", add], ct);

    public Task RemoveLabelAsync(int issueNumber, string label, CancellationToken ct) =>
        RunGhAsync(["issue", "edit", issueNumber.ToString(), "--repo", repository, "--remove-label", label], ct);

    public Task CommentAsync(int issueNumber, string comment, CancellationToken ct) =>
        RunGhAsync(["issue", "comment", issueNumber.ToString(), "--repo", repository, "--body", origin.Format(comment)], ct);

    public async Task EnsureSuccessCommentAsync(int issueNumber, Guid executionId, string comment, CancellationToken ct, bool allowCreate = true)
    {
        var marker = $"<!-- codex-worker-success:{executionId:D} -->";
        var body = comment + "\n\n" + marker;
        // Scan every page, with process output bounds enforced by the existing runner. A read
        // failure is uncertainty, never permission to append another durable report.
        var result = await RunGhAsync(SuccessCommentReadArguments(repository, issueNumber, marker), ct,
            allowGracefulCancellation: true);
        try
        {
            var matches = ReadCommentPages(result.StandardOutput);
            if (matches.Length == 1 && matches[0] is { } match &&
                CodexProvisioning.GeneratedMessageOrigin.IsGenerated(match) && match.Contains(body, StringComparison.Ordinal)) return;
            if (matches.Length != 0)
                throw new WorkerInfrastructureException("Success report marker is duplicated or its content changed; manual reconciliation is required.");
        }
        catch (JsonException)
        {
            throw new WorkerInfrastructureException("Success report presence could not be verified; manual reconciliation is required.");
        }
        if (!allowCreate)
            throw new WorkerInfrastructureException("Previously confirmed success report is missing; manual reconciliation is required.");
        await retry.ExecuteAsync("success comment", async token =>
        {
            await CommentAsync(issueNumber, body, token);
            return true;
        }, ex => ex is GitHubOperationException { FailureKind: GitHubFailureKind.TransientProvider }, ct,
            async token =>
            {
                var proof = await RunGhAsync(SuccessCommentReadArguments(repository, issueNumber, marker), token,
                    allowGracefulCancellation: true);
                var matches = ReadCommentPages(proof.StandardOutput);
                if (matches is { Length: 0 }) return false;
                if (matches is { Length: 1 } && matches[0] == origin.Format(body)) return true;
                throw new WorkerInfrastructureException("Success report proof changed or is ambiguous; reconciliation is required.");
            }, true);
    }

    internal static string[] SuccessCommentReadArguments(string repository, int issueNumber, string marker) =>
        ["api", $"repos/{repository}/issues/{issueNumber}/comments?per_page=100",
            "--paginate", "--jq", $"[.[] | select(.body | contains(\"{marker}\")) | .body]"];

    private static string[] ReadCommentPages(string output)
    {
        using var pages = JsonDocument.Parse(ParseApiPages(output));
        return pages.RootElement.EnumerateArray().SelectMany(page =>
            JsonSerializer.Deserialize<string[]>(page.GetRawText()) ?? throw new JsonException()).ToArray();
    }

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
        var arguments = args.ToArray();
        var mutation = IsMutation(arguments);
        var number = IssueNumber(arguments);
        // Comments without a durable identity remain fail-closed: absence alone cannot prove
        // that a delayed provider write will not eventually appear.
        if (mutation && arguments[1] == "comment")
            return await RunGhOnceAsync(arguments, ct, allowGracefulCancellation);
        GitHubIssueState? before = mutation && number is { } issue ? await ReadIssueStateAsync(issue, ct) : null;
        return await retry.ExecuteAsync(DescribeOperation(arguments),
            token => RunGhOnceAsync(arguments, token, allowGracefulCancellation),
            ex => ex is GitHubOperationException { FailureKind: GitHubFailureKind.TransientProvider }, ct,
            before is null ? null : async token =>
            {
                var current = await ReadIssueStateAsync(number.GetValueOrDefault(), token);
                var intendedLabels = before.Labels.ToHashSet(StringComparer.Ordinal);
                var remove = Array.IndexOf(arguments, "--remove-label");
                var add = Array.IndexOf(arguments, "--add-label");
                if (remove >= 0) intendedLabels.Remove(arguments[remove + 1]);
                if (add >= 0) intendedLabels.Add(arguments[add + 1]);
                var intendedOpen = arguments[1] == "close" ? false : before.IsOpen;
                if (current.IsOpen == intendedOpen && intendedLabels.SetEquals(current.Labels)) return true;
                if (current.IsOpen == before.IsOpen && before.Labels.ToHashSet(StringComparer.Ordinal).SetEquals(current.Labels)) return false;
                throw new GitHubOperationException(DescribeOperation(arguments), number, true,
                    GitHubFailureKind.Unknown, GitHubRemoteState.Uncertain,
                    "Unexpected remote Issue state; manual reconciliation is required.");
            }, new ProcessResult(0, "", ""));
    }

    private async Task<ProcessResult> RunGhOnceAsync(IEnumerable<string> args, CancellationToken ct, bool allowGracefulCancellation = false)
    {
        var arguments = args.ToArray();
        var operation = DescribeOperation(arguments);
        var issueNumber = IssueNumber(arguments);
        var mutation = IsMutation(arguments);

        ProcessResult result;
        try
        {
            result = await runCommand(arguments, ct);
        }
        catch (OperationCanceledException) when (allowGracefulCancellation && ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex)
        {
            var remoteState = mutation ? GitHubRemoteState.Uncertain : GitHubRemoteState.NotApplicable;
            throw new GitHubOperationException(operation, issueNumber, mutation, GitHubFailureKind.Cancellation,
                remoteState, $"GitHub CLI {operation} was cancelled; remote Issue state is " +
                $"{(mutation ? "uncertain" : "not changed by this read") }.", ex);
        }
        catch (GitHubOperationException)
        {
            throw;
        }
        catch (WorkerInfrastructureException ex)
        {
            var kind = ClassifyFailure(ex.Message);
            var remoteState = mutation ? RemoteState(kind) : GitHubRemoteState.NotApplicable;
            throw new GitHubOperationException(operation, issueNumber, mutation, kind, remoteState,
                $"GitHub CLI {operation} could not complete; remote Issue state is " +
                $"{(remoteState == GitHubRemoteState.Uncertain ? "uncertain and requires reconciliation" : remoteState == GitHubRemoteState.NotChanged ? "known unchanged" : "not changed by this read")}. " +
                Sanitize(ex.Message, 1000), ex);
        }
        catch (Exception ex)
        {
            var kind = ClassifyFailure(ex.Message);
            var remoteState = mutation ? RemoteState(kind) : GitHubRemoteState.NotApplicable;
            throw new GitHubOperationException(operation, issueNumber, mutation, kind, remoteState,
                $"GitHub CLI {operation} failed or timed out; remote Issue state is " +
                $"{(remoteState == GitHubRemoteState.Uncertain ? "uncertain and requires reconciliation" : remoteState == GitHubRemoteState.NotChanged ? "known unchanged" : "not changed by this read")}. " +
                Sanitize(ex.Message, 1000), ex);
        }

        if (result.ExitCode == 0) return result;
        var detail = Sanitize(Tail(result.StandardError), 1000);
        var failureKind = ClassifyFailure(detail);
        var failedRemoteState = mutation ? RemoteState(failureKind) : GitHubRemoteState.NotApplicable;
        throw new GitHubOperationException(operation, issueNumber, mutation, failureKind, failedRemoteState,
            $"GitHub CLI {operation} failed (exit {result.ExitCode}); remote Issue state is " +
            $"{(failedRemoteState == GitHubRemoteState.Uncertain ? "uncertain and requires reconciliation" : failedRemoteState == GitHubRemoteState.NotChanged ? "known unchanged" : "not changed by this read")}. {detail}");
    }


    private static bool IsMutation(IReadOnlyList<string> args) => args.Count >= 2 && args[0] == "issue" &&
        args[1] is "edit" or "comment" or "close";

    private static int? IssueNumber(IReadOnlyList<string> args)
    {
        if (args.Count >= 3 && args[0] == "issue" && args[1] is "view" or "edit" or "comment" or "close" &&
            int.TryParse(args[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var issueNumber))
            return issueNumber;
        if (args.Count < 2 || args[0] != "api") return null;

        foreach (var argument in args)
        {
            const string issuePath = "/issues/";
            var issuePathIndex = argument.IndexOf(issuePath, StringComparison.Ordinal);
            if (issuePathIndex < 0) continue;
            var numberStart = issuePathIndex + issuePath.Length;
            var numberEnd = argument.IndexOf('/', numberStart);
            var value = numberEnd < 0 ? argument[numberStart..] : argument[numberStart..numberEnd];
            if (int.TryParse(value, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out issueNumber))
                return issueNumber;
        }
        return null;
    }

    private static string DescribeOperation(IReadOnlyList<string> args) => args.Count >= 2 && args[0] == "issue"
        ? $"issue {args[1]}"
        : args.Count > 0 && args[0] == "api" ? "Issue API read" : "Issue query";

    internal static GitHubFailureKind ClassifyFailure(string detail)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(detail,
                @"(?i)unknown flag|unknown shorthand flag|requires an argument|Usage:\s+gh|HTTP request header in key:value format"))
            return GitHubFailureKind.LocalInvocation;
        if (System.Text.RegularExpressions.Regex.IsMatch(detail,
                @"(?i)rate limit|secondary rate limit|abuse detection|\b429\b|\b(500|502|503|504)\b|internal server error|internal provider error|something went wrong|temporarily unavailable|timed? out|connection reset|connection timed out|temporary failure in name resolution|unexpected EOF|try again later"))
            return GitHubFailureKind.TransientProvider;
        if (System.Text.RegularExpressions.Regex.IsMatch(detail,
                @"(?i)\b(401|403)\b|authentication required|not authorized|permission denied|resource not accessible"))
            return GitHubFailureKind.AuthenticationOrAuthorization;
        if (System.Text.RegularExpressions.Regex.IsMatch(detail,
                @"(?i)\b(404|422)\b|not found|could not resolve to a node|invalid (argument|request)|validation failed"))
            return GitHubFailureKind.DeterministicRequest;
        return GitHubFailureKind.Unknown;
    }

    private static GitHubRemoteState RemoteState(GitHubFailureKind kind) => kind is
        GitHubFailureKind.AuthenticationOrAuthorization or GitHubFailureKind.DeterministicRequest
        ? GitHubRemoteState.NotChanged : GitHubRemoteState.Uncertain;

    private static GitHubOperationException ReadFailure(string operation, int? issueNumber, string message, Exception inner) =>
        new(operation, issueNumber, false, GitHubFailureKind.Unknown, GitHubRemoteState.NotApplicable, message, inner);

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

    private static string Sanitize(string value, int maximumLength)
    {
        var safe = System.Text.RegularExpressions.Regex.Replace(value,
            "(?i)(token|password|secret|credential|api[_-]?key)(\\s*[:=]\\s*)[^\\s,;]+", "$1$2[redacted]");
        safe = System.Text.RegularExpressions.Regex.Replace(safe,
            "(?i)\\bBearer\\s+[A-Za-z0-9._~+/-]+=*", "Bearer [redacted]");
        safe = new string(safe.Where(character => !char.IsControl(character)).ToArray());
        return safe[..Math.Min(safe.Length, maximumLength)];
    }
}
