namespace CodexWorker;

using System.Runtime.InteropServices;
using System.Text.Json;

/// <summary>Stable, local-only snapshot returned by the status command.</summary>
public sealed record WorkerStatusDocument(
    int ContractVersion,
    string WorkerVersion,
    string Platform,
    WorkerStatusConfiguration Configuration,
    WorkerStatusRegistration Registration,
    WorkerStatusOperation Operation,
    WorkerStatusCapacity Capacity,
    WorkerStatusProvisioning Provisioning,
    IReadOnlyList<WorkerStatusCapability> Capabilities,
    IReadOnlyList<string> Diagnostics);

public sealed record WorkerStatusConfiguration(string Validity, string Path, int? ProjectCount, string? Ownership,
    string? ServerConfigured, string? DiagnosticCode);
public sealed record WorkerStatusRegistration(string State, string ServerConfiguration, string ServerConnectivity,
    string? IdentityFile);
public sealed record WorkerStatusOperation(string Lifecycle, string Readiness, string ObservationScope);
public sealed record WorkerStatusCapacity(int? Maximum, int? Active, string ObservationScope);
public sealed record WorkerStatusProvisioning(bool Enabled, bool AllowNonPrivileged, bool AllowCredentials,
    int AllowedPrivilegedActionCount, int DeniedActionCount);
public sealed record WorkerStatusCapability(string Type, string Name, string State, string? Version, string? DiagnosticCode);

public static class WorkerStatusReporter
{
    private static readonly string[] RequiredTools = ["git", "github-cli", "codex-cli"];

    public static async Task<WorkerStatusDocument> CreateAsync(string configurationPath,
        WorkerCapabilityDiscovery? discovery = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        var fullPath = Path.GetFullPath(configurationPath);
        GlobalWorkerConfiguration? configuration = null;
        IReadOnlyList<(string Path, WorkerConfiguration Configuration)> projects = [];
        string? diagnostic = null;
        try
        {
            configuration = GlobalWorkerConfiguration.Load(fullPath);
            projects = ProjectConfigurationDiscovery.Load(configuration.Projects.Directory,
                allowEmpty: configuration.Server.Enabled && configuration.Projects.Ownership == "managed",
                validateExecutionResources: false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            configuration = null;
            projects = [];
            diagnostic = ex is FileNotFoundException or DirectoryNotFoundException ? "configuration-not-found" : "configuration-invalid";
        }

        var found = await (discovery ?? new WorkerCapabilityDiscovery()).DiscoverAsync(cancellationToken);
        var capabilities = found.Select(capability => new WorkerStatusCapability(capability.Type, capability.Name,
                "available", capability.Version, null)).ToList();
        var foundNames = found.Select(capability => capability.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "dotnet", "node", "git", "github-cli", "docker", "postgresql", "codex-cli" })
            if (!foundNames.Contains(name))
                capabilities.Add(new WorkerStatusCapability(name is "dotnet" or "node" ? "runtime" : "tool", name,
                    "missing", null, "tool-missing"));
        var missingRequired = RequiredTools.Where(name => !foundNames.Contains(name)).ToArray();
        var locallyReady = configuration is not null && missingRequired.Length == 0;
        var identityPath = configuration is null ? null : configuration.Server.IdentityFile ?? WorkerIdentity.DefaultPath;
        var identityPresent = identityPath is not null && File.Exists(identityPath);
        var provisioning = configuration?.Worker.Provisioning;
        var diagnostics = new List<string>();
        if (diagnostic is not null) diagnostics.Add(diagnostic);
        diagnostics.AddRange(missingRequired.Select(name => $"{name}-unavailable"));
        if (configuration?.Projects.Ownership == "managed" && !identityPresent) diagnostics.Add("worker-identity-missing");
        if (locallyReady && configuration?.Projects.Ownership == "managed") diagnostics.Add("server-state-unverified");
        if (!locallyReady && diagnostics.Count == 0) diagnostics.Add("worker-not-ready");

        return new WorkerStatusDocument(1, ApplicationVersion.Display, RuntimeInformation.OSDescription,
            new WorkerStatusConfiguration(configuration is null ? "invalid" : "valid", fullPath,
                configuration is null ? null : projects.Count, configuration?.Projects.Ownership,
                configuration is null ? null : configuration.Server.Enabled ? "configured" : "disabled", diagnostic),
            new WorkerStatusRegistration(configuration is null ? "unknown" : configuration.Server.Enabled
                    ? identityPresent ? "local-identity-present" : "local-identity-missing" : "standalone",
                configuration is null ? "unknown" : configuration.Server.Enabled ? "configured" : "disabled",
                configuration is null ? "unknown" : configuration.Server.Enabled ? "not-checked" : "not-applicable",
                identityPath),
            new WorkerStatusOperation("unknown", !locallyReady ? "not-ready" :
                configuration?.Projects.Ownership == "managed" ? "server-dependent-unverified" : "local-prerequisites-present", "local-observation-only"),
            new WorkerStatusCapacity(configuration?.Worker.MaxParallelTasks, null, "active-count-not-available-outside-running-worker"),
            new WorkerStatusProvisioning(provisioning?.Enabled ?? false, provisioning?.AllowNonPrivileged ?? false,
                provisioning?.AllowCredentials ?? false, provisioning?.AllowedPrivilegedActions.Count ?? 0,
                provisioning?.DeniedActions.Count ?? 0), capabilities, diagnostics);
    }

