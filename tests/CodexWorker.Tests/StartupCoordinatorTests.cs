using CodexWorker;

namespace CodexWorker.Tests;

public sealed class StartupCoordinatorTests
{
    [Fact]
    public async Task HostMapsPreOperationalCheckoutFailureToStartupExitCode()
    {
        var config = new WorkerConfiguration
        {
            Project = new ProjectSettings { Name = "Test", Repository = "owner/repo", Directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) },
            GitHub = new GitHubSettings { ReadyLabel = "ready", WorkingLabel = "working", BlockedLabel = "blocked", FailedLabel = "failed", DoneLabel = "done" },
            Codex = new CodexSettings { InstructionsFile = "instructions.md" }
        };
        var host = new WorkerHost(new GlobalWorkerConfiguration(), [("project.yml", config)],
            new WorkerConsole(new StringWriter(), interactive: false));

        var failure = await Assert.ThrowsAsync<WorkerStartupException>(() => host.RunAsync(CancellationToken.None));

        Assert.Contains("checkout does not exist", failure.Message);
        Assert.Equal(ProcessExitCodes.StartupFailure, Program.ExitCodeFor(failure));
    }

    [Fact]
    public async Task InvalidFirstProjectPreventsEveryLabelOperation()
    {
        var events = new List<string>();
        var projects = new[]
        {
            Plan("A", events, validateError: new InvalidDataException("bad checkout")),
            Plan("B", events)
        };
        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => StartupCoordinator.RunAsync(projects, CancellationToken.None));
        Assert.Equal(new[] { "validate:A" }, events);
    }

    [Fact]
    public async Task ValidFirstAndInvalidSecondProjectCreateNoLabelsAnywhere()
    {
        var events = new List<string>();
        var projects = new[]
        {
            Plan("A", events, missing: [new RequiredGitHubLabel("a-ready", "123456", "ready")]),
            Plan("B", events, validateError: new InvalidDataException("invalid origin"))
        };
        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => StartupCoordinator.RunAsync(projects, CancellationToken.None));
        Assert.Equal(new[] { "validate:A", "validate:B" }, events);
        Assert.DoesNotContain(events, x => x.StartsWith("create:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AllReadOnlyValidationFinishesBeforeLabelQueriesAndCreation()
    {
        var events = new List<string>();
        var projects = new[]
        {
            Plan("A", events, missing: [new RequiredGitHubLabel("a-ready", "123456", "ready")]),
            Plan("B", events, missing: [new RequiredGitHubLabel("b-done", "654321", "done")])
        };

        var created = await StartupCoordinator.RunAsync(projects, CancellationToken.None);

        Assert.Equal(2, created);
        Assert.Equal(new[]
        {
            "validate:A", "validate:B", "capabilities:A", "capabilities:B", "query:A", "query:B",
            "create:A:a-ready", "create:B:b-done", "initialize:A", "initialize:B"
        }, events);
    }

    [Fact]
    public async Task LabelDiscoveryFailureStopsBeforeAnyCreationInitializationPreflightOrQueueWork()
    {
        var events = new List<string>();
        var projects = new[]
        {
            Plan("A", events, missing: [new RequiredGitHubLabel("a-ready", "123456", "ready")]),
            Plan("B", events, queryError: new WorkerInfrastructureException("label query failed"))
        };
        var laterPhasesReached = false;

        await Assert.ThrowsAsync<WorkerInfrastructureException>(async () =>
        {
            await StartupCoordinator.RunAsync(projects, CancellationToken.None);
            laterPhasesReached = true; // Host performs Codex preflight and then queue scans only after this barrier.
        });

        Assert.False(laterPhasesReached);
        Assert.DoesNotContain(events, x => x.StartsWith("create:", StringComparison.Ordinal));
        Assert.DoesNotContain(events, x => x.StartsWith("initialize:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GitHubCapabilityFailureStopsBeforeLabelsCodexPreflightAndQueueAccess()
    {
        var events = new List<string>();
        var projects = new[] { Plan("A", events, missing: [new RequiredGitHubLabel("ready", "123456", "ready")]),
            Plan("B", events, githubError: new WorkerInfrastructureException("dependency API unavailable")) };

        var failure = await Assert.ThrowsAsync<WorkerInfrastructureException>(() => StartupCoordinator.RunAsync(projects, CancellationToken.None));

        Assert.Contains("dependency API unavailable", failure.Message);
        Assert.Equal(new[] { "validate:A", "validate:B", "capabilities:A", "capabilities:B" }, events);
        Assert.DoesNotContain(events, item => item.StartsWith("query:", StringComparison.Ordinal) ||
            item.StartsWith("create:", StringComparison.Ordinal) || item.StartsWith("initialize:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LabelCreationFailureIsFatalAndStopsBeforeInitialization()
    {
        var events = new List<string>();
        var projects = new[]
        {
            Plan("A", events, missing: [new RequiredGitHubLabel("a-ready", "123456", "ready")], createError: new WorkerInfrastructureException("CLI failed"))
        };
        var failure = await Assert.ThrowsAsync<WorkerInfrastructureException>(() => StartupCoordinator.RunAsync(projects, CancellationToken.None));
        Assert.Contains("A", failure.Message);
        Assert.Contains("a-ready", failure.Message);
        Assert.DoesNotContain(events, x => x.StartsWith("initialize:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationDuringLabelMutationIsWrappedAsUncertainInfrastructureFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var plan = Plan("A", [], missing: [new RequiredGitHubLabel("a-ready", "123456", "ready")], createError: new OperationCanceledException(cancellation.Token));
        var failure = await Assert.ThrowsAsync<WorkerInfrastructureException>(() => StartupCoordinator.RunAsync([plan], cancellation.Token));
        Assert.IsType<OperationCanceledException>(failure.InnerException);
    }

    private static ProjectStartupPlan Plan(string name, List<string> events,
        Exception? validateError = null, IReadOnlyList<RequiredGitHubLabel>? missing = null,
        Exception? queryError = null, Exception? createError = null, Exception? githubError = null) => new(
        $"{name}.yml", name,
        _ => { events.Add($"validate:{name}"); return Return(validateError); },
        _ => { events.Add($"capabilities:{name}"); return Return(githubError); },
        _ => { events.Add($"query:{name}"); return queryError is null ? Task.FromResult(missing ?? []) : Task.FromException<IReadOnlyList<RequiredGitHubLabel>>(queryError); },
        (label, _) => { events.Add($"create:{name}:{label.Name}"); return Return(createError); },
        _ => { events.Add($"initialize:{name}"); return Task.CompletedTask; });

    private static Task Return(Exception? exception) => exception is null ? Task.CompletedTask : Task.FromException(exception);
}
