namespace CodexWorker;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>Storage boundary for project definitions. Runtime consumers depend on this contract, not YAML paths.</summary>
public interface IProjectConfigurationProvider
{
    Task<IReadOnlyList<(string Path, WorkerConfiguration Configuration)>> ReadAllAsync(CancellationToken ct);
    Task<string> WriteAsync(WorkerConfiguration configuration, CancellationToken ct);
    Task RemoveAsync(string path, CancellationToken ct);
}

/// <summary>V0.7 provider that persists each project as an atomically replaced local YAML file.</summary>
public sealed class LocalYamlProjectConfigurationProvider(string directory) : IProjectConfigurationProvider
{
    private readonly string _directory = Path.GetFullPath(directory);
    private readonly ISerializer _serializer = new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

    public Task<IReadOnlyList<(string Path, WorkerConfiguration Configuration)>> ReadAllAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_directory);
        var files = Directory.EnumerateFiles(_directory).Where(x => Path.GetExtension(x).Equals(".yml", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(x).Equals(".yaml", StringComparison.OrdinalIgnoreCase)).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
        var projects = files.Select(file => (Path.GetFullPath(file), WorkerConfiguration.Load(file))).ToArray();
        ProjectConfigurationDiscovery.ValidateSet(projects);
        return Task.FromResult<IReadOnlyList<(string Path, WorkerConfiguration Configuration)>>(projects);
    }

    public async Task<string> WriteAsync(WorkerConfiguration configuration, CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, FileName(configuration.Project.Name));
        var temp = Path.Combine(_directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temp, _serializer.Serialize(configuration), new UTF8Encoding(false), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
            return Path.GetFullPath(path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public Task RemoveAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), _directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Project configuration is outside the configured projects directory.");
        File.Delete(full);
        return Task.CompletedTask;
    }

    private static string FileName(string name)
    {
        var safe = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        if (safe.Length > 48) safe = safe[..48];
        if (safe.Length == 0) safe = "project";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToUpperInvariant())))[..12].ToLowerInvariant();
        return $"{safe}-{digest}.yml";
    }
}

public sealed record ProjectConfigurationView(string Name, string Repository, string Directory,
    GitSettings Git, GitHubSettings GitHub, CodexSettings Codex, string? EnvironmentFile,
    ValidationSettings Validation, WorkerSettings Worker);

