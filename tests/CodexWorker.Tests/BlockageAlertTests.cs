namespace CodexWorker.Tests;

using System.Net;
using System.Text.Json;

public sealed class BlockageAlertTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-24T10:00:00Z");
    private static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Trait("ValidationProfile", "FastCandidate")]
    [Fact]
    public void LastingBlockagesAlertOnceAndTheirRecoveryOnce()
    {
        var state = new BlockageAlertState(TimeSpan.FromMinutes(15));
        var paused = new Dictionary<string, string> { ["Project"] = "Completion reconciliation required." };

        Assert.Empty(state.Observe(Start, "Codex preflight failed.", paused));
        Assert.Empty(state.Observe(Start.AddMinutes(14), "Codex preflight failed.", paused));
        var alerts = state.Observe(Start.AddMinutes(15), "Codex preflight failed.", paused);
        Assert.Equal([(null, false), ("Project", false)], alerts.Select(alert => (alert.Project, alert.Recovered)));
        Assert.All(alerts, alert => Assert.Equal(TimeSpan.FromMinutes(15), alert.Duration));
        Assert.Empty(state.Observe(Start.AddMinutes(40), "Codex preflight failed.", paused));

        var recovered = Assert.Single(state.Observe(Start.AddMinutes(45), null, paused));
        Assert.Equal((null, true, TimeSpan.FromMinutes(45)), (recovered.Project, recovered.Recovered, recovered.Duration));
        Assert.Equal("Project", Assert.Single(state.Observe(Start.AddMinutes(50), null, None)).Project);
        Assert.Empty(state.Observe(Start.AddMinutes(51), null, None));
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void ShortInterruptionsAndADisabledThresholdStaySilent()
    {
        var state = new BlockageAlertState(TimeSpan.FromMinutes(15));
        Assert.Empty(state.Observe(Start, "not ready", None));
        Assert.Empty(state.Observe(Start.AddMinutes(10), null, None));
        Assert.Empty(state.Observe(Start.AddMinutes(20), "not ready", None));
        Assert.Empty(state.Observe(Start.AddMinutes(30), "not ready", None));
        Assert.Single(state.Observe(Start.AddMinutes(35), "not ready", None));

        var disabled = new BlockageAlertState(TimeSpan.Zero);
        Assert.Empty(disabled.Observe(Start, "not ready", None));
        Assert.Empty(disabled.Observe(Start.AddDays(1), "not ready", None));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task AlertsNameTheWorkerProjectDurationAndReason()
    {
        using var handler = new RecordingTelegram();
        using var client = new HttpClient(handler);
        using var notifier = new TelegramNotifier(true, "123:token", "-100", client, workerName: "node");

        await notifier.BlockageAsync(new("Project", "GitHub failed\nduring claim.", TimeSpan.FromMinutes(20), false), CancellationToken.None);
        await notifier.BlockageAsync(new(null, "Codex preflight failed.", TimeSpan.FromHours(2), true), CancellationToken.None);

        Assert.Equal([
            $"🟠 node {ApplicationVersion.Display} · PROYECTO PAUSADO\nProyecto: Project\nDesde hace 20:00\nGitHub failed during claim.",
            $"🟢 node {ApplicationVersion.Display} · VUELVE A ACEPTAR TRABAJO\nTras 02:00:00 sin aceptar trabajo"], handler.Messages);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData("", 15)]
    [InlineData("  blockedAlertMinutes: 0\n", 0)]
    [InlineData("  blockedAlertMinutes: 60\n", 60)]
    [InlineData("  blockedAlertMinutes: -1\n", null)]
    [InlineData("  blockedAlertMinutes: 1441\n", null)]
    public void ThresholdIsConfiguredAndBounded(string setting, int? expected)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"worker-blockage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "worker.yml");
            File.WriteAllText(path, $"projects:\n  directory: ./projects\ntelegram:\n  enabled: false\n{setting}");
            if (expected is { } minutes) Assert.Equal(minutes, GlobalWorkerConfiguration.Load(path).Telegram.BlockedAlertMinutes);
            else Assert.Contains("telegram.blockedAlertMinutes", Assert.Throws<InvalidDataException>(() => GlobalWorkerConfiguration.Load(path)).Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private sealed class RecordingTelegram : HttpMessageHandler
    {
        public List<string> Messages { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await (request.Content ?? throw new InvalidDataException("Missing body.")).ReadAsStringAsync(cancellationToken));
            Messages.Add(body.RootElement.GetProperty("text").GetString() ?? "");
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
        }
    }
}
