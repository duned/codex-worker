using CodexProvisioning;

namespace CodexWorker.Tests;

public sealed class CodexInterruptionTests
{
    [Theory]
    [InlineData("You've hit your usage limit", "Codex usage limit", true)]
    [InlineData("rate_limit_exceeded", "Codex usage limit", true)]
    [InlineData("401 Unauthorized token=secret-value", "Codex authentication", true)]
    [InlineData("Failed to refresh token", "Codex authentication", true)]
    [InlineData("error sending request: connection reset", "Codex service/network", true)]
    [InlineData("503 Service Unavailable", "Codex service/network", true)]
    [InlineData("process exited 1", "Codex CLI/process failure", false)]
    public void ClassificationUsesEvidenceAndUnknownFailuresRequireInspection(string evidence, string category, bool recoverable)
    {
        var failure = CodexFailure.Classify(evidence);
        Assert.Equal(category, failure.Category);
        Assert.Equal(recoverable, failure.Recoverable);
        Assert.DoesNotContain("secret-value", failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentOutputAndEchoedPromptDoNotAuthorizeInfrastructureResume()
    {
        var evidence = CodexFailure.ProcessEvidence("{\"type\":\"item.completed\",\"item\":{\"text\":\"usage limit reached\"}}", "Task: investigate usage limits");
        Assert.False(CodexFailure.Classify(evidence).Recoverable);
        Assert.Equal("Codex usage limit", CodexFailure.Classify(CodexFailure.ProcessEvidence(
            "{\"type\":\"turn.failed\",\"error\":{\"message\":\"You've hit your usage limit\"}}", "")).Category);
    }

    [Fact]
    public void TimeoutAndDiagnosticsAreBoundedAndSanitized()
    {
        Assert.Equal("Codex timeout", CodexFailure.Classify("", timedOut: true).Category);
        var evidence = CodexFailure.Evidence("source fragments that do not explain the failure", "error token=private-value usage limit\nerror " + new string('x', 4000));
        Assert.DoesNotContain("private-value", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("source fragments", evidence, StringComparison.Ordinal);
        Assert.True(evidence.Length < 2100);
    }

    [Fact]
    public void SessionIdentityComesOnlyFromValidatedThreadStartedEvent()
    {
        var id = Guid.NewGuid().ToString();
        Assert.Equal(id, CodexExecutor.TryReadSession($"{{\"type\":\"thread.started\",\"thread_id\":\"{id}\"}}\n{{partial"));
        Assert.Null(CodexExecutor.TryReadSession("{\"type\":\"thread.started\",\"thread_id\":\"--last\"}"));
        Assert.Null(CodexExecutor.TryReadSession($"{{\"type\":\"item.completed\",\"thread_id\":\"{id}\"}}"));
    }

    [Fact]
    public async Task ReadinessRestoresAutomaticallyWithBackoffAndNoBusyLoop()
    {
        var clock = new Clock();
        var provider = new TestProvider();
        var discovery = new NodeCapabilityDiscovery((_, _, _) => Task.FromResult((0, "1.0.0")));
        var readiness = new ManagedCodexReadiness(provider, clock);
        Assert.False(await readiness.EvaluateAsync(discovery, false, CancellationToken.None));
        Assert.Contains("usage limit", readiness.DiagnosticCode, StringComparison.Ordinal);
        Assert.Contains("automatic preflight at", readiness.DiagnosticCode, StringComparison.Ordinal);
        Assert.DoesNotContain("private-value", readiness.DiagnosticCode, StringComparison.Ordinal);
        for (var i = 0; i < 100; i++) Assert.False(await readiness.EvaluateAsync(discovery, false, CancellationToken.None));
        Assert.Equal(1, provider.Calls);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(await readiness.EvaluateAsync(discovery, false, CancellationToken.None));
        Assert.Equal(2, provider.Calls);
        provider.Ready = true;
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(await readiness.EvaluateAsync(discovery, false, CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(await readiness.EvaluateAsync(discovery, false, CancellationToken.None));
        Assert.Equal(3, provider.Calls);
        Assert.Null(readiness.DiagnosticCode);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class TestProvider : IAgentAuthenticationProvider
    {
        public string Provider => "codex";
        public int Calls { get; private set; }
        public bool Ready { get; set; }
        public Task ValidateAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Ready ? Task.CompletedTask : Task.FromException(new WorkerInfrastructureException("Codex usage limit reached token=private-value"));
        }
    }
}