/// <summary>Serializes project mutations, validates the complete resulting set, and guards active removals.</summary>
public sealed class ProjectConfigurationService(IProjectConfigurationProvider provider, ExecutionHistoryStore history, string configurationDirectory,
    ProjectRuntimeRegistry? runtimeRegistry = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<ProjectConfigurationView>> ListAsync(CancellationToken ct)
    {
        var projects = await provider.ReadAllAsync(ct);
        return projects.Select(x => ToView(x.Configuration)).ToArray();
    }

    public async Task<ProjectConfigurationView?> GetAsync(string name, CancellationToken ct)
    {
        var projects = await provider.ReadAllAsync(ct);
        var item = projects.FirstOrDefault(x => string.Equals(x.Configuration.Project.Name, name, StringComparison.OrdinalIgnoreCase));
        return item.Configuration is null ? null : ToView(item.Configuration);
    }

    /// <summary>Validates the complete on-disk set before atomically replacing the active configuration snapshot.</summary>
    public async Task<IReadOnlyList<ProjectConfigurationView>> ReloadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var candidate = await provider.ReadAllAsync(ct);
            ProjectConfigurationDiscovery.ValidateSet(candidate);
            runtimeRegistry?.ReplaceConfiguration(candidate);
            runtimeRegistry?.Publish("configuration.reloaded", "Project configuration reloaded.");
            return candidate.Select(x => ToView(x.Configuration)).ToArray();
        }
        catch (Exception)
        {
            runtimeRegistry?.Publish("configuration.reload.rejected", "Project configuration reload was rejected.");
            throw;
        }
        finally { _gate.Release(); }
    }

    public Task<ProjectConfigurationView> CreateAsync(WorkerConfiguration configuration, CancellationToken ct) => MutateAsync(null, configuration, ct);
    public Task<ProjectConfigurationView> UpdateAsync(string name, WorkerConfiguration configuration, CancellationToken ct) => MutateAsync(name, configuration, ct);

    public async Task<bool> RemoveAsync(string name, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var projects = await provider.ReadAllAsync(ct);
            var item = projects.FirstOrDefault(x => string.Equals(x.Configuration.Project.Name, name, StringComparison.OrdinalIgnoreCase));
            if (item.Configuration is null) return false;
            if ((await history.ReadActiveAsync(ct)).Any(x => string.Equals(x.Project, name, StringComparison.OrdinalIgnoreCase)))
                throw new ProjectConfigurationConflictException($"Project '{name}' has an active execution and cannot be removed.");
            var remaining = projects.Where(x => !string.Equals(x.Path, item.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
            var removalReserved = runtimeRegistry is null || runtimeRegistry.TryBeginRemoval(name);
            if (!removalReserved)
                throw new ProjectConfigurationConflictException($"Project '{name}' has an active execution or is already being removed.");
            try
            {
                if (remaining.Length == 0) throw new InvalidDataException("At least one project configuration must remain.");
                await provider.RemoveAsync(item.Path, ct);
                if (runtimeRegistry is not null) runtimeRegistry.CompleteRemoval(name);
                runtimeRegistry?.Publish("configuration.reloaded", "Project configuration reloaded.", name);
                return true;
            }
            catch
            {
                runtimeRegistry?.CancelRemoval(name);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<ProjectConfigurationView> MutateAsync(string? existingName, WorkerConfiguration configuration, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var projects = await provider.ReadAllAsync(ct);
            var existing = existingName is null ? default : projects.FirstOrDefault(x => string.Equals(x.Configuration.Project.Name, existingName, StringComparison.OrdinalIgnoreCase));
            if (existingName is not null && existing.Configuration is null) throw new KeyNotFoundException($"Project '{existingName}' was not found.");
            var active = existingName is not null && (await history.ReadActiveAsync(ct)).Any(x => string.Equals(x.Project, existingName, StringComparison.OrdinalIgnoreCase));
            var oldPath = existingName is null ? null : existing.Path;
            configuration.ResolvePaths(Path.Combine(Path.GetFullPath(configurationDirectory), "candidate.yml"));
            var candidatePath = oldPath ?? Path.Combine(Path.GetFullPath(configurationDirectory), "candidate.yml");
            ProjectConfigurationDiscovery.ValidateCandidate(candidatePath, configuration, projects, oldPath);
            if (active && existing.Configuration is { } previous &&
                (!string.Equals(previous.Project.Repository, configuration.Project.Repository, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(Path.GetFullPath(previous.Project.Directory), Path.GetFullPath(configuration.Project.Directory), StringComparison.OrdinalIgnoreCase)))
                throw new ProjectConfigurationConflictException($"Project '{existingName}' has an active execution and cannot change repository or checkout.");
            var path = await provider.WriteAsync(configuration, ct);
            if (oldPath is not null && !string.Equals(oldPath, path, StringComparison.OrdinalIgnoreCase)) await provider.RemoveAsync(oldPath, ct);
            await RefreshRuntimeAsync(ct, configuration.Project.Name);
            return ToView(configuration);
        }
        finally { _gate.Release(); }
    }

    private async Task RefreshRuntimeAsync(CancellationToken ct, string? project)
    {
        if (runtimeRegistry is null) return;
        var updated = await provider.ReadAllAsync(ct);
        runtimeRegistry.ReplaceConfiguration(updated);
        runtimeRegistry.Publish("configuration.reloaded", "Project configuration reloaded.", project);
    }

    private static ProjectConfigurationView ToView(WorkerConfiguration config) => new(config.Project.Name,
        config.Project.Repository, config.Project.Directory, config.Git, config.GitHub, config.Codex,
        config.Environment.File, config.Validation, config.Worker);
}

public sealed class ProjectConfigurationConflictException(string message) : Exception(message);
