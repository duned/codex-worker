using System.Text.Json;
using CodexWorker;

namespace CodexWorker.Tests;

public sealed class GitHubLabelTests
{
    [Fact]
    public async Task ExistingLabelsAreLeftUnchangedAndNoCreateCommandRuns()
    {
        var fixture = new LabelCommandFixture();
        fixture.Existing.UnionWith(fixture.Required.Select(x => x.Name));
        await fixture.EnsureAsync();
        Assert.Empty(fixture.Created);
        Assert.Single(fixture.Commands);
        Assert.Equal(new[] { "label", "list" }, fixture.Commands[0].Take(2));
    }

    [Fact]
    public async Task OnlyMissingConfiguredLabelIsCreatedAndSecondStartupIsIdempotent()
    {
        var fixture = new LabelCommandFixture();
        fixture.Existing.UnionWith(fixture.Required.Select(x => x.Name));
        fixture.Existing.Remove("custom-ready");

        await fixture.EnsureAsync();
        await fixture.EnsureAsync();

        Assert.Equal(new[] { "custom-ready" }, fixture.Created);
        Assert.Equal("custom-ready", fixture.Commands[1][2]);
        Assert.DoesNotContain("--force", fixture.Commands.SelectMany(x => x));
        Assert.Contains("--color", fixture.Commands[1]);
    }

    [Fact]
    public async Task CreatesAllSevenCustomizedLabelsWhenNoneExist()
    {
        var fixture = new LabelCommandFixture();
        await fixture.EnsureAsync();
        Assert.Equal(new[] { "custom-ready", "custom-working", "custom-blocked", "custom-failed", "custom-integration-conflict", "custom-integration-recovery", "custom-done" }, fixture.Created);
        Assert.All(fixture.Created, name => Assert.Contains(name, fixture.Required.Select(x => x.Name)));
    }

    [Fact]
    public async Task LabelQueryFailureIsInfrastructureFailure()
    {
        var client = new GitHubClient("owner/repo", (_, _) => Task.FromResult(new ProcessResult(1, "", "permission denied")));
        var failure = await Assert.ThrowsAsync<WorkerInfrastructureException>(() => client.FindMissingLabelsAsync(Required(), CancellationToken.None));
        Assert.Contains("repository 'owner/repo'", failure.Message);
        Assert.Contains("permission denied", failure.Message);
    }

    [Fact]
    public async Task LabelCreationFailureIsInfrastructureFailureAndNeverUsesForce()
    {
        var commands = new List<string[]>();
        var client = new GitHubClient("owner/repo", (args, _) =>
        {
            var array = args.ToArray();
            commands.Add(array);
            return Task.FromResult(new ProcessResult(1, "", "label service unavailable"));
        });
        var failure = await Assert.ThrowsAsync<WorkerInfrastructureException>(() =>
            client.CreateLabelAsync(Required()[0], CancellationToken.None));
        Assert.Contains("custom-ready", failure.Message);
        Assert.Contains("uncertain", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--force", commands.SelectMany(x => x));
    }

    [Fact]
    public async Task CancellationDuringLabelQueryIsSafeButCreateCancellationIsUncertain()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new GitHubClient("owner/repo", (_, ct) => Task.FromException<ProcessResult>(new OperationCanceledException(ct)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FindMissingLabelsAsync(Required(), cancellation.Token));
        var failure = await Assert.ThrowsAsync<WorkerInfrastructureException>(() => client.CreateLabelAsync(Required()[0], cancellation.Token));
        Assert.Contains("remote state may be uncertain", failure.Message);
    }

    private static IReadOnlyList<RequiredGitHubLabel> Required() =>
    [
        new("custom-ready", "1D76DB", "ready"), new("custom-working", "FBCA04", "working"),
        new("custom-blocked", "D93F0B", "blocked"), new("custom-failed", "B60205", "failed"),
        new("custom-integration-conflict", "D4C5F9", "integration conflict"),
        new("custom-integration-recovery", "5319E7", "integration recovery"),
        new("custom-done", "0E8A16", "done")
    ];

    private sealed class LabelCommandFixture
    {
        public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Created { get; } = [];
        public List<string[]> Commands { get; } = [];
        public IReadOnlyList<RequiredGitHubLabel> Required { get; } = Required();
        private readonly GitHubClient _client;

        public LabelCommandFixture()
        {
            _client = new GitHubClient("owner/repo", (arguments, _) =>
            {
                var args = arguments.ToArray();
                Commands.Add(args);
                if (args[1] == "list")
                {
                    var json = JsonSerializer.Serialize(Existing.Select(name => new { name }));
                    return Task.FromResult(new ProcessResult(0, json, ""));
                }
                if (args[1] == "create")
                {
                    Created.Add(args[2]);
                    Existing.Add(args[2]);
                    return Task.FromResult(new ProcessResult(0, "", ""));
                }
                throw new InvalidOperationException("Unexpected GitHub command.");
            });
        }

        public async Task EnsureAsync()
        {
            var missing = await _client.FindMissingLabelsAsync(Required, CancellationToken.None);
            foreach (var label in missing) await _client.CreateLabelAsync(label, CancellationToken.None);
        }
    }
}
