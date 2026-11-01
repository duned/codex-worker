namespace CodexWorker.Tests;

public sealed class ClaudeReadinessTests
{
    [Theory]
    [InlineData("success", "ready")]
    [InlineData("auth-error", "unauthenticated")]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task NativeProcessFixtureChecksAuthenticationBeforeLiveReadiness(string mode, string expected)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var cli = new FakeClaudeCli(mode);
        var readiness = new ClaudeReadiness((_, arguments, cwd, timeout, ct) =>
            cli.RunAsync(arguments.ToArray(), cwd, "", new Dictionary<string, string?>(),
                timeout ?? TimeSpan.FromSeconds(5), ct));
        await readiness.RefreshAsync(CancellationToken.None);
        Assert.Equal(expected, readiness.Observation.Scope);
        Assert.NotNull(readiness.Observation.CheckedAtUtc);
        Assert.Equal(0, cli.Invocations);
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Trait("ValidationProfile", "FastCandidate")]
    [Theory]
    [InlineData(false, false, "unauthenticated", 2)]
    [InlineData(true, false, "service-unavailable", 4)]
    [InlineData(true, true, "ready", 4)]
    public async Task ReadinessRequiresAuthenticatedLiveResponse(bool authenticated, bool serviceReady, string expected, int calls)
    {
        var count = 0;
        var readiness = new ClaudeReadiness((_, arguments, _, timeout, _) =>
        {
            Assert.NotNull(timeout);
            count++;
            var args = arguments.ToArray();
            if (args[0] == "--print")
            {
                Assert.Contains("--restricted", args);
                Assert.Contains("--safe-mode", args);
                Assert.Equal("dontAsk", args[Array.IndexOf(args, "--permission-mode") + 1]);
                Assert.Equal("none", args[Array.IndexOf(args, "--permission-prompts") + 1]);
                Assert.Equal("sonnet", args[Array.IndexOf(args, "--model") + 1]);
                Assert.Equal("high", args[Array.IndexOf(args, "--effort") + 1]);
                Assert.Equal("{\"disableAllHooks\":true}", args[Array.IndexOf(args, "--settings") + 1]);
                Assert.Equal("", args[Array.IndexOf(args, "--tools") + 1]);
            }
            var result = args[0] switch
            {
                "--version" => new ProcessResult(0, "2.1.259", ""),
                "--help" => new ProcessResult(0, "--restricted --safe-mode --permission-prompts --permission-mode --json-schema --effort --model --tools --allowedTools --strict-mcp-config --no-session-persistence", ""),
                "auth" => new ProcessResult(0, authenticated ? "{\"loggedIn\":true}" : "{\"loggedIn\":false}", ""),
                _ => new ProcessResult(serviceReady ? 0 : 1, "{\"type\":\"result\",\"is_error\":false,\"result\":\"OK\"}", "secret")
            };
            return Task.FromResult(result);
        });
        await readiness.RefreshAsync(CancellationToken.None);
        Assert.Equal(expected, readiness.Observation.Scope);
        Assert.Equal("2.1.259", readiness.Observation.Version);
        Assert.NotNull(readiness.Observation.CheckedAtUtc);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(readiness.Observation));
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
    [Theory]
    [InlineData("2.1.0", 1)]
    [InlineData("2.1.259", 3)]
    public async Task UnsupportedPermissionBoundaryIsNotReady(string version, int expectedCalls)
    {
        var calls = 0;
        var readiness = new ClaudeReadiness((_, arguments, _, _, _) =>
        {
            calls++;
            return Task.FromResult(arguments.First() switch
            {
                "--version" => new ProcessResult(0, version, ""),
                "auth" => new ProcessResult(0, "{\"loggedIn\":true}", ""),
                _ => new ProcessResult(0, "private-output-without-required-flags", "")
            });
        });
        await readiness.RefreshAsync(CancellationToken.None);
        Assert.Equal("incompatible", readiness.Observation.Scope);
        Assert.Equal(expectedCalls, calls);
        Assert.DoesNotContain("private-output", System.Text.Json.JsonSerializer.Serialize(readiness.Observation));
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