    public static void Write(WorkerStatusDocument status, bool json, TextWriter? writer = null)
    {
        writer ??= Console.Out;
        if (json)
        {
            writer.WriteLine(JsonSerializer.Serialize(status, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
            return;
        }

        writer.WriteLine($"Worker {status.WorkerVersion} ({status.Platform})");
        writer.WriteLine($"Configuration: {status.Configuration.Validity} ({status.Configuration.Path})");
        if (status.Configuration.ProjectCount is int projectCount)
            writer.WriteLine($"Projects: {projectCount}; ownership: {status.Configuration.Ownership}");
        writer.WriteLine($"Server: {status.Registration.ServerConfiguration}; connectivity: {status.Registration.ServerConnectivity}; registration: {status.Registration.State}");
        writer.WriteLine($"Lifecycle: {status.Operation.Lifecycle}; readiness: {status.Operation.Readiness} ({status.Operation.ObservationScope})");
        writer.WriteLine($"Capacity: {status.Capacity.Active?.ToString() ?? "unknown"}/{status.Capacity.Maximum?.ToString() ?? "unknown"}");
        writer.WriteLine($"Provisioning: {(status.Provisioning.Enabled ? "enabled" : "disabled")}; non-privileged: {status.Provisioning.AllowNonPrivileged}; credentials: {status.Provisioning.AllowCredentials}; allow rules: {status.Provisioning.AllowedPrivilegedActionCount}; deny rules: {status.Provisioning.DeniedActionCount}");
        writer.WriteLine("Capabilities:");
        foreach (var capability in status.Capabilities)
            writer.WriteLine($"  {capability.Type}/{capability.Name}: {capability.State}{(capability.Version is null ? "" : $" ({capability.Version})")}");
        foreach (var diagnostic in status.Diagnostics)
            writer.WriteLine($"Diagnostic: {DiagnosticText(diagnostic)}");
    }

    private static string DiagnosticText(string code) => code switch
    {
        "configuration-not-found" => "Configuration file was not found; pass --config with a valid worker configuration path.",
        "configuration-invalid" => "Configuration could not be validated; run 'codex-worker config validate' for details.",
        "worker-identity-missing" => "Managed mode has no local Worker identity file; register this Worker or restore its identity before starting it.",
        "server-state-unverified" => "Local tools are present; Server connectivity, managed configuration, and execution readiness were not checked.",
        "git-unavailable" => "Git is unavailable; install Git and ensure it is on PATH.",
        "github-cli-unavailable" => "GitHub CLI is unavailable; install gh and configure authentication.",
        "codex-cli-unavailable" => "Codex CLI is unavailable; install Codex and complete its authentication setup.",
        _ => "Worker is not ready based on local observations."
    };
}
