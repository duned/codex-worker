using CodexProvisioning;
using System.Text.Json;
using CodexWorker;

namespace CodexWorker.Tests;

public sealed class GitHubDependencyTests
{
    [Fact]
    public async Task DependencyLookupUsesRestApiAndIssueListingDoesNotRequestBlockedBy()
    {
        var fixture = new Fixture([Issue(1, "2025-01-01T00:00:00Z")]);

        var issue = await fixture.Client.FindOldestReadyAsync("codex-ready", CancellationToken.None);

        Assert.Equal(1, issue?.Number);
        Assert.Contains(fixture.Commands[0], arg => arg == "number,title,body,createdAt,labels");
        Assert.DoesNotContain(fixture.Commands.SelectMany(args => args), arg => arg.Contains("blockedBy", StringComparison.Ordinal));
        Assert.Contains(fixture.Commands, args => args.SequenceEqual([
            "api", "--paginate", "repos/owner/repo/issues/1/dependencies/blocked_by"]));
        Assert.DoesNotContain(fixture.Commands.SelectMany(args => args), arg => arg is "--slurp" or "view");
        Assert.DoesNotContain(fixture.Commands.SelectMany(args => args), arg => arg is "--add-label" or "--remove-label");
    }

    [Fact]
    public async Task ReadyIssueWithoutDependenciesIsEligible()
    {
        var fixture = new Fixture([Issue(1, "2025-01-01T00:00:00Z")]);
        Assert.Equal(1, (await fixture.Client.FindOldestReadyAsync("ready", CancellationToken.None))?.Number);
    }

    [Fact]
    public async Task ReadyIssueLookupPreservesLabelsForSchedulingIntent()
    {
        var fixture = new Fixture([IssueWithLabels(1, "2025-01-01T00:00:00Z", "team-ready", "team-recover")]);

        var issue = await fixture.Client.FindOldestReadyAsync("team-recover", CancellationToken.None);

        Assert.Equal(new[] { "team-ready", "team-recover" }, issue?.Labels);
    }

    [Fact]
    public async Task ReadyIssueWithAllDependenciesClosedIsEligible()
    {
        var fixture = new Fixture([Issue(2, "2025-01-01T00:00:00Z", "CLOSED", "CLOSED")]);
        Assert.Equal(2, (await fixture.Client.FindOldestReadyAsync("ready", CancellationToken.None))?.Number);
    }

    [Fact]
    public async Task ReadyIssueLookupSkipsIssuesAlreadyActiveOnThisWorker()
    {
        var fixture = new Fixture([Issue(1, "2025-01-01T00:00:00Z"), Issue(2, "2025-01-02T00:00:00Z")]);

        var issue = await fixture.Client.FindOldestReadyAsync("ready", new HashSet<int> { 1 }, CancellationToken.None);

        Assert.Equal(2, issue?.Number);
        Assert.DoesNotContain(fixture.Commands, args => args.SequenceEqual([
            "api", "--paginate", "repos/owner/repo/issues/1/dependencies/blocked_by"]));
    }

    [Theory]
    [InlineData("OPEN")]
    [InlineData("CLOSED", "OPEN")]
    public async Task CandidateWithAnyOpenDependencyIsSkippedForLaterIndependentIssue(params string[] states)
    {
        var fixture = new Fixture([Issue(10, "2025-01-01T00:00:00Z", states), Issue(11, "2025-01-02T00:00:00Z")]);
        Assert.Equal(11, (await fixture.Client.FindOldestReadyAsync("ready", CancellationToken.None))?.Number);
    }

    [Fact]
    public async Task UnresolvedDependencyOnLaterPagePreventsEligibility()
    {
        var fixture = new Fixture([Issue(20, "2025-01-01T00:00:00Z")], pages: [["CLOSED"], ["OPEN"]]);
        Assert.Null(await fixture.Client.FindOldestReadyAsync("ready", CancellationToken.None));
    }

