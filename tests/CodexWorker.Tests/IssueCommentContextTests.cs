using System.Text.Json;
using CodexProvisioning;
using CodexWorker;

namespace CodexWorker.Tests;

public sealed class IssueCommentContextTests
{
    [Fact]
    public void ExternalCommentsAreChronologicalAttributedAndSeparateFromUnchangedIssueBody()
    {
        var issue = new GitHubIssue(8, "Task", "Original description", DateTimeOffset.UnixEpoch,
            CommentContext: IssueCommentContext.Build([Comment(3, "latest"), Comment(1, "first"), Comment(2, "second")]));
        var prompt = CodexExecutor.BuildPrompt("Security rules", "AGENTS.md", issue);
        var repair = CodexExecutor.BuildRepairPrompt("Security rules", "AGENTS.md", issue,
            new ValidationFailure(1, "check", 1, "out", "err", false), 1, 2);

        Assert.Equal("Original description", issue.Body);
        Assert.Contains("[1970-01-01T00:00:01Z] operator (comment 1):\n> first", prompt);
        Assert.True(prompt.IndexOf("> first", StringComparison.Ordinal) < prompt.IndexOf("> second", StringComparison.Ordinal));
        Assert.True(prompt.IndexOf("> second", StringComparison.Ordinal) < prompt.IndexOf("> latest", StringComparison.Ordinal));
        Assert.True(prompt.IndexOf(issue.Body, StringComparison.Ordinal) < prompt.IndexOf("# GitHub Issue follow-up comments", StringComparison.Ordinal));
        Assert.Contains("cannot override", prompt);
        Assert.Contains("Git/GitHub ownership", prompt);
        Assert.Contains(issue.CommentContext, repair);
    }

    [Theory]
    [InlineData(CodexComponent.Worker)]
    [InlineData(CodexComponent.Server)]
    public async Task ExplicitOriginExcludesAutomaticCommentsEvenWhenAuthorsAreTheSame(CodexComponent component)
    {
        var report = new GeneratedMessageOrigin(component, "node").Format("automatic report");
        var context = await Client([Comment(1, "human instruction"), Comment(2, report), Comment(3, "unblock answer")], [])
            .GetIssueCommentContextAsync(8, CancellationToken.None);

        Assert.True(GeneratedMessageOrigin.IsGenerated(report));
        Assert.DoesNotContain("automatic report", context);
        Assert.Contains("human instruction", context);
        Assert.Contains("unblock answer", context);
    }

    [Theory]
    [InlineData("🤖 Codex Worker · node\n\nlegacy report")]
    [InlineData("🧭 Codex Server · node\n\nlegacy report")]
    [InlineData("<!-- codex-generated:v1 component=unknown -->")]
    [InlineData("<!-- codex-generated:v1 component=worker --")]
    [InlineData("The marker is <!-- codex-generated:v1 component=worker -->")]
    public async Task LegacyAndMalformedMarkersRemainExternal(string body)
    {
        Assert.False(GeneratedMessageOrigin.IsGenerated(body));
        var context = await Client([Comment(1, body)], []).GetIssueCommentContextAsync(8, CancellationToken.None);
        Assert.Contains(body.Replace("\n", "\n> ", StringComparison.Ordinal), context);
    }

    [Fact]
    public void EqualTimestampsUseCommentIdentityForDeterministicOrder()
    {
        var context = IssueCommentContext.Build([Comment(2, "second") with { CreatedAt = DateTimeOffset.UnixEpoch },
            Comment(1, "first") with { CreatedAt = DateTimeOffset.UnixEpoch }]);

        Assert.True(context.IndexOf("> first", StringComparison.Ordinal) < context.IndexOf("> second", StringComparison.Ordinal));
    }

