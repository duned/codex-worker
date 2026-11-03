using System.Threading.Channels;

namespace CodexWorker;

/// <summary>Coalesces local YAML file notifications and applies only complete, validated configuration sets.</summary>
public sealed class ProjectConfigurationWatcher : IAsyncDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly ProjectConfigurationService _service;
    private readonly ProjectRuntimeRegistry _registry;
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;
    private readonly TimeSpan _debounce;

    public ProjectConfigurationWatcher(string directory, ProjectConfigurationService service, ProjectRuntimeRegistry registry,
        TimeSpan? debounce = null)
    {
        _service = service;
        _registry = registry;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(300);
        _watcher = new FileSystemWatcher(Path.GetFullPath(directory))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = false
        };
        _watcher.Changed += Changed;
        _watcher.Created += Changed;
        _watcher.Deleted += Changed;
        _watcher.Renamed += Renamed;
        _watcher.Error += Error;
        _watcher.EnableRaisingEvents = true;
        _loop = RunAsync(_shutdown.Token);
    }

    private void Changed(object sender, FileSystemEventArgs args)
    {
        if (IsProjectYaml(args.FullPath)) _changes.Writer.TryWrite(true);
    }

    private void Renamed(object sender, RenamedEventArgs args)
    {
        if (IsProjectYaml(args.FullPath) || IsProjectYaml(args.OldFullPath)) _changes.Writer.TryWrite(true);
    }

    private void Error(object sender, ErrorEventArgs args) => _changes.Writer.TryWrite(true);

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (await _changes.Reader.WaitToReadAsync(ct))
            {
                while (_changes.Reader.TryRead(out _)) { }
                await Task.Delay(_debounce, ct);
                while (_changes.Reader.TryRead(out _)) { }
                await ReloadAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    internal async Task ReloadAsync(CancellationToken ct)
    {
        try { await _service.ReloadAsync(ct, _registry); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // The service has already published this invocation's rejection.
            // Keep watching so a later valid configuration can be accepted.
        }
    }

    private static bool IsProjectYaml(string path) =>
        Path.GetExtension(path).Equals(".yml", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".yaml", StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _changes.Writer.TryComplete();
        _shutdown.Cancel();
        try { await _loop; }
        catch (OperationCanceledException) { }
        _shutdown.Dispose();
    }
}