    [Fact]
    public async Task DependencyApiFailureDoesNotReturnAnEligibleIssue()
    {
        var fixture = new Fixture([Issue(30, "2025-01-01T00:00:00Z")], dependencyExit: 1);
        var failure = await Assert.ThrowsAsync<WorkerInfrastructureException>(() =>
            fixture.Client.FindOldestReadyAsync("ready", CancellationToken.None));
        Assert.Contains("permission denied", failure.Message);
    }

    [Fact]
    public async Task StartupCapabilitiesAreReadOnlyAndProbeDependencyApiOnAnExistingIssue()
    {
        var commands = new List<string[]>();
        var client = new GitHubClient("owner/repo", (args, _) =>
        {
            var command = args.ToArray();
            commands.Add(command);
            var output = command.Contains("--version") ? "gh version 2.45.0" :
                command.Contains("--jq") ? "{\"push\":true,\"pull\":true}" :
                command.Contains("number") ? "[{\"number\":7}]" :
                command.Any(arg => arg.Contains("/dependencies/blocked_by", StringComparison.Ordinal)) ? "[]" : "{}";
            return Task.FromResult(new ProcessResult(0, output, ""));
        });

        await client.ValidateCapabilitiesAsync(CancellationToken.None);

        Assert.Contains(commands, command => command.SequenceEqual(["--version"]));
        Assert.Contains(commands, command => command.SequenceEqual(["auth", "status"]));
        Assert.Contains(commands, command => command.Any(arg => arg.Contains("/dependencies/blocked_by", StringComparison.Ordinal)));
        Assert.DoesNotContain(commands.SelectMany(command => command), argument => argument is "--add-label" or "--remove-label" or "comment");
    }

    [Fact]
    public async Task StartupDependencyCapabilityFailureIsInfrastructureFailure()
    {
        var dependencyReached = false;
        var client = new GitHubClient("owner/repo", (args, _) =>
        {
            if (args.Any(arg => arg.Contains("/dependencies/blocked_by", StringComparison.Ordinal)))
            {
                dependencyReached = true;
                return Task.FromResult(new ProcessResult(1, "", "endpoint unavailable"));
            }
            var output = args.Contains("number") ? "[{\"number\":7}]" : args.Contains("--jq") ? "{\"push\":true}" : "{}";
            return Task.FromResult(new ProcessResult(0, output, ""));
        });

        var failure = await Assert.ThrowsAsync<WorkerInfrastructureException>(() => client.ValidateCapabilitiesAsync(CancellationToken.None));

        Assert.True(dependencyReached);
        Assert.Contains("endpoint unavailable", failure.Message);
    }

