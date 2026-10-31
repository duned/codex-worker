namespace CodexWorker.Tests;

public sealed class ClaudeReadinessTests
{
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Trait("ValidationProfile", "FastCandidate")]
    [Theory]
    [InlineData(false, false, "unauthenticated", 2)]
    [InlineData(true, false, "service-unavailable", 3)]
    [InlineData(true, true, "ready", 3)]
    public async Task ReadinessRequiresAuthenticatedLiveResponse(bool authenticated, bool serviceReady, string expected, int calls)
    {
        var count = 0;
        var readiness = new ClaudeReadiness((_, arguments, _, timeout, _) =>
        {
            Assert.NotNull(timeout);
            count++;
            var args = arguments.ToArray();
            var result = args[0] switch
            {
                "--version" => new ProcessResult(0, "2.1.0", ""),
                "auth" => new ProcessResult(0, authenticated ? "{\"loggedIn\":true}" : "{\"loggedIn\":false}", ""),
                _ => new ProcessResult(serviceReady ? 0 : 1, "{\"type\":\"result\",\"is_error\":false,\"result\":\"OK\"}", "secret")
            };
            return Task.FromResult(result);
        });
        await readiness.RefreshAsync(CancellationToken.None);
        Assert.Equal(expected, readiness.Observation.Scope);
        Assert.Equal(calls, count);
        Assert.Equal("agent-readiness", readiness.Observation.Type);
        // Reading the cached observation does not execute more CLI commands.
        _ = readiness.Observation;
        Assert.Equal(calls, count);
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Trait("ValidationProfile", "FastCandidate")]
    [Fact]
    public async Task ProvisioningRefreshRechecksMissingExecutable()
    {
        var present = false;
        var readiness = new ClaudeReadiness((_, _, _, _, _) => present
            ? Task.FromResult(new ProcessResult(0, "unrecognized", ""))
            : throw new IOException("secret"));
        await readiness.RefreshAsync(CancellationToken.None);
        Assert.Equal("absent", readiness.Observation.Scope);
        present = true;
        await readiness.RefreshAsync(CancellationToken.None);
        Assert.Equal("incompatible", readiness.Observation.Scope);
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Trait("ValidationProfile", "FastCandidate")]
    [Fact]
    public void LiveReadinessDoesNotAuthorizeUnsupportedEngine()
    {
        var result = CodexProvisioning.CapabilityEligibility.Evaluate(
            [new WorkerCapabilityContract("agent-provider", "ccc")],
            [new WorkerCapabilityContract("agent-readiness", "ccc", Scope: "ready")]);
        Assert.False(result.IsEligible);
        Assert.Contains("engine execution unsupported", Assert.Single(result.MissingRequirements));
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Trait("ValidationProfile", "FastCandidate")]
    [Fact]
    public async Task CancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var readiness = new ClaudeReadiness((_, _, _, _, token) => Task.FromCanceled<ProcessResult>(token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readiness.RefreshAsync(cancellation.Token));
    }
}
