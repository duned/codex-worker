using CodexWorker;

namespace CodexWorker.Tests;

public sealed class WorkerAgentCapabilitiesTests
{
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void InventoryRevalidationKeepsAgentReadinessAndWithdrawsRepositoryAuthentication()
    {
        IReadOnlyList<WorkerCapabilityContract> discovered = [new("tool", "git", "2.43.0"), new("tool", "codex", "0.160.0")];
        IReadOnlyList<WorkerCapabilityContract> current =
        [
            new("tool", "git", "2.43.0"),
            new("authentication", "github-api", Scope: "owner/repo"),
            new("authentication", "git-repository", Scope: "owner/repo"),
            WorkerAgentCapabilities.AuthenticatedProvider("codex"),
            new("agent-readiness", "ccc", Scope: "ready")
        ];

        var revalidating = WorkerAgentCapabilities.KeepAgentReadiness(discovered, current);

        Assert.Contains(WorkerAgentCapabilities.AuthenticatedProvider("codex"), revalidating);
        Assert.Contains(new WorkerCapabilityContract("agent-readiness", "ccc", Scope: "ready"), revalidating);
        Assert.Contains(new WorkerCapabilityContract("tool", "codex", "0.160.0"), revalidating);
        Assert.DoesNotContain(revalidating, capability => capability.Type == "authentication");
        Assert.Single(revalidating, capability => capability == new WorkerCapabilityContract("tool", "git", "2.43.0"));
    }
}