    [Fact]
    public async Task StartupCapabilityChecksDoNotAssumeRepositoryContainsIssues()
    {
        var commands = new List<string[]>();
        var client = new GitHubClient("owner/repo", (args, _) =>
        {
            var command = args.ToArray();
            commands.Add(command);
            return Task.FromResult(new ProcessResult(0, command.Contains("number") ? "[]" :
                command.Contains("--jq") ? "{\"push\":true}" : "{}", ""));
        });

        await client.ValidateCapabilitiesAsync(CancellationToken.None);

        Assert.Contains(commands, command => command.Contains("issue") && command.Contains("list"));
        Assert.DoesNotContain(commands.SelectMany(command => command), argument => argument.Contains("/dependencies/blocked_by", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartupRejectsGitHubAuthenticationWithoutIssueWritePermission()
    {
        var client = new GitHubClient("owner/repo", (args, _) => Task.FromResult(new ProcessResult(0,
            args.Contains("--jq") ? "{\"pull\":true}" : "[]", "")));

        var failure = await Assert.ThrowsAsync<WorkerInfrastructureException>(() => client.ValidateCapabilitiesAsync(CancellationToken.None));

        Assert.Contains("lacks permission to manage Issues", failure.Message);
    }

    [Theory]
    [InlineData("{\"maintain\":true}", true)]
    [InlineData("{\"push\":true}", true)]
    [InlineData("{\"triage\":true}", true)]
    [InlineData("{\"pull\":true}", false)]
    public void IssueWritePermissionIsCheckedWithoutRequestingSecrets(string permissions, bool ready)
    {
        if (ready) GitHubClient.ValidateIssueWritePermission(permissions);
        else Assert.Throws<WorkerInfrastructureException>(() => GitHubClient.ValidateIssueWritePermission(permissions));
    }

    [Fact]
    public async Task ApiCredentialProvisioningSendsSecretOnlyOnStandardInputAndVerifiesAccess()
    {
        const string secret = "fake-worker-test-token";
        string? suppliedInput = null;
        string[]? suppliedArguments = null;
        var github = new GitHubClient("team/repo", (args, _) =>
        {
            var command = args.ToArray();
            var output = command.Contains("--version") ? "gh version 2.45.0" :
                command.Contains("--jq") ? "{\"push\":true}" : "[]";
            return Task.FromResult(new ProcessResult(0, output, ""));
        });
        var provisioner = new GitHubAuthenticationProvisioner(
            (executable, arguments, _, timeout, _, input) =>
            {
                Assert.Equal("gh", executable);
                Assert.Equal(TimeSpan.FromSeconds(30), timeout);
                suppliedArguments = arguments.ToArray();
                suppliedInput = input;
                return Task.FromResult(new ProcessResult(0, "", ""));
            },
            repository => repository == "team/repo" ? github : null,
            _ => null,
            (_, _) => Task.FromResult<CredentialDeliveryResponse?>(new CredentialDeliveryResponse("credential-id", "github", "api-token", 1, secret)));

        var result = await provisioner.ExecuteAsync(new ProvisioningActionContract("auth", "authentication", "github-api",
            Operation: "provision", CredentialId: "credential-id", Scope: "team/repo"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(secret + "\n", suppliedInput);
        Assert.DoesNotContain(secret, suppliedArguments!);
        Assert.DoesNotContain(secret, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationDuringDependencyLookupPropagatesSafely()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new GitHubClient("owner/repo", (_, ct) => Task.FromException<ProcessResult>(new OperationCanceledException(ct)));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FindOldestReadyAsync("ready", cancellation.Token));
    }

    private static Dictionary<string, JsonElement> Issue(int number, string createdAt, params string[] states)
    {
        var json = JsonSerializer.Serialize(new { number, title = $"Issue {number}", body = "", createdAt, states, labels = Array.Empty<string>() });
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    }

    private static Dictionary<string, JsonElement> IssueWithLabels(int number, string createdAt, params string[] labels)
    {
        var json = JsonSerializer.Serialize(new { number, title = $"Issue {number}", body = "", createdAt,
            states = Array.Empty<string>(), labels });
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    }

    private sealed class Fixture
    {
        public List<string[]> Commands { get; } = [];
        public GitHubClient Client { get; }

        public Fixture(IEnumerable<Dictionary<string, JsonElement>> issues, string[][]? pages = null, int dependencyExit = 0)
        {
            var issueList = issues.ToArray();
            var output = JsonSerializer.Serialize(issueList.Select(issue => new
            {
                number = issue["number"].GetInt32(),
                title = issue["title"].GetString(),
                body = issue["body"].GetString(),
                createdAt = issue["createdAt"].GetDateTimeOffset(),
                labels = issue["labels"].EnumerateArray().Select(label => new { name = label.GetString() })
            }));
            var issuesByNumber = issueList.ToDictionary(issue => issue["number"].GetInt32());
            Client = new GitHubClient("owner/repo", (args, _) =>
            {
                var command = args.ToArray();
                Commands.Add(command);
                if (command.Contains("api") && command.Any(arg => arg.Contains("/dependencies/blocked_by", StringComparison.Ordinal)))
                {
                    var issueNumber = int.Parse(command.Single(arg => arg.Contains("/dependencies/blocked_by", StringComparison.Ordinal)).Split('/')[4]);
                    var dependencyStates = pages ?? [issuesByNumber[issueNumber]["states"].EnumerateArray().Select(state => state.GetString()!).ToArray()];
                    return Task.FromResult(dependencyExit == 0
                        ? new ProcessResult(0, string.Join("\n", dependencyStates.Select(page => JsonSerializer.Serialize(page.Select(state => new { state }).ToArray()))), "")
                        : new ProcessResult(dependencyExit, "", "permission denied"));
                }
                return Task.FromResult(new ProcessResult(0, output, ""));
            });
        }
    }
}
