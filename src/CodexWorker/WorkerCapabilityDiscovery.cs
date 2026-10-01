namespace CodexWorker;

using System.Text.RegularExpressions;

/// <summary>Discovers a bounded, safe summary of tools available in the worker environment.</summary>
public sealed class WorkerCapabilityDiscovery
{
    private static readonly WorkerCapabilityDiscovery SharedDiscovery = new();
    private static readonly (string Type, string Name, string Executable, string[] Arguments)[] Tools =
    [
        ("runtime", "dotnet", "dotnet", ["--version"]),
        ("runtime", "node", "node", ["--version"]),
        ("tool", "git", "git", ["--version"]),
        ("tool", "github-cli", "gh", ["--version"]),
        ("tool", "docker", "docker", ["--version"]),
        ("tool", "postgresql", "psql", ["--version"]),
        ("tool", "codex-cli", "codex", ["--version"])
    ];

    private readonly Func<string, IEnumerable<string>, string, TimeSpan?, CancellationToken, Task<ProcessResult>> _run;
    private readonly TimeSpan _refreshInterval;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private IReadOnlyList<WorkerCapabilityContract>? _cachedCapabilities;
    private DateTimeOffset _cachedAtUtc;

    internal static WorkerCapabilityDiscovery Shared => SharedDiscovery;

    public WorkerCapabilityDiscovery()
        : this((executable, arguments, directory, timeout, cancellationToken) =>
            new ProcessRunner().RunAsync(executable, arguments, directory, timeout, cancellationToken), TimeSpan.FromMinutes(5))
    {
    }

    public WorkerCapabilityDiscovery(
        Func<string, IEnumerable<string>, string, TimeSpan?, CancellationToken, Task<ProcessResult>> run,
        TimeSpan? refreshInterval = null)
    {
        _run = run;
        _refreshInterval = refreshInterval ?? TimeSpan.FromMinutes(5);
    }

    public async Task<IReadOnlyList<WorkerCapabilityContract>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var capabilities = new List<WorkerCapabilityContract>
        {
            new("integration", "github-issues")
        };
        foreach (var (type, name, executable, arguments) in Tools)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await _run(name == "codex-cli" ? CodexProvisioning.CodexServiceEnvironment.Executable : executable, arguments, Environment.CurrentDirectory,
                    TimeSpan.FromSeconds(3), cancellationToken);
                if (result.ExitCode != ProcessExitCodes.Success) continue;
                var version = ParseVersion(result.StandardOutput) ?? ParseVersion(result.StandardError);
                if (version is not null) capabilities.Add(new WorkerCapabilityContract(type, name, version));
                else capabilities.Add(new WorkerCapabilityContract(type, name));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsUnavailableToolFailure(ex))
            {
                // Unavailable or unresponsive optional tools do not prevent worker startup.
            }
        }
        return capabilities;
    }

    public async Task<IReadOnlyList<WorkerCapabilityContract>> GetCachedAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedCapabilities is not null && DateTimeOffset.UtcNow - _cachedAtUtc < _refreshInterval)
            return _cachedCapabilities;
        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedCapabilities is null || DateTimeOffset.UtcNow - _cachedAtUtc >= _refreshInterval)
            {
                _cachedCapabilities = await DiscoverAsync(cancellationToken);
                _cachedAtUtc = DateTimeOffset.UtcNow;
            }
            return _cachedCapabilities;
        }
        finally { _cacheLock.Release(); }
    }

    public async Task<IReadOnlyList<WorkerCapabilityContract>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            _cachedCapabilities = await DiscoverAsync(cancellationToken);
            _cachedAtUtc = DateTimeOffset.UtcNow;
            return _cachedCapabilities;
        }
        finally { _cacheLock.Release(); }
    }

    private static bool IsUnavailableToolFailure(Exception exception) => exception is
        System.ComponentModel.Win32Exception or FileNotFoundException or DirectoryNotFoundException or ProcessTimeoutException ||
        exception is InvalidOperationException && exception.Message.StartsWith("Could not start", StringComparison.Ordinal);

    public static string? ParseVersion(string output)
    {
        var match = Regex.Match(output, @"(?<![\w])v?(\d+(?:\.\d+){0,3}(?:[-+][0-9A-Za-z.-]+)?)(?![\w])", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }
}
