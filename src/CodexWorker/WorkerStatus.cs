namespace CodexWorker;

using CodexProvisioning;
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
public sealed record WorkerStatusCapability(string Type, string Name, string State, string? Version, string? DiagnosticCode,
    InstallationState? Installation = null, RequirementState? Authentication = null, RequirementState? Configuration = null,
    LocalConfigurationDependencyKind? ConfigurationDependency = null,
    IReadOnlyList<AuthenticationDependencyKind>? AuthenticationDependencies = null,
    IReadOnlyList<string>? BlockingReasons = null);

public static class WorkerStatusReporter
{
    public static async Task<WorkerStatusDocument> CreateAsync(string configurationPath,
        WorkerCapabilityDiscovery? discovery = null, CancellationToken cancellationToken = default,
        NodeCapabilityDiscovery? inventoryDiscovery = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        var fullPath = Path.GetFullPath(configurationPath);
        GlobalWorkerConfiguration? configuration = null;
        IReadOnlyList<(string Path, WorkerConfiguration Configuration)> projects = [];
        string? diagnostic = null;
        try
        {
            configuration = GlobalWorkerConfiguration.Load(fullPath);
            projects = configuration.Projects.Ownership == "managed" ? [] : ProjectConfigurationDiscovery.Load(configuration.Projects.Directory,
                allowEmpty: true,
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
        var inventory = await (inventoryDiscovery ?? WorkerRegistrationClient.ProvisioningDiscovery)
            .GetAsync(cancellationToken: cancellationToken);
        foreach (var definition in CapabilityCatalog.Definitions)
        {
            var index = capabilities.FindIndex(capability => capability.Name == definition.Id);
            if (index < 0) continue;
            var state = inventory.FirstOrDefault(state => state.Id == definition.Id) ?? CapabilityCatalog.Unknown(definition);
            var evaluated = CapabilityCatalog.Evaluate(definition, state);
            var summary = state.Installation == InstallationState.Missing ? "missing" :
                evaluated.Available ? "available" : "blocked";
            capabilities[index] = new("tool", definition.Id, summary, state.DetectedVersion, state.DiagnosticCode,
                state.Installation, state.Authentication, state.Configuration, definition.ConfigurationDependency,
                definition.AuthenticationDependencies, evaluated.BlockingReasons);
        }
        var provisioning = configuration?.Worker.Provisioning;
        var diagnostics = new List<string>();
        if (diagnostic is not null) diagnostics.Add(diagnostic);
        foreach (var capability in capabilities.Where(capability => capability.BlockingReasons is not null))
        {
            foreach (var reason in capability.BlockingReasons ?? [])
            {
                // Installation is the first actionable step; dependency state remains visible in the summary/JSON.
                if (capability.Installation == InstallationState.Missing && reason is "authentication-required" or "configuration-required") continue;
                if (reason == "not-detected" && (capability.BlockingReasons ?? []).Contains("probe-failed")) continue;
                diagnostics.Add($"{capability.Name}:{reason}");
            }
        }
        var registration = await ObserveRegistrationAsync(configuration, diagnostics, cancellationToken);
        var readiness = CapabilityCatalog.ExecutionReadiness(inventory);
        var locallyReady = configuration is not null && readiness.Available &&
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
                provisioning?.DeniedActions.Count ?? 0), capabilities, diagnostics.Distinct(StringComparer.Ordinal).ToArray(),
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
        {
            var details = new List<string>();
            if (capability.Installation is { } installation) details.Add(installation.ToString().ToLowerInvariant());
            if (capability.ConfigurationDependency is { } dependency && capability.Configuration != RequirementState.Satisfied)
                details.Add($"configuration required ({dependency})");
            if (capability.AuthenticationDependencies is { Count: > 0 } && capability.Authentication != RequirementState.Satisfied)
                details.Add(capability.Installation == InstallationState.Missing ? "authentication required after installation" : "authentication required");
            writer.WriteLine($"  {capability.Type}/{capability.Name}: {capability.State}{(capability.Version is null ? "" : $" ({capability.Version})")}{(details.Count == 0 ? "" : $"; {string.Join(", ", details)}")}");
        }
        foreach (var diagnostic in status.Diagnostics.Distinct(StringComparer.Ordinal))
            writer.WriteLine($"Diagnostic: {DiagnosticText(diagnostic)}");
    }

    private static string DiagnosticText(string code)
    {
        var parts = code.Split(':', 2);
        if (parts.Length == 2 && CapabilityCatalog.Definitions.FirstOrDefault(definition => definition.Id == parts[0]) is { } definition)
            return parts[1] switch
            {
                "tool-missing" => $"{definition.DisplayName} is not installed.",
                "configuration-required" when definition.ConfigurationDependency == LocalConfigurationDependencyKind.GitIdentity =>
                    "Git identity is not configured for Worker execution.",
                "configuration-required" when definition.ConfigurationDependency == LocalConfigurationDependencyKind.DockerDaemonAccess =>
                    "Docker daemon is not accessible by the Worker service account; check daemon availability and service-account access.",
                "configuration-required" => $"{definition.DisplayName} requires configuration for Worker execution.",
                "authentication-required" => $"{definition.DisplayName} authentication is required for Worker execution.",
                "probe-failed" => $"{definition.DisplayName} readiness could not be checked; inspect local tool and service-account access.",
                "not-detected" => $"{definition.DisplayName} readiness has not been established; refresh capabilities.",
                "operation-running" => $"{definition.DisplayName} has a provisioning operation in progress.",
                "operation-failed" => $"{definition.DisplayName} has a failed provisioning operation; inspect provisioning status.",
                _ => $"{definition.DisplayName} is not ready for Worker execution."
            };
        return code switch
        {
            "configuration-not-found" => "Configuration file was not found; pass --config with a valid worker configuration path.",
            "configuration-invalid" => "Configuration could not be validated; run 'codex-worker config validate' for details.",
            "worker-identity-missing" => "Managed mode has no local Worker identity file; register this Worker or restore its identity before starting it.",
            "worker-identity-invalid" or "worker-identity-unreadable" => "Local Worker identity cannot be validated; restore the existing identity or correct file access. Identity was not regenerated.",
            "worker-credential-missing" or "worker-credential-invalid" or "worker-credential-unreadable" => "Local Worker credentials are unavailable or invalid; inspect registration and local file access. No credential was changed.",
            "worker-server-association-invalid" or "worker-server-association-unreadable" => "Persisted Server association cannot be validated; correct the endpoint or local file access before connecting.",
            "server-state-unverified" => "Local prerequisites are present; Server connectivity, managed configuration, and agent execution preflight were not checked.",
            "git-unavailable" => "Git is unavailable; install Git and ensure it is on PATH.",
            "github-cli-unavailable" => "GitHub CLI is unavailable; install gh and configure authentication.",
            "codex-cli-unavailable" => "Codex CLI is unavailable; install Codex and complete its authentication setup.",
            _ => "Worker is not ready based on local observations."
        };
    }
}