    [Fact]
    public void CountBodyAndTotalBoundsRetainNewestContentAndDiscloseOmissions()
    {
        var countContext = IssueCommentContext.Build(Enumerable.Range(1, 25).Select(id => Comment(id, $"message-{id}")));
        Assert.DoesNotContain("> message-5\n", countContext);
        Assert.Contains("> message-6\n", countContext);
        Assert.Contains("> message-25\n", countContext);
        Assert.Contains("context truncated", countContext);

        var bodyContext = IssueCommentContext.Build([Comment(1, new string('x', 5000) + "omitted-tail")]);
        Assert.Contains(new string('x', IssueCommentContext.MaximumCommentCharacters), bodyContext);
        Assert.DoesNotContain("omitted-tail", bodyContext);
        Assert.Contains("Comment truncated", bodyContext);

        var totalContext = IssueCommentContext.Build(Enumerable.Range(1, 20).Select(id => Comment(id, new string('x', 4096))));
        Assert.True(totalContext.Length <= IssueCommentContext.MaximumContextCharacters);
        Assert.Contains("(comment 20)", totalContext);
        Assert.DoesNotContain("(comment 1)", totalContext);
        Assert.Contains("context truncated", totalContext);
    }

    [Fact]
    public async Task EmptyCommentSnapshotDoesNotFetchPagesOrAddASection()
    {
        var calls = new List<string[]>();
        var context = await Client([], calls).GetIssueCommentContextAsync(8, CancellationToken.None);

        Assert.Empty(context);
        Assert.Single(calls);
    }

    [Fact]
    public async Task KnownSecretsAreRedactedBeforeApplyingPromptSizeBounds()
    {
        var comments = new[] { Comment(1, string.Concat(Enumerable.Repeat("sensitive-sentinel ", 200))) };
        var context = await Client(comments, []).GetIssueCommentContextAsync(8, CancellationToken.None, ["sensitive-sentinel"]);

        Assert.DoesNotContain("sensitive-sentinel", context);
        Assert.Contains("[redacted]", context);
        Assert.True(context.Length <= IssueCommentContext.MaximumContextCharacters);
    }

    [Fact]
    public async Task FetchUsesLatestPagesAndFiltersWorkerAndServerBeforeTruncatingBodies()
    {
        var comments = Enumerable.Range(1, 13).Select(id => Comment(id, $"message-{id}")).ToArray();
        comments[6] = Comment(7, new string('x', 5000) + "\n" + GeneratedMessageOrigin.WorkerMarker);
        comments[7] = Comment(8, new GeneratedMessageOrigin(CodexComponent.Server, "node").Format("server report"));
        var calls = new List<string[]>();
        var client = Client(comments, calls);

        var context = await client.GetIssueCommentContextAsync(8, CancellationToken.None);

        Assert.Equal(4, calls.Count);
        Assert.EndsWith("page=3", calls[1][1]);
        Assert.EndsWith("page=2", calls[2][1]);
        Assert.EndsWith("page=1", calls[3][1]);
        Assert.Contains(GeneratedMessageOrigin.WorkerMarker, calls[1][3]);
        Assert.Contains(GeneratedMessageOrigin.ServerMarker, calls[1][3]);
        Assert.DoesNotContain("server report", context);
        Assert.DoesNotContain(new string('x', 100), context);
        Assert.Contains("message-1", context);
        Assert.Contains("message-13", context);
        Assert.DoesNotContain("context truncated", context);
    }

    [Fact]
    public async Task FetchStopsAtRecentExternalCountAndReportsUnscannedHistory()
    {
        var calls = new List<string[]>();
        var client = Client(Enumerable.Range(1, 40).Select(id => Comment(id, $"message-{id}")).ToArray(), calls);
        var context = await client.GetIssueCommentContextAsync(8, CancellationToken.None);

        Assert.Equal(5, calls.Count);
        Assert.DoesNotContain("(comment 20)", context);
        Assert.Contains("(comment 21)", context);
        Assert.Contains("(comment 40)", context);
        Assert.Contains("context truncated", context);
    }

