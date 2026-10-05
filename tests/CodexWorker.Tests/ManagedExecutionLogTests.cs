using CodexWorker;

namespace CodexWorker.Tests;

public sealed class ManagedExecutionLogTests
{
    [Fact]
    public void AssignmentFieldsAndDiagnosticsAreBoundedRedactedAndSingleLine()
    {
        var now = DateTimeOffset.UnixEpoch;
        var assignment = new WorkerAssignmentContract("assignment\nforged-line", "server-execution",
            new ServerProjectContract("project-id", "Project private-test-value\nforged-line" + new string('x', 2000),
                "owner/repo", "main", "body must never appear", [], 1, now, now),
            new ServerWorkReferenceContract("github-issue", "17", "https://user:password@host/unsafe"),
            "worker-id", new Dictionary<string, string> { ["prompt"] = "prompt must never appear" },
            new ServerExecutionLeaseContract("server-execution", "worker-id", 7, now, now.AddMinutes(5), "Active"));

        var message = ManagedExecutionLog.Assignment(assignment,
            "failure private-test-value token=unsafe-value\n" + new string('x', 2000), ["private-test-value"], includeCorrelation: true);

        var transition = ManagedExecutionLog.Assignment(assignment, "revision 1 verified · project ready");
        Assert.StartsWith("Managed · assignment [", transition, StringComparison.Ordinal);
        Assert.EndsWith("revision 1 verified · project ready", transition, StringComparison.Ordinal);
        Assert.DoesNotContain("Server execution", transition, StringComparison.Ordinal);
        Assert.DoesNotContain("lease generation", transition, StringComparison.Ordinal);
        Assert.True(transition.Length < 100);

        Assert.Contains("project project-id/Project [redacted]", message, StringComparison.Ordinal);
        Assert.Contains("work github-issue #17", message, StringComparison.Ordinal);
        Assert.Contains("Server execution server-execution · lease generation 7", message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-test-value", message, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-value", message, StringComparison.Ordinal);
        Assert.DoesNotContain("body must never appear", message, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt must never appear", message, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', message);
        Assert.True(message.Length < 1800);
    }
}
