namespace CodexServer;

using System.Globalization;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public sealed record ServerConfigurationMutationDocument(int ContractVersion, bool Succeeded, string Setting,
    bool RestartRequired);

/// <summary>Atomically updates one explicitly supported value in the installed systemd EnvironmentFile.</summary>
public static partial class ServerConfigurationMutation
{
    private static readonly IReadOnlyDictionary<string, string> SupportedSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ListenUrl"] = "Server__ListenUrl",
        ["DataDirectory"] = "Server__DataDirectory",
        ["DatabasePath"] = "Server__DatabasePath",
        ["EnableLocalProvisioning"] = "Server__EnableLocalProvisioning",
        ["AllowLocalProvisioningElevation"] = "Server__AllowLocalProvisioningElevation",
        ["WorkerStaleAfterSeconds"] = "Server__WorkerStaleAfterSeconds",
        ["ExecutionLeaseDurationSeconds"] = "Server__ExecutionLeaseDurationSeconds",
        ["ExecutionLeaseRenewalIntervalSeconds"] = "Server__ExecutionLeaseRenewalIntervalSeconds"
    };

    public static void Set(string path, string setting, string value, ServerConfiguration currentConfiguration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(setting);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(currentConfiguration);
        if (setting.StartsWith("Server:", StringComparison.OrdinalIgnoreCase)) setting = setting[7..];
        if (!SupportedSettings.TryGetValue(setting, out var environmentKey))
            throw new ArgumentException($"Unknown or unsupported Server setting '{setting}'. Supported settings: {string.Join(", ", SupportedSettings.Keys)}.");
        if (value.Length > 4096 || value.Any(char.IsControl))
            throw new ArgumentException("Server configuration values must be at most 4096 characters and cannot contain control characters.");

        ApplyValue(currentConfiguration, setting, value);
        currentConfiguration.Validate();
        _ = currentConfiguration.ResolveDataDirectory();
        _ = currentConfiguration.ResolveDatabasePath();

        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        if (file.LinkTarget is not null || !file.Exists)
            throw new IOException("The installed Server configuration file is missing or linked.");

        var originalText = File.ReadAllText(fullPath);
        var newline = originalText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = originalText.Split('\n').Select(line => line.EndsWith('\r') ? line[..^1] : line).ToArray();
        var found = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var separator = line.IndexOf('=');
            if (separator < 0) continue;
            var key = line[..separator].Trim();
            if (!string.Equals(key, environmentKey, StringComparison.OrdinalIgnoreCase)) continue;
            lines[index] = $"{environmentKey}={FormatEnvironmentValue(value)}";
            found = true;
        }
        if (!found)
        {
            var entries = lines.ToList();
            if (entries.Count > 0 && entries[^1].Length == 0) entries.RemoveAt(entries.Count - 1);
            entries.Add($"{environmentKey}={FormatEnvironmentValue(value)}");
            lines = entries.ToArray();
        }

        var updatedText = string.Join(newline, lines);
        if (!updatedText.EndsWith(newline, StringComparison.Ordinal)) updatedText += newline;
        var directory = Path.GetDirectoryName(fullPath) ?? throw new IOException("Server configuration path has no directory.");
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                writer.Write(updatedText);
            PreserveUnixMetadata(fullPath, temporaryPath);
            if (!string.Equals(originalText, File.ReadAllText(fullPath), StringComparison.Ordinal))
                throw new IOException("Server configuration changed while the update was being prepared; no changes were written.");
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static string CanonicalSetting(string setting)
    {
        if (setting.StartsWith("Server:", StringComparison.OrdinalIgnoreCase)) setting = setting[7..];
        return SupportedSettings.Keys.FirstOrDefault(key => string.Equals(key, setting, StringComparison.OrdinalIgnoreCase)) ?? setting;
    }

    private static void ApplyValue(ServerConfiguration configuration, string setting, string value)
    {
        switch (CanonicalSetting(setting))
        {
            case "ListenUrl": configuration.ListenUrl = value; break;
            case "DataDirectory": configuration.DataDirectory = value; break;
            case "DatabasePath": configuration.DatabasePath = value; break;
            case "EnableLocalProvisioning": configuration.EnableLocalProvisioning = ParseBoolean(setting, value); break;
            case "AllowLocalProvisioningElevation": configuration.AllowLocalProvisioningElevation = ParseBoolean(setting, value); break;
            case "WorkerStaleAfterSeconds": configuration.WorkerStaleAfterSeconds = ParseInteger(setting, value); break;
            case "ExecutionLeaseDurationSeconds": configuration.ExecutionLeaseDurationSeconds = ParseInteger(setting, value); break;
            case "ExecutionLeaseRenewalIntervalSeconds": configuration.ExecutionLeaseRenewalIntervalSeconds = ParseInteger(setting, value); break;
            default: throw new ArgumentException("Unsupported Server setting.");
        }
    }

    private static bool ParseBoolean(string setting, string value) => bool.TryParse(value, out var result)
        ? result : throw new ArgumentException($"Server:{CanonicalSetting(setting)} requires true or false.");

    private static int ParseInteger(string setting, string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            ? result : throw new ArgumentException($"Server:{CanonicalSetting(setting)} requires a positive integer within its supported range.");

    private static string FormatEnvironmentValue(string value) => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static void PreserveUnixMetadata(string source, string destination)
    {
        if (OperatingSystem.IsWindows()) return;
        if (OperatingSystem.IsLinux())
        {
            if (NativeMethods.Statx(-100, source, 0, 0x7ff, out var metadata) != 0)
                throw new IOException("Could not inspect Server configuration ownership.", new Win32Exception(Marshal.GetLastPInvokeError()));
            if (NativeMethods.Chown(destination, metadata.UserId, metadata.GroupId) != 0)
                throw new IOException("Could not preserve Server configuration ownership.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
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
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(4)] public uint BlockSize;
        [FieldOffset(8)] public ulong Attributes;
        [FieldOffset(16)] public uint LinkCount;
        [FieldOffset(20)] public uint UserId;
        [FieldOffset(24)] public uint GroupId;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(48)] public ulong Blocks;
        [FieldOffset(56)] public ulong AttributesMask;
        [FieldOffset(64)] public long AccessSeconds;
        [FieldOffset(72)] public uint AccessNanoseconds;
        [FieldOffset(80)] public long BirthSeconds;
        [FieldOffset(88)] public uint BirthNanoseconds;
        [FieldOffset(96)] public long ChangeSeconds;
        [FieldOffset(104)] public uint ChangeNanoseconds;
        [FieldOffset(112)] public long ModifySeconds;
        [FieldOffset(120)] public uint ModifyNanoseconds;
        [FieldOffset(128)] public uint RdevMajor;
        [FieldOffset(132)] public uint RdevMinor;
        [FieldOffset(136)] public uint DevMajor;
        [FieldOffset(140)] public uint DevMinor;
        [FieldOffset(144)] public ulong MountId;
        [FieldOffset(152)] public uint DioMemoryAlignment;
        [FieldOffset(156)] public uint DioOffsetAlignment;
    }
}