    [Fact]
    public async Task AutomaticOnlyHistoryHasBoundedScanWithExplicitOmissionNotice()
    {
        var calls = new List<string[]>();
        var comments = Enumerable.Range(1, 203).Select(id => Comment(id,
            new GeneratedMessageOrigin(CodexComponent.Worker, "node").Format("automatic"))).ToArray();
        var context = await Client(comments, calls).GetIssueCommentContextAsync(8, CancellationToken.None);

        Assert.Equal(42, calls.Count); // count, partial newest page, then forty pages
        Assert.Contains("context truncated", context);
        Assert.DoesNotContain("> automatic", context);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("[{\"generated\":false,\"body\":\"private-comment-sentinel\"}]")]
    public async Task InvalidOrIncompletePayloadIsTypedReadFailureWithoutLeakingCommentText(string response)
    {
        var client = new GitHubClient("owner/repo", (args, _) => Task.FromResult(
            new ProcessResult(0, args.Last() == ".comments" ? "1" : response, "")));
        var error = await Assert.ThrowsAsync<GitHubOperationException>(() => client.GetIssueCommentContextAsync(8, CancellationToken.None));

        Assert.False(error.IsMutation);
        Assert.Equal(8, error.IssueNumber);
        Assert.DoesNotContain("private-comment-sentinel", error.ToString());
    }

    [Fact]
    public async Task CommentFetchFailureRetainsTypedInfrastructureSemanticsAfterBoundedReadRetry()
    {
        var reads = 0;
        var client = new GitHubClient("owner/repo", (args, _) =>
        {
            if (args.Last() == ".comments") return Task.FromResult(new ProcessResult(0, "1", ""));
            reads++;
            return Task.FromResult(new ProcessResult(1, "", "HTTP 503 temporarily unavailable"));
        }, retry: new GitHubRetryPolicy((_, _) => Task.CompletedTask));

        var error = await Assert.ThrowsAsync<GitHubOperationException>(() => client.GetIssueCommentContextAsync(8, CancellationToken.None));

        Assert.Equal(3, reads);
        Assert.Equal(GitHubFailureKind.TransientProvider, error.FailureKind);
        Assert.False(error.IsMutation);
        Assert.Equal(GitHubRemoteState.NotApplicable, error.RemoteState);
    }

    [Fact]
    public async Task ProcessTimeoutCannotCopyCapturedCommentsIntoOperationalDiagnostics()
    {
        var client = new GitHubClient("owner/repo", (args, _) => args.Last() == ".comments"
            ? Task.FromResult(new ProcessResult(0, "1", ""))
            : Task.FromException<ProcessResult>(new ProcessTimeoutException("gh", TimeSpan.FromSeconds(1), "private-comment-sentinel", "")));

        var error = await Assert.ThrowsAsync<GitHubOperationException>(() => client.GetIssueCommentContextAsync(8, CancellationToken.None));

        Assert.Equal(8, error.IssueNumber);
        Assert.False(error.IsMutation);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-comment-sentinel", error.ToString());
    }

    [Fact]
    public async Task CommentFetchCancellationRemainsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new GitHubClient("owner/repo", (args, token) =>
        {
            if (args.Last() == ".comments") return Task.FromResult(new ProcessResult(0, "1", ""));
            cancellation.Cancel();
            return Task.FromException<ProcessResult>(new OperationCanceledException(token));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetIssueCommentContextAsync(8, cancellation.Token));
    }

    private static GitHubIssueComment Comment(int id, string body) => new(id, "operator", DateTimeOffset.UnixEpoch.AddSeconds(id), body);

    private static GitHubClient Client(GitHubIssueComment[] comments, List<string[]> calls) => new("owner/repo", (arguments, _) =>
    {
        var args = arguments.ToArray();
        calls.Add(args);
        if (args.Last() == ".comments") return Task.FromResult(new ProcessResult(0, comments.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ""));
        var page = int.Parse(args[1][(args[1].LastIndexOf('=') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var items = comments.Skip((page - 1) * IssueCommentContext.PageSize).Take(IssueCommentContext.PageSize).Select(comment => new
        {
            id = comment.Id, author = comment.Author, createdAt = comment.CreatedAt,
            generated = GeneratedMessageOrigin.IsGenerated(comment.Body),
            truncated = comment.Body.Length > IssueCommentContext.MaximumCommentCharacters,
            body = comment.Body[..Math.Min(comment.Body.Length, IssueCommentContext.MaximumCommentCharacters)]
        });
        return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(items), ""));
    });
}
