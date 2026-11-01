namespace CodexWorker.Tests;

using System.Text.Json;

public sealed class EngineStatusContractTests
{
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void LegacyCompletionReportDefaultsToCodex()
    {
        var report = JsonSerializer.Deserialize<IssueExecutionReport>("{\"ImplementationSummary\":null,\"ValidationRepairs\":[]}");
        Assert.NotNull(report);
        Assert.Equal("codex", report.Engine);
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void ReadinessCapabilityRetainsSafeTimestampAcrossServerWireContract()
    {
        var timestamp = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var observation = new WorkerCapabilityContract("agent-readiness", "ccc", "2.1.259", "unauthenticated", timestamp);
        var payload = JsonSerializer.Serialize(observation, options);
        var server = JsonSerializer.Deserialize<CodexServer.WorkerCapability>(payload, options);
        Assert.NotNull(server);
        Assert.Equal(timestamp, server.CheckedAtUtc);
        Assert.Equal("unauthenticated", server.Scope);
        Assert.Equal("2.1.259", server.Version);
        var roundTrip = JsonSerializer.Deserialize<WorkerCapabilityContract>(JsonSerializer.Serialize(server, options), options);
        Assert.Equal(observation, roundTrip);
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void LegacyReadinessCapabilityHasNoCheckTime()
    {
        var capability = JsonSerializer.Deserialize<CodexServer.WorkerCapability>("{\"type\":\"agent-readiness\",\"name\":\"ccc\",\"scope\":\"absent\"}");
        Assert.NotNull(capability);
        Assert.Null(capability.CheckedAtUtc);
    }
}
