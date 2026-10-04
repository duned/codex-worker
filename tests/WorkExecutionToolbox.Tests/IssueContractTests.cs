namespace WorkExecutionToolbox.Tests;

public sealed class IssueContractTests
{
    [Fact]
    public async Task IndependentHostCanReadAndRequestRelationshipsUsingHumanNumbers()
    {
        var repository = new RepositoryContext("team/project");
        var issue = new IssueReference(repository, 42);
        var host = new TestProvider();
        IIssueRelationshipProvider provider = host;

        var summary = await provider.GetIssueAsync(issue);
        Assert.NotNull(summary);
        Assert.Equal(42, summary.Issue.Number);
        var relationships = await provider.GetRelationshipsAsync(issue);
        Assert.NotNull(relationships);
        Assert.Equal(10, relationships.Parent?.Issue.Number);
        Assert.Equal(43, Assert.Single(relationships.Children).Issue.Number);
        Assert.Equal(20, Assert.Single(relationships.BlockedBy).Issue.Number);
        Assert.Equal(50, Assert.Single(relationships.Blocking).Issue.Number);

        var parent = new SetParentRequest(issue, 11, previewOnly: true);
        Assert.Equal(RelationshipChangeStatus.Preview, (await provider.SetParentAsync(parent)).Status);
        Assert.Same(parent, host.ParentRequest);
        Assert.NotNull(host.ParentRequest);
        Assert.True(host.ParentRequest.PreviewOnly);
        Assert.Equal(11, host.ParentRequest.ParentIssueNumber);
        var clear = new SetParentRequest(issue, null);
        await provider.SetParentAsync(clear);
        Assert.Null(host.ParentRequest.ParentIssueNumber);

        var dependency = new SetDependencyRequest(issue, 21, applied: true);
        await provider.SetDependencyAsync(dependency);
        Assert.Same(dependency, host.DependencyRequest);
        Assert.NotNull(host.DependencyRequest);
        Assert.Equal(21, host.DependencyRequest.BlockerIssueNumber);
        Assert.True(host.DependencyRequest.Applied);
        await provider.SetDependencyAsync(new(issue, 21, applied: false));
        Assert.False(host.DependencyRequest.Applied);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveHumanNumbersAreRejected(int number)
    {
        var repository = new RepositoryContext("team/project");
        var issue = new IssueReference(repository, 42);
        Assert.Throws<ArgumentOutOfRangeException>(() => new IssueReference(repository, number));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SetParentRequest(issue, number));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SetDependencyRequest(issue, number, true));
    }

    [Fact]
    public void SelfRelationshipsAreRejectedBeforeProviderCalls()
    {
        var issue = new IssueReference(new("team/project"), 42);
        Assert.Throws<ArgumentException>(() => new SetParentRequest(issue, 42));
        Assert.Throws<ArgumentException>(() => new SetDependencyRequest(issue, 42, false));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("team/\nproject")]
    public void InvalidRepositoryContextsAreRejected(string repository)
    {
        Assert.Throws<ArgumentException>(() => new RepositoryContext(repository));
    }

    [Fact]
    public void RepositoryContextsAreBoundedAndRequired()
    {
        Assert.Throws<ArgumentException>(() => new RepositoryContext(new string('a', 513)));
        Assert.Throws<ArgumentNullException>(() => new RepositoryContext(null!));
        Assert.Throws<ArgumentNullException>(() => new IssueReference(null!, 42));
        Assert.Throws<ArgumentNullException>(() => new SetParentRequest(null!, null));
        Assert.Throws<ArgumentNullException>(() => new SetDependencyRequest(null!, 1, true));
    }

    [Fact]
    public async Task HostCanPassCancellationThroughEveryOperation()
    {
        IIssueRelationshipProvider provider = new TestProvider();
        var issue = new IssueReference(new("team/project"), 42);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.GetIssueAsync(issue, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.GetRelationshipsAsync(issue, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.SetParentAsync(new(issue, null), cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.SetDependencyAsync(new(issue, 1, true), cancellation.Token));
    }

    // This host references only the toolbox. No Worker/Server configuration, gh process,
    // credentials, external service or provider-specific Issue identity is needed.
    private sealed class TestProvider : IIssueRelationshipProvider
    {
        public SetParentRequest? ParentRequest { get; private set; }
        public SetDependencyRequest? DependencyRequest { get; private set; }

        public Task<IssueSummary?> GetIssueAsync(IssueReference issue, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IssueSummary?>(Summary(issue.Repository, issue.Number));
        }

        public Task<IssueRelationships?> GetRelationshipsAsync(IssueReference issue, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IssueRelationships?>(new(Summary(issue.Repository, issue.Number),
                Summary(issue.Repository, 10), [Summary(issue.Repository, 43)],
                [Summary(issue.Repository, 20)], [Summary(issue.Repository, 50)]));
        }

        public Task<RelationshipChangeResult> SetParentAsync(SetParentRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ParentRequest = request;
            return Task.FromResult(new RelationshipChangeResult(request.PreviewOnly
                ? RelationshipChangeStatus.Preview : RelationshipChangeStatus.Changed));
        }

        public Task<RelationshipChangeResult> SetDependencyAsync(SetDependencyRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DependencyRequest = request;
            return Task.FromResult(new RelationshipChangeResult(request.PreviewOnly
                ? RelationshipChangeStatus.Preview : RelationshipChangeStatus.Changed));
        }

        private static IssueSummary Summary(RepositoryContext repository, int number) => new(new(repository, number), "Example", IssueState.Open);
    }
}
