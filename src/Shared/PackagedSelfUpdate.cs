namespace CodexProvisioning;

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

public sealed class PackagedSelfUpdate(HttpClient http) : ISelfUpdateOperations
{
    private const string Repository = "duned/codex-worker";

    public async Task<UpdateRelease> FindReleaseAsync(UpdateComponent component, CancellationToken cancellationToken)
    {
        if (component.Name is not ("server" or "worker")) throw new ArgumentException("Unsupported update component.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        UpdateRelease? selected = null;
        SemanticReleaseVersion? selectedVersion = null;
        // Enumerate published releases, filtering at the boundary rather than trusting tags or flags.
        // Bound discovery; an unexpectedly large catalog must not silently select an incomplete result.
        for (var page = 1; page <= 10; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases?per_page=100&page={page}");
            request.Headers.UserAgent.ParseAdd("Codex-Self-Update/1");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await ReadBoundedAsync(response, 4 * 1024 * 1024, deadline.Token));
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) continue;
                var tag = release.GetProperty("tag_name").GetString() ?? "";
                if (!tag.StartsWith('v')) continue;
                var version = tag[1..];
                SemanticReleaseVersion semantic;
                try { semantic = SemanticReleaseVersion.Parse(version); }
                catch (ArgumentException) { continue; }
                if (!semantic.IsStable || version.Contains('+', StringComparison.Ordinal)) continue;
                var assets = release.GetProperty("assets").EnumerateArray().Select(asset => asset.GetProperty("name").GetString()).ToHashSet(StringComparer.Ordinal);
                if (!assets.Contains($"codex-{component.Name}-{version}-linux-x64.tar.gz") || !assets.Contains("checksums.txt")) continue;
                if (selectedVersion is null || semantic.CompareTo(selectedVersion) > 0)
                {
                    selected = new(version);
                    selectedVersion = semantic;
                }
            }
            if (document.RootElement.GetArrayLength() < 100)
                return selected ?? throw new InvalidOperationException("No valid stable published release with the component package and checksums was found. Installation unchanged.");
        }
        throw new InvalidOperationException("Release catalog exceeded the bounded discovery limit. Installation unchanged.");
    }

    public async Task<string> InstallAsync(UpdateComponent component, UpdateRelease release, CancellationToken cancellationToken)
    {
        if (component.Name is not ("server" or "worker")) throw new ArgumentException("Unsupported update component.");
        var semantic = SemanticReleaseVersion.Parse(release.Version);
        if (!semantic.IsStable || release.Version.Contains('+', StringComparison.Ordinal))
            throw new ArgumentException("Unsupported installer version.");
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw new InvalidOperationException("Self-update requires the packaged Linux x64 installation.");
        if (!File.Exists(component.InstalledExecutable))
            throw new InvalidOperationException("Packaged installation not found. Install through the documented Linux installer first.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        if ((await RunProcessAsync("/usr/bin/id", ["-u"], deadline.Token)).Trim() != "0")
            throw new InvalidOperationException("Updating requires root. Run the update command with sudo.");
        var directory = Path.Combine(Path.GetTempPath(), "codex-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            // Download the selected tag's installer completely before executing it. Never pipe a partial response to bash.
            using var downloadDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            downloadDeadline.CancelAfter(TimeSpan.FromSeconds(60));
            using var response = await http.GetAsync($"https://raw.githubusercontent.com/{Repository}/v{release.Version}/packaging/linux/{component.Installer}",
                HttpCompletionOption.ResponseHeadersRead, downloadDeadline.Token);
            response.EnsureSuccessStatusCode();
            var script = await ReadBoundedAsync(response, 1024 * 1024, downloadDeadline.Token);
            if (!script.StartsWith("#!/usr/bin/env bash\n", StringComparison.Ordinal))
                throw new InvalidOperationException("Release installer is invalid. Installation unchanged.");
            var path = Path.Combine(directory, component.Installer);
            await File.WriteAllTextAsync(path, script, downloadDeadline.Token);
            var arguments = new List<string> { path, "--version", release.Version };
            if (component.Name == "server") arguments.Add("--non-interactive");
            await RunProcessAsync("/bin/bash", arguments, deadline.Token);
            var installed = (await RunProcessAsync(component.InstalledExecutable, ["--version"], deadline.Token)).Trim();
            var prefix = component.DisplayName + " ";
            if (!installed.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException("Could not verify the installed version. Inspect installation and service status.");
            return installed[prefix.Length..];
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, token)) != 0)
        {
            if (buffer.Length + count > limit) throw new InvalidOperationException("Release response exceeded the supported size. Installation unchanged.");
            buffer.Write(chunk, 0, count);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<string> RunProcessAsync(string executable, IEnumerable<string> arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // The installer has no need for service secrets or caller authentication.
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";
        start.Environment["LANG"] = "C.UTF-8";
        using var process = new Process { StartInfo = start };
        token.ThrowIfCancellationRequested();
        process.Start();
        process.StandardInput.Close();
        var output = DrainAsync(process.StandardOutput, token);
        var error = DrainAsync(process.StandardError, token);
        try
        {
            await process.WaitForExitAsync(token);
            await Task.WhenAll(output, error);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            // Join terminated child and readers without allowing a cleanup error to hide the original failure.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(cleanup.Token); await Task.WhenAll(output, error).WaitAsync(cleanup.Token); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException) { }
            throw;
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Update subprocess failed (exit {process.ExitCode}). Inspect service status and journalctl; rerun the documented installer for detailed diagnostics. Configuration and recovery data remain installer-owned.");
        return await output;
    }

    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken token)
    {
        var retained = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
            if (retained.Length < 1024) retained.Append(buffer, 0, Math.Min(count, 1024 - retained.Length));
        return retained.ToString();
    }
}

public static class SelfUpdateCommand
{
    public static async Task<int> RunAsync(UpdateComponent component, string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            return await new SelfUpdateCli(new PackagedSelfUpdate(http), Console.In, Console.Out, Console.Error,
                !Console.IsInputRedirected).RunAsync(component, args, cancellation.Token);
        }
        finally { Console.CancelKeyPress -= handler; }
    }
}
