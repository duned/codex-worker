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
    IReadOnlyList<string> Diagnostics,
    WorkerStatusRuntime? Runtime = null);

public sealed record WorkerStatusConfiguration(string Validity, string Path, int? ProjectCount, string? Ownership,
    string? ServerConfigured, string? DiagnosticCode);
public sealed record WorkerStatusRegistration(string State, string ServerConfiguration, string ServerConnectivity,
    string? IdentityFile, string? WorkerId = null, string CredentialState = "unknown",
    string? ServerUrl = null, string AssociationSource = "unknown", string ServerAcceptance = "unverified");
public sealed record WorkerStatusRuntime(string Framework, string ProcessArchitecture, string OsArchitecture);
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
        var provisioning = configuration?.Worker.Provisioning;
        var diagnostics = new List<string>();
        if (diagnostic is not null) diagnostics.Add(diagnostic);
        diagnostics.AddRange(missingRequired.Select(name => $"{name}-unavailable"));
        var registration = await ObserveRegistrationAsync(configuration, diagnostics, cancellationToken);
        var locallyReady = configuration is not null && missingRequired.Length == 0 &&
            (configuration.Projects.Ownership != "managed" ||
                registration.State == "local-identity-present" &&
                registration.CredentialState is ("persisted" or "environment") && registration.ServerUrl is not null);
        if (locallyReady && configuration?.Projects.Ownership == "managed") diagnostics.Add("server-state-unverified");
        if (!locallyReady && diagnostics.Count == 0) diagnostics.Add("worker-not-ready");

        return new WorkerStatusDocument(1, ApplicationVersion.Display, RuntimeInformation.OSDescription,
            new WorkerStatusConfiguration(configuration is null ? "invalid" : "valid", fullPath,
                configuration is null ? null : projects.Count, configuration?.Projects.Ownership,
                configuration is null ? null : configuration.Server.Enabled ? "configured" : "disabled", diagnostic),
            registration,
            new WorkerStatusOperation("unknown", !locallyReady ? "not-ready" :
                configuration?.Projects.Ownership == "managed" ? "server-dependent-unverified" : "local-prerequisites-present", "local-observation-only"),
            new WorkerStatusCapacity(configuration?.Worker.MaxParallelTasks, null, "active-count-not-available-outside-running-worker"),
            new WorkerStatusProvisioning(provisioning?.Enabled ?? false, provisioning?.AllowNonPrivileged ?? false,
                provisioning?.AllowCredentials ?? false, provisioning?.AllowedPrivilegedActions.Count ?? 0,
                provisioning?.DeniedActions.Count ?? 0), capabilities, diagnostics,
            new WorkerStatusRuntime(RuntimeInformation.FrameworkDescription, RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeInformation.OSArchitecture.ToString()));
    }

    private static async Task<WorkerStatusRegistration> ObserveRegistrationAsync(GlobalWorkerConfiguration? configuration,
        List<string> diagnostics, CancellationToken cancellationToken)
    {
        if (configuration is null) return new("unknown", "unknown", "unknown", null);
        var settings = configuration.Server;
        var path = settings.IdentityFile ?? WorkerIdentity.DefaultPath;
        string? workerId = null;
        var identityState = "present";
        try { workerId = await WorkerIdentity.LoadAsync(path, cancellationToken); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            identityState = ex is FileNotFoundException or DirectoryNotFoundException ? "missing" :
                ex is InvalidDataException ? "invalid" : "unreadable";
        }
        if (!settings.Enabled)
            return new("standalone", "disabled", "not-applicable", path, workerId,
                "not-applicable", AssociationSource: "not-applicable", ServerAcceptance: "not-applicable");

        if (identityState != "present") diagnostics.Add($"worker-identity-{identityState}");
        var credentialState = "persisted";
        try { _ = await WorkerAuthentication.LoadTokenAsync(path, cancellationToken); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            credentialState = ex is FileNotFoundException or DirectoryNotFoundException
                ? string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN")) ? "missing" : "environment"
                : ex is InvalidDataException ? "invalid" : "unreadable";
        }
        if (credentialState is "missing" or "invalid" or "unreadable") diagnostics.Add($"worker-credential-{credentialState}");
        string? serverUrl = null;
        var source = File.Exists(Path.GetFullPath(path) + ".server") ? "persisted" : "configuration";
        try { serverUrl = settings.EffectiveUrl; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            source = ex is InvalidDataException ? "invalid" : "unreadable";
            diagnostics.Add($"worker-server-association-{source}");
        }
        return new($"local-identity-{identityState}", "configured", "not-checked", path, workerId,
            credentialState, serverUrl, source);
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
        if (status.Runtime is { } runtime)
            writer.WriteLine($"Runtime: {runtime.Framework}; process: {runtime.ProcessArchitecture}; OS: {runtime.OsArchitecture}");
        writer.WriteLine($"Configuration: {status.Configuration.Validity} ({status.Configuration.Path})");
        if (status.Configuration.ProjectCount is int projectCount)
            writer.WriteLine($"Projects: {projectCount}; ownership: {status.Configuration.Ownership}");
        writer.WriteLine($"Server: {status.Registration.ServerConfiguration}; connectivity: {status.Registration.ServerConnectivity}; registration: {status.Registration.State}");
        writer.WriteLine($"Identity: {status.Registration.WorkerId ?? "unknown"}; credential: {status.Registration.CredentialState}; Server acceptance: {status.Registration.ServerAcceptance}");
        if (status.Registration.ServerUrl is { } serverUrl)
            writer.WriteLine($"Server association: {serverUrl} ({status.Registration.AssociationSource})");
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
        "worker-identity-invalid" or "worker-identity-unreadable" => "Local Worker identity cannot be validated; restore the existing identity or correct file access. Identity was not regenerated.",
        "worker-credential-missing" or "worker-credential-invalid" or "worker-credential-unreadable" => "Local Worker credentials are unavailable or invalid; inspect registration and local file access. No credential was changed.",
        "worker-server-association-invalid" or "worker-server-association-unreadable" => "Persisted Server association cannot be validated; correct the endpoint or local file access before connecting.",
        "server-state-unverified" => "Local tools are present; Server connectivity, managed configuration, and execution readiness were not checked.",
        "git-unavailable" => "Git is unavailable; install Git and ensure it is on PATH.",
        "github-cli-unavailable" => "GitHub CLI is unavailable; install gh and configure authentication.",
        "codex-cli-unavailable" => "Codex CLI is unavailable; install Codex and complete its authentication setup.",
        _ => "Worker is not ready based on local observations."
    };
}
