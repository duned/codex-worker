namespace CodexWorker;

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using YamlDotNet.RepresentationModel;

/// <summary>Local administration operations for the installed global Worker configuration.</summary>
public static partial class WorkerConfigurationAdministration
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly IReadOnlyDictionary<string, string> SupportedSettings = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["worker.pollingSeconds"] = "positive integer",
        ["worker.preflightTimeoutSeconds"] = "integer from 1 to 300",
        ["worker.maxParallelTasks"] = "integer from 1 to 8",
        ["worker.provisioning.enabled"] = "true or false",
        ["worker.provisioning.allowNonPrivileged"] = "true or false",
        ["worker.provisioning.allowCredentials"] = "true or false",
        ["worker.provisioning.allowedPrivilegedActions"] = "comma-separated action keys, up to 100",
        ["worker.provisioning.deniedActions"] = "comma-separated action keys, up to 100"
    };

    public static string ResolvePath(string? configuredPath) => Path.GetFullPath(
        string.IsNullOrWhiteSpace(configuredPath) ? WorkerCommandLine.DefaultConfigurationPath : configuredPath);

    public static IReadOnlyList<string> Validate(string path)
    {
        var diagnostics = new List<string>();
        try
        {
            var configuration = GlobalWorkerConfiguration.Load(path);
            _ = ProjectConfigurationDiscovery.LoadForWorker(configuration);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(FailureDiagnosticRedactor.Redact(ex.Message));
        }
        return diagnostics;
    }

    public static string Show(string path, bool json)
    {
        var configuration = GlobalWorkerConfiguration.Load(path);
        var fullPath = Path.GetFullPath(path);
        var view = CreateDocument(fullPath, configuration);

        if (json) return JsonSerializer.Serialize(view, JsonOptions);
        return $"Configuration: {fullPath}{Environment.NewLine}" +
            $"worker.pollingSeconds: {configuration.Worker.PollingSeconds}{Environment.NewLine}" +
            $"worker.preflightTimeoutSeconds: {configuration.Worker.PreflightTimeoutSeconds}{Environment.NewLine}" +
            $"worker.maxParallelTasks: {configuration.Worker.MaxParallelTasks}{Environment.NewLine}" +
            $"worker.provisioning.enabled: {configuration.Worker.Provisioning.Enabled.ToString().ToLowerInvariant()}{Environment.NewLine}" +
            $"worker.provisioning.allowNonPrivileged: {configuration.Worker.Provisioning.AllowNonPrivileged.ToString().ToLowerInvariant()}{Environment.NewLine}" +
            $"worker.provisioning.allowCredentials: {configuration.Worker.Provisioning.AllowCredentials.ToString().ToLowerInvariant()}{Environment.NewLine}" +
            $"worker.provisioning.allowedPrivilegedActions: {string.Join(",", configuration.Worker.Provisioning.AllowedPrivilegedActions)}{Environment.NewLine}" +
            $"worker.provisioning.deniedActions: {string.Join(",", configuration.Worker.Provisioning.DeniedActions)}{Environment.NewLine}" +
            $"projects.directory: {configuration.Projects.Directory}{Environment.NewLine}" +
            $"projects.ownership: {configuration.Projects.Ownership}{Environment.NewLine}" +
            $"telegram.enabled: {configuration.Telegram.Enabled.ToString().ToLowerInvariant()}{Environment.NewLine}" +
            $"api.enabled: {configuration.Api.Enabled.ToString().ToLowerInvariant()}{Environment.NewLine}" +
            $"api.listenUrl: {SafeUrl(configuration.Api.ListenUrl)}{Environment.NewLine}" +
            $"api.eventHistoryLimit: {configuration.Api.EventHistoryLimit}{Environment.NewLine}" +
            $"server.enabled: {configuration.Server.Enabled.ToString().ToLowerInvariant()}{Environment.NewLine}" +
            $"server.url: {SafeUrl(configuration.Server.Url)}{Environment.NewLine}" +
            $"server.heartbeatIntervalSeconds: {configuration.Server.HeartbeatIntervalSeconds}{Environment.NewLine}" +
            $"server.identityFile: {(string.IsNullOrWhiteSpace(configuration.Server.IdentityFile) ? "" : "[redacted]")}{Environment.NewLine}" +
            "Sensitive paths are redacted.";
    }

    internal static WorkerConfigurationDocument CreateDocument(string path, GlobalWorkerConfiguration configuration) => new(
        1, Path.GetFullPath(path),
        new WorkerConfigurationWorker(configuration.Worker.PollingSeconds, configuration.Worker.PreflightTimeoutSeconds,
            configuration.Worker.MaxParallelTasks,
            new WorkerConfigurationProvisioning(configuration.Worker.Provisioning.Enabled,
                configuration.Worker.Provisioning.AllowNonPrivileged, configuration.Worker.Provisioning.AllowCredentials,
                configuration.Worker.Provisioning.AllowedPrivilegedActions, configuration.Worker.Provisioning.DeniedActions)),
        new WorkerConfigurationProjects(configuration.Projects.Directory, configuration.Projects.Ownership),
        new WorkerConfigurationTelegram(configuration.Telegram.Enabled),
        new WorkerConfigurationApi(configuration.Api.Enabled, SafeUrl(configuration.Api.ListenUrl), configuration.Api.EventHistoryLimit),
        new WorkerConfigurationServer(configuration.Server.Enabled, SafeUrl(configuration.Server.Url),
            configuration.Server.HeartbeatIntervalSeconds,
            string.IsNullOrWhiteSpace(configuration.Server.IdentityFile) ? null : "[redacted]"));

    internal static string SafeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return string.IsNullOrWhiteSpace(value) ? "" : "[redacted]";
        var safeUri = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri;
        return safeUri.GetLeftPart(UriPartial.Path);
    }

    public static void Set(string path, string setting, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(setting);
        ArgumentNullException.ThrowIfNull(value);
        if (!SupportedSettings.ContainsKey(setting))
            throw new ArgumentException($"Unknown setting '{setting}'. Supported settings: {string.Join(", ", SupportedSettings.Keys)}.");

        var scalarValue = ParseValue(setting, value);
        var fullPath = Path.GetFullPath(path);
        _ = GlobalWorkerConfiguration.Load(fullPath);

        var originalText = File.ReadAllText(fullPath);
        var yaml = new YamlStream();
        using (var reader = new StringReader(originalText)) yaml.Load(reader);
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException("Worker configuration must contain one YAML mapping document.");

        SetNode(root, setting.Split('.'), scalarValue);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidDataException("Configuration path must include a directory.");
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var writer = new StreamWriter(temporaryPath, false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                yaml.Save(writer, assignAnchors: false);
            PreserveUnixMode(fullPath, temporaryPath);

            // Validate the complete candidate before the atomic replacement. A temp file in the
            // same directory keeps all relative paths anchored to the original config directory.
            _ = GlobalWorkerConfiguration.Load(temporaryPath);
            if (!string.Equals(originalText, File.ReadAllText(fullPath), StringComparison.Ordinal))
                throw new IOException("Configuration changed while the update was being prepared; no changes were written.");
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static string HelpText =>
        "Usage: codex-worker config <show|validate|set> [options]" + Environment.NewLine +
        "  show [--json]                         Inspect the installed global configuration." + Environment.NewLine +
        "  validate [--json]                     Validate global and project configuration." + Environment.NewLine +
        "  set <setting> <value> [--json]        Atomically update a supported Worker setting." + Environment.NewLine +
        "Use --config <path> to select a configuration file; otherwise the installed default is used." + Environment.NewLine +
        "Set supports worker.pollingSeconds, worker.preflightTimeoutSeconds, worker.maxParallelTasks, " +
        "worker.provisioning.enabled, worker.provisioning.allowNonPrivileged, worker.provisioning.allowCredentials, " +
        "worker.provisioning.allowedPrivilegedActions, and worker.provisioning.deniedActions.";

    private static object ParseValue(string setting, string value)
    {
        if (setting.EndsWith("Actions", StringComparison.Ordinal))
        {
            if (value.Length == 0) return Array.Empty<string>();
            var actions = value.Split(',', StringSplitOptions.None);
            if (actions.Length > 100 || actions.Any(action => string.IsNullOrWhiteSpace(action) || action.Length > 200 ||
                    action.Any(char.IsControl) || !string.Equals(action, action.Trim(), StringComparison.Ordinal)) ||
                actions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != actions.Length)
                throw new ArgumentException($"Setting '{setting}' requires up to 100 distinct, nonempty printable action keys of at most 200 characters; separate keys with commas.");
            return actions;
        }

        if (setting is "worker.provisioning.enabled" or "worker.provisioning.allowNonPrivileged" or
            "worker.provisioning.allowCredentials")
        {
            if (bool.TryParse(value, out var boolean)) return boolean;
            throw new ArgumentException($"Setting '{setting}' requires true or false.");
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var integer))
            throw new ArgumentException($"Setting '{setting}' requires {SupportedSettings[setting]}.");
        if ((setting == "worker.pollingSeconds" && integer <= 0) ||
            (setting == "worker.preflightTimeoutSeconds" && integer is < 1 or > 300) ||
            (setting == "worker.maxParallelTasks" && integer is < 1 or > 8))
            throw new ArgumentException($"Setting '{setting}' requires {SupportedSettings[setting]}.");
        return integer;
    }

    private static void SetNode(YamlMappingNode root, IReadOnlyList<string> path, object value)
    {
        var mapping = root;
        for (var index = 0; index < path.Count - 1; index++)
        {
            var key = new YamlScalarNode(path[index]);
            if (mapping.Children.TryGetValue(key, out var child))
            {
                mapping = child as YamlMappingNode ?? throw new InvalidDataException($"Configuration section '{string.Join('.', path.Take(index + 1))}' must be a YAML mapping.");
            }
            else
            {
                var nested = new YamlMappingNode();
                mapping.Add(key, nested);
                mapping = nested;
            }
        }

        var finalKey = new YamlScalarNode(path[^1]);
        YamlNode replacement = value switch
        {
            bool boolean => new YamlScalarNode(boolean ? "true" : "false"),
            int integer => new YamlScalarNode(integer.ToString(CultureInfo.InvariantCulture)),
            string[] items => new YamlSequenceNode(items.Select(item => (YamlNode)new YamlScalarNode(item))),
            _ => throw new InvalidOperationException("Unsupported Worker configuration value type.")
        };
        if (mapping.Children.ContainsKey(finalKey)) mapping.Children[finalKey] = replacement;
        else mapping.Add(finalKey, replacement);
    }

    private static void PreserveUnixMode(string source, string destination)
    {
        if (OperatingSystem.IsWindows()) return;

        if (OperatingSystem.IsLinux()) PreserveLinuxOwnership(source, destination);
        File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
    }

    private static void PreserveLinuxOwnership(string source, string destination)
    {
        if (NativeMethods.Statx(-100, source, 0, 0x7ff, out var metadata) != 0)
            throw new IOException("Could not inspect the existing configuration file ownership.", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));

        if (NativeMethods.Chown(destination, metadata.UserId, metadata.GroupId) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            var cause = new System.ComponentModel.Win32Exception(error);
            if (error is 1 or 13)
                throw new UnauthorizedAccessException("Elevated permissions are required to preserve the existing configuration file ownership.", cause);
            throw new IOException("Could not preserve the existing configuration file ownership.", cause);
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Statx(int directoryFileDescriptor, string path, int flags, uint mask, out StatxMetadata metadata);

        [LibraryImport("libc", EntryPoint = "chown", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Chown(string path, uint owner, uint group);
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxMetadata
    {
        [FieldOffset(20)]
        public uint UserId;

        [FieldOffset(24)]
        public uint GroupId;
    }
}
