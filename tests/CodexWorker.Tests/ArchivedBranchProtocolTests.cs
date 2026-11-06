namespace CodexWorker.Tests;

using CodexProvisioning;
using Xunit;

public sealed class ArchivedBranchProtocolTests
{
    [Theory]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [InlineData("feature/active")]
    [InlineData("main")]
    [InlineData("done/feature/../main")]
    [InlineData("done/feature/foo.lock")]
    [InlineData("refs/heads/done/feature/foo")]
    public void RejectsUnsafeSelections(string branch)
    {
        Assert.False(ExecutionMaintenanceProtocol.Valid(Request() with { Apply = true, Branches = [new(branch, new string('a', 40))] }));
    }

    [Fact]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public void RequiresExplicitBoundedSelectionsForDeletion()
    {
        var request = Request();
        Assert.True(ExecutionMaintenanceProtocol.Valid(request));
        Assert.False(ExecutionMaintenanceProtocol.Valid(request with { Apply = true }));
        var selected = request with { Apply = true, Branches = [new("complete/feature/task-1", new string('a', 40))] };
        Assert.True(ExecutionMaintenanceProtocol.Valid(selected));
        Assert.False(ExecutionMaintenanceProtocol.Valid(selected with { Branches = [new("done/feature/task-1", "invalid")] }));
        Assert.False(ExecutionMaintenanceProtocol.Valid(selected with { Project = null }));
        Assert.False(ExecutionMaintenanceProtocol.Valid(selected with { Repository = null }));
    }

    private static ExecutionMaintenanceRequest Request() => new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
        null, null, null, null, Action: "archived-branches", Project: "sample", OlderThanDays: 30, Repository: "owner/repo");
}
