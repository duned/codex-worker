using CodexServer;
using CodexWorker;

namespace CodexWorker.Tests;

public sealed class AgentAuthenticationTests
{
    [Fact]
    public async Task CodexProviderReportsReadyAndMissingAuthenticationWithoutReturningCliOutput()
    {
        var ready = new CodexAgentProvider((_, arguments, _, _, _) =>
        {
            Assert.Equal(new[] { "login", "status" }, arguments);
            return Task.FromResult(new ProcessResult(0, "Logged in using ChatGPT", ""));
        });
        var missing = new CodexAgentProvider((_, _, _, _, _) =>
            Task.FromResult(new ProcessResult(1, "", "Not logged in. private-token-marker")));

        Assert.Equal(new AgentAuthenticationContract("codex", "ready"),
            await ready.CheckAuthenticationAsync(CancellationToken.None));
        var state = await missing.CheckAuthenticationAsync(CancellationToken.None);
        Assert.Equal(new AgentAuthenticationContract("codex", "authentication-required"), state);
        Assert.DoesNotContain("private-token-marker", state.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodexProviderDistinguishesUnavailableAndUncertainChecks()
    {
        var unavailable = new CodexAgentProvider((_, _, _, _, _) =>
            throw new FileNotFoundException("codex missing"));
        var uncertain = new CodexAgentProvider((_, _, _, _, _) =>
            Task.FromResult(new ProcessResult(1, "", "network error")));

        Assert.Equal("unsupported", (await unavailable.CheckAuthenticationAsync(CancellationToken.None)).State);
        Assert.Equal("unknown", (await uncertain.CheckAuthenticationAsync(CancellationToken.None)).State);
    }

    [Theory]
    [InlineData("codex", "ready", true)]
    [InlineData("codex", "authentication-required", false)]
    [InlineData("codex", "unsupported", false)]
    [InlineData("codex", "invalid-or-expired", false)]
    [InlineData("codex", "unknown", false)]
    [InlineData("future-agent", "ready", false)]
    public void ServerUsesReportedConfiguredProviderReadiness(string provider, string state, bool eligible) =>
        Assert.Equal(eligible, WorkerAgentEligibility.CanExecute([new WorkerAgentAuthentication(provider, state)]));

    [Fact]
    public void ServerRetainsCompatibilityWithOlderWorkersWithoutAgentState() =>
        Assert.True(WorkerAgentEligibility.CanExecute(null));

    [Fact]
    public void AuthenticationStateContainsNoCredentialField()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new AgentAuthenticationContract("codex", "ready"));
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CodexProviderMaterializesGenericApiKeyCredentialOnlyIntoChildEnvironment()
    {
        var provider = new CodexAgentProvider((_, _, _, _, _) =>
            Task.FromResult(new ProcessResult(0, "Logged in", "")));
        const string sentinel = "non-secret-test-sentinel";
        var environment = provider.MaterializeCredential(
            new WorkerCredentialContract("credential-id", "codex", "api-key", 1, sentinel));

        Assert.Equal(sentinel, environment["OPENAI_API_KEY"]);
        Assert.DoesNotContain(sentinel, environment.ToString(), StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => provider.MaterializeCredential(
            new WorkerCredentialContract("credential-id", "codex", "chatgpt-session", 1, "session")));
    }
}
