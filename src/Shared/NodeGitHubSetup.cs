namespace CodexProvisioning;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>The only SSH material allowed across the provisioning boundary.</summary>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record SshPublicIdentity(string PublicKey, string Fingerprint);

/// <summary>Node-local, service-account-owned authentication. No private material or process output is reported.</summary>
public sealed class NodeGitHubSetup
{
    private const string Ownership = "codex-provisioning-github-v1";
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly string _root;
    private readonly Func<bool> _unrelatedAuthentication;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output)>> _run;
    private readonly Func<string, Func<CodexLoginInstructions, CancellationToken, Task>, CancellationToken, Task<int>> _login;
    public static string DefaultRoot
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!Path.IsPathFullyQualified(home)) throw new IOException("Service account home is unavailable.");
            return Path.Combine(home, ".local", "share", "codex-provisioning");
        }
    }

    public NodeGitHubSetup(string? root = null,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output)>>? run = null,
        Func<bool>? unrelatedAuthentication = null,
        Func<string, Func<CodexLoginInstructions, CancellationToken, Task>, CancellationToken, Task<int>>? login = null)
    {
        _root = root ?? DefaultRoot;
        _unrelatedAuthentication = unrelatedAuthentication ?? (() => HasUnrelatedAuthentication(Path.Combine(_root, "github")));
        _run = run ?? RunAsync;
        _login = login ?? GitHubDeviceLogin.RunAsync;
    }

    public static bool Handles(ProvisioningCommandRequest request) => request.Action is
        ProvisioningCommandAction.PrepareAuthentication or ProvisioningCommandAction.GenerateSshKey or
        ProvisioningCommandAction.InspectSshKey or ProvisioningCommandAction.RemoveSshKey or ProvisioningCommandAction.VerifyRepositoryAccess ||
        request.CapabilityId == "github-cli" && request.Action is (ProvisioningCommandAction.Logout or ProvisioningCommandAction.Login);

    // Existing operator gh configuration remains untouched. Once setup is explicitly requested,
    // trusted gh calls use the product directory; Codex's stripped environment is unchanged.
    public static async Task ApplyGitHubEnvironmentAsync(ProcessStartInfo start, CancellationToken token)
    {
        if (Path.GetFileName(start.FileName) != "gh") return;
        var environment = await GitHubEnvironmentAsync(token);
        foreach (var (key, value) in environment)
            if (value is null) start.Environment.Remove(key); else start.Environment[key] = value;
        start.Environment.Remove("GH_TOKEN");
        start.Environment.Remove("GITHUB_TOKEN");
        start.Environment.Remove("GH_ENTERPRISE_TOKEN");
    }

    public static async Task<IReadOnlyDictionary<string, string?>> GitHubEnvironmentAsync(CancellationToken token, string? root = null)
    {
        var directory = Path.Combine(root ?? DefaultRoot, "github");
        VerifyPath(directory);
        if (!Directory.Exists(directory)) return new Dictionary<string, string?>();
        if (HasUnrelatedAuthentication(directory)) throw new IOException("Unrelated GitHub authentication must be reconciled locally.");
        await VerifyGitHubOwnershipAsync(directory, token);
        return new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = directory };
    }

    public async Task<ProvisioningCommandReport> ExecuteAsync(ProvisioningCommandRequest request, CancellationToken token,
        Func<CodexLoginInstructions, Task>? reportLoginInstructions = null)
    {
        if (!OperatingSystem.IsLinux()) return Failure(ProvisioningDiagnostic.Unsupported);
        if (!ProvisioningCommandProtocol.Valid(request) || !Handles(request)) return Failure(ProvisioningDiagnostic.Unsupported);
        token.ThrowIfCancellationRequested();
        VerifyPath(_root);
        var directory = Path.Combine(_root, request.CapabilityId == "github-cli" ? "github" : "ssh");
        VerifyPath(directory);
        if (request.Action == ProvisioningCommandAction.PrepareAuthentication)
        {
            if (_unrelatedAuthentication()) return Failure(ProvisioningDiagnostic.Denied);
            if (Directory.Exists(directory)) await VerifyGitHubOwnershipAsync(directory, token);
            else
            {
                CreatePrivateDirectory(directory);
                var marker = Path.Combine(directory, "managed-by-codex");
                await File.WriteAllTextAsync(marker, Ownership, token);
                File.SetUnixFileMode(marker, FileMode);
            }
            return Success();
        }
        if (request.Action == ProvisioningCommandAction.Logout)
        {
            // gh removes its own keyring/file credential within the verified product
            // context. Never touch a shared operator authentication configuration.
            if (!Directory.Exists(directory)) return Success();
            if (_unrelatedAuthentication()) return Failure(ProvisioningDiagnostic.Denied);
            await VerifyGitHubOwnershipAsync(directory, token);
            var hosts = Path.Combine(directory, "hosts.yml");
            VerifyPath(hosts);
            if (!File.Exists(hosts)) return Success();
            var logout = await _run("/usr/bin/gh", ["auth", "logout", "--hostname", "github.com"], token);
            if (logout.ExitCode != 0) return Failure(ProvisioningDiagnostic.ProcessFailed);
            var status = await _run("/usr/bin/gh", ["auth", "status", "--hostname", "github.com"], token);
            return status.ExitCode != 0 ? Success() : Failure(ProvisioningDiagnostic.ProcessFailed);
        }

        if (request.Action == ProvisioningCommandAction.Login)
        {
            if (reportLoginInstructions is null || !Directory.Exists(directory) || _unrelatedAuthentication())
                return Failure(ProvisioningDiagnostic.Denied);
            await VerifyGitHubOwnershipAsync(directory, token);
            var code = await _login(directory, async (instructions, progressToken) =>
            {
                if (instructions.VerificationUri != "https://github.com/login/device" || !CodexDeviceLogin.Valid(instructions))
                    throw new InvalidOperationException("Invalid GitHub device login instructions.");
                await reportLoginInstructions(instructions);
                progressToken.ThrowIfCancellationRequested();
            }, token);
            if (code != 0) return Failure(ProvisioningDiagnostic.ProcessFailed);
            var authenticationStatus = await _run("/usr/bin/gh", ["auth", "status", "--hostname", "github.com"], token);
            return authenticationStatus.ExitCode == 0 ? Success() : Failure(ProvisioningDiagnostic.ProcessFailed);
        }

        var key = Path.Combine(directory, "github_ed25519");
        var publicKey = key + ".pub";
        VerifyPath(key);
        VerifyPath(publicKey);
        if (request.Action == ProvisioningCommandAction.RemoveSshKey)
        {
            // Replacement is deliberately remove, then generate. Refuse unrelated/partial files.
            if (!File.Exists(key) && !File.Exists(publicKey)) return Success();
            await ReadIdentityAsync(key, publicKey, token);
            token.ThrowIfCancellationRequested();
            File.Delete(publicKey);
            File.Delete(key);
            return Success();
        }
        if (request.Action == ProvisioningCommandAction.GenerateSshKey)
        {
            if (File.Exists(key) || File.Exists(publicKey)) return Failure(ProvisioningDiagnostic.Denied);
            CreatePrivateDirectory(directory);
            // ssh-keygen itself creates the private key with mode 0600. Failed/interrupted
            // generation is retained for explicit node-local reconciliation, never overwritten.
            var generated = await _run("/usr/bin/ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-C", "codex-provisioning", "-f", key], token);
            if (generated.ExitCode != 0) return Failure(ProvisioningDiagnostic.ProcessFailed);
            File.SetUnixFileMode(key, FileMode);
            File.SetUnixFileMode(publicKey, FileMode);
        }
        var identity = await ReadIdentityAsync(key, publicKey, token);
        if (request.Action is ProvisioningCommandAction.GenerateSshKey or ProvisioningCommandAction.InspectSshKey)
            return Success(identity);
        // No caller-supplied executable, options, host, URL, path or shell text. Ignore SSH
        // config and require an operator-trusted github.com entry in node known_hosts.
        var ssh = "/usr/bin/ssh -F /dev/null -o BatchMode=yes -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes -i " + ShellQuote(key);
        var repository = request.Repository ?? throw new InvalidOperationException("Repository is required.");
        if (repository.EndsWith(".git", StringComparison.Ordinal)) repository = repository[..^4];
        var verified = await _run("/usr/bin/git", ["-C", directory, "-c", "core.sshCommand=" + ssh, "ls-remote", "--", "git@github.com:" + repository + ".git"], token);
        return verified.ExitCode == 0 ? Success() : Failure(ProvisioningDiagnostic.ProcessFailed);
    }

    private async Task<SshPublicIdentity> ReadIdentityAsync(string key, string publicKey, CancellationToken token)
    {
        VerifyPath(key);
        VerifyPath(publicKey);
        if (!File.Exists(key) || !File.Exists(publicKey) || new FileInfo(publicKey).Length > 256)
            throw new IOException("Managed keypair is missing or invalid.");
        if (OperatingSystem.IsLinux() && (File.GetUnixFileMode(key) != FileMode || File.GetUnixFileMode(Path.GetDirectoryName(key) ?? throw new IOException("Invalid key directory.")) != DirectoryMode))
            throw new IOException("Managed key permissions are invalid.");
        var text = (await File.ReadAllTextAsync(publicKey, token)).Trim();
        if (!text.EndsWith(" codex-provisioning", StringComparison.Ordinal)) throw new IOException("Key ownership is not established.");
        var identity = ParseIdentity(text);
        var derived = await _run("/usr/bin/ssh-keygen", ["-y", "-P", "", "-f", key], token);
        if (derived.ExitCode != 0 || ParseIdentity(derived.Output.Trim()).PublicKey != identity.PublicKey)
            throw new IOException("Managed keypair does not match.");
        return identity;
    }

    private static class GitHubDeviceLogin
    {
        private const string VerificationUri = "https://github.com/login/device";

        public static async Task<int> RunAsync(string configurationDirectory,
            Func<CodexLoginInstructions, CancellationToken, Task> publish, CancellationToken token)
        {
            using var process = new Process { StartInfo = new("/usr/bin/gh")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            } };
            foreach (var argument in new[] { "auth", "login", "--hostname", "github.com", "--web", "--git-protocol", "https" })
                process.StartInfo.ArgumentList.Add(argument);
            process.StartInfo.Environment["GH_CONFIG_DIR"] = configurationDirectory;
            process.StartInfo.Environment["GH_PROMPT_DISABLED"] = "1";
            process.StartInfo.Environment["GH_BROWSER"] = "/usr/bin/true";
            process.StartInfo.Environment.Remove("GH_TOKEN");
            process.StartInfo.Environment.Remove("GITHUB_TOKEN");
            process.StartInfo.Environment.Remove("GH_ENTERPRISE_TOKEN");
            process.Start();
            process.StandardInput.Close();
            using var drains = CancellationTokenSource.CreateLinkedTokenSource(token);
            var output = DrainLoginOutputAsync(process.StandardOutput, publish, drains.Token);
            var error = DrainLoginOutputAsync(process.StandardError, publish, drains.Token);
            try
            {
                var exited = process.WaitForExitAsync(token);
                var pending = new List<Task> { exited, output, error };
                while (pending.Count > 0)
                {
                    var completed = await Task.WhenAny(pending);
                    await completed;
                    pending.Remove(completed);
                    if (completed == exited) drains.CancelAfter(TimeSpan.FromSeconds(3));
                }
                return process.ExitCode;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                drains.Cancel();
                try { await Task.WhenAll(output, error); }
                catch (OperationCanceledException) when (drains.IsCancellationRequested) { }
            }
        }

        private static async Task DrainLoginOutputAsync(StreamReader reader,
            Func<CodexLoginInstructions, CancellationToken, Task> publish, CancellationToken token)
        {
            var buffer = new char[256];
            var recent = string.Empty;
            var published = false;
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
            {
                if (published) continue;
                recent += new string(buffer, 0, count);
                if (recent.Length > 4096) recent = recent[^4096..];
                var plain = Regex.Replace(recent, @"\x1B\[[0-9;]*m", "");
                if (!Regex.IsMatch(plain, @"(?<!\S)https://github\.com/login/device(?=\s|$)")) continue;
                var code = Regex.Match(plain, @"(?<![A-Z0-9-])[A-Z0-9]{4}-[A-Z0-9]{4,5}(?![A-Z0-9-])");
                if (!code.Success) continue;
                await publish(new(VerificationUri, code.Value), token);
                published = true;
                recent = string.Empty;
            }
        }
    }

    private static SshPublicIdentity ParseIdentity(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3 || parts[0] != "ssh-ed25519" || parts.Length == 3 && parts[2] != "codex-provisioning")
            throw new IOException("Invalid managed public key.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(parts[1]); }
        catch (FormatException) { throw new IOException("Invalid managed public key."); }
        // RFC 4253 string("ssh-ed25519"), string(32-byte public key).
        ReadOnlySpan<byte> prefix = [0, 0, 0, 11, .. Encoding.ASCII.GetBytes("ssh-ed25519"), 0, 0, 0, 32];
        if (bytes.Length != 51 || !bytes.AsSpan(0, 19).SequenceEqual(prefix)) throw new IOException("Invalid managed public key.");
        return new("ssh-ed25519 " + Convert.ToBase64String(bytes), "SHA256:" + Convert.ToBase64String(SHA256.HashData(bytes)).TrimEnd('='));
    }

    public static bool ValidIdentity(SshPublicIdentity identity)
    {
        if (identity.PublicKey is null || identity.PublicKey.Length > 100 || identity.Fingerprint is null) return false;
        try { return ParseIdentity(identity.PublicKey) == identity; }
        catch (IOException) { return false; }
    }

    private static bool HasUnrelatedAuthentication(string managedDirectory)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configured = Environment.GetEnvironmentVariable("GH_CONFIG_DIR");
        var candidates = new[] { configured, Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg, "gh") };
        return candidates.OfType<string>().Where(path => !string.IsNullOrEmpty(path)).Any(path =>
            !Path.GetFullPath(path).Equals(Path.GetFullPath(managedDirectory), StringComparison.Ordinal) &&
            (File.Exists(Path.Combine(path, "hosts.yml")) || new FileInfo(Path.Combine(path, "hosts.yml")).LinkTarget is not null));
    }

    private static async Task VerifyGitHubOwnershipAsync(string directory, CancellationToken token)
    {
        var marker = Path.Combine(directory, "managed-by-codex");
        VerifyPath(marker);
        if (!OperatingSystem.IsLinux() || File.GetUnixFileMode(directory) != DirectoryMode ||
            !File.Exists(marker) || new FileInfo(marker).Length != Ownership.Length || await File.ReadAllTextAsync(marker, token) != Ownership)
            throw new IOException("Product GitHub authentication ownership or permissions are invalid.");
    }

    private static void VerifyPath(string path)
    {
        // Reject links at every level, including dangling links. A path in an unrelated
        // SSH/config directory can never be supplied through the protocol.
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (new FileInfo(current).LinkTarget is not null) throw new IOException("Linked authentication paths are not supported.");
    }

    private static void CreatePrivateDirectory(string path)
    {
        VerifyPath(path);
        if (!OperatingSystem.IsLinux()) throw new IOException("Unsupported authentication platform.");
        Directory.CreateDirectory(path, DirectoryMode);
        File.SetUnixFileMode(path, DirectoryMode);
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static ProvisioningCommandReport Success(SshPublicIdentity? identity = null) => new(ProvisioningCommandStatus.Succeeded, ProvisioningDiagnostic.Completed, identity);
    private static ProvisioningCommandReport Failure(ProvisioningDiagnostic diagnostic) => new(ProvisioningCommandStatus.Failed, diagnostic);

    private async Task<(int ExitCode, string Output)> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["LC_ALL"] = "C";
        if (Path.GetFileName(executable) == "gh")
        {
            process.StartInfo.Environment["GH_CONFIG_DIR"] = Path.Combine(_root, "github");
            process.StartInfo.Environment.Remove("GH_TOKEN");
            process.StartInfo.Environment.Remove("GITHUB_TOKEN");
        }
        // Verification cannot inherit URL rewrites, repository identity, or SSH overrides.
        foreach (var variable in process.StartInfo.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            process.StartInfo.Environment.Remove(variable);
        process.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        process.StartInfo.Environment["GIT_CONFIG_SYSTEM"] = "/dev/null";
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.Start();
        process.StandardInput.Close();
        using var drain = new CancellationTokenSource();
        var stdout = DrainAsync(process.StandardOutput, drain.Token);
        var stderr = DrainAsync(process.StandardError, drain.Token);
        try
        {
            await process.WaitForExitAsync(token);
            drain.CancelAfter(TimeSpan.FromSeconds(3));
            try { await Task.WhenAll(stdout, stderr); }
            catch (OperationCanceledException) when (drain.IsCancellationRequested) { throw new IOException("Provisioning output drain exceeded its deadline."); }
            return (process.ExitCode, await stdout);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            drain.CancelAfter(TimeSpan.FromSeconds(3));
            try { await Task.WhenAll(stdout, stderr); }
            catch (OperationCanceledException) when (drain.IsCancellationRequested) { }
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
            if (result.Length < 1024) result.Append(buffer, 0, Math.Min(count, 1024 - result.Length));
        return result.ToString();
    }
}
