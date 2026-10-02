namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexServer;
using System.Text.Json;

public sealed class NodeGitHubSetupTests
{
    [Theory]
    [InlineData("owner/repository", true)]
    [InlineData("owner/repository.git", true)]
    [InlineData("owner/repo;echo secret", false)]
    [InlineData("--upload-pack=command", false)]
    [InlineData("https://other.example/repo", false)]
    [InlineData("owner/..", false)]
    [InlineData("owner/repo\n", false)]
    public void RepositoryBoundaryAcceptsOnlyGitHubIdentifiers(string repository, bool valid)
    {
        Assert.Equal(valid, ProvisioningCommandProtocol.Valid(new("server", "git", ProvisioningCommandAction.VerifyRepositoryAccess, Repository: repository)));
        Assert.False(ProvisioningCommandProtocol.Valid(new("server", "git", ProvisioningCommandAction.Detect, Repository: repository)));
        Assert.False(ProvisioningCommandProtocol.Supported(new("server", "github-cli", ProvisioningCommandAction.GenerateSshKey)));
    }

    [Theory]
    [InlineData("prepareauthentication")]
    [InlineData("generatesshkey")]
    [InlineData("inspectsshkey")]
    [InlineData("removesshkey")]
    [InlineData("verifyrepositoryaccess")]
    public void InventoryAcceptsRegisteredSetupOperationNames(string action)
    {
        var state = CapabilityCatalog.Unknown(CapabilityCatalog.Definitions[0]) with
        {
            Operation = new(CapabilityOperationState.Running, action)
        };
        Assert.True(CapabilityCatalog.ValidInventory([state]));
    }

    [Fact]
    public async Task KeyLifecycleExportsOnlyPublicIdentityAndRequiresExplicitReplacement()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var node = new TestNode();
        var setup = new NodeGitHubSetup(node.Root, node.RunAsync, unrelatedAuthentication: () => false);
        var generated = await setup.ExecuteAsync(Request(ProvisioningCommandAction.GenerateSshKey), CancellationToken.None);
        Assert.Equal(ProvisioningCommandStatus.Succeeded, generated.Status);
        Assert.NotNull(generated.PublicIdentity);
        Assert.True(NodeGitHubSetup.ValidIdentity(generated.PublicIdentity));
        Assert.StartsWith("SHA256:", generated.PublicIdentity.Fingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain("private-sentinel", JsonSerializer.Serialize(generated), StringComparison.Ordinal);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(node.Key));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(node.Key) ?? throw new InvalidOperationException()));
        var duplicate = await setup.ExecuteAsync(Request(ProvisioningCommandAction.GenerateSshKey), CancellationToken.None);
        Assert.Equal(ProvisioningDiagnostic.Denied, duplicate.Diagnostic);
        Assert.Equal(1, node.Generations);
        var inspected = await setup.ExecuteAsync(Request(ProvisioningCommandAction.InspectSshKey), CancellationToken.None);
        Assert.Equal(generated.PublicIdentity, inspected.PublicIdentity);
        var verified = await setup.ExecuteAsync(Request(ProvisioningCommandAction.VerifyRepositoryAccess) with { Repository = "owner/repo" }, CancellationToken.None);
        Assert.Equal(ProvisioningCommandStatus.Succeeded, verified.Status);
        Assert.Contains(node.Calls, call => call.Tool == "/usr/bin/git" && call.Args.Contains("git@github.com:owner/repo.git") &&
            call.Args.Any(arg => arg.Contains("StrictHostKeyChecking=yes", StringComparison.Ordinal) && arg.Contains("IdentitiesOnly=yes", StringComparison.Ordinal)));
        Assert.Equal(ProvisioningCommandStatus.Succeeded, (await setup.ExecuteAsync(Request(ProvisioningCommandAction.RemoveSshKey), CancellationToken.None)).Status);
        Assert.False(File.Exists(node.Key));
        Assert.False(File.Exists(node.Key + ".pub"));
        Assert.Equal(ProvisioningCommandStatus.Succeeded, (await setup.ExecuteAsync(Request(ProvisioningCommandAction.GenerateSshKey), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task KeysWithUnknownOwnershipBadPermissionsOrMismatchedPairsArePreserved()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var node = new TestNode();
        var setup = new NodeGitHubSetup(node.Root, node.RunAsync, unrelatedAuthentication: () => false);
        await setup.ExecuteAsync(Request(ProvisioningCommandAction.GenerateSshKey), CancellationToken.None);
        await File.WriteAllTextAsync(node.Key + ".pub", TestNode.PublicKey + " unrelated");
        await Assert.ThrowsAsync<IOException>(() => setup.ExecuteAsync(Request(ProvisioningCommandAction.RemoveSshKey), CancellationToken.None));
        await File.WriteAllTextAsync(node.Key + ".pub", TestNode.PublicKey + " codex-provisioning");
        File.SetUnixFileMode(node.Key, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        await Assert.ThrowsAsync<IOException>(() => setup.ExecuteAsync(Request(ProvisioningCommandAction.InspectSshKey), CancellationToken.None));
        File.SetUnixFileMode(node.Key, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        node.Mismatch = true;
        await Assert.ThrowsAsync<IOException>(() => setup.ExecuteAsync(Request(ProvisioningCommandAction.RemoveSshKey), CancellationToken.None));
        Assert.True(File.Exists(node.Key));
    }

    [Fact]
    public async Task AuthenticationLogoutTouchesOnlyProductFilesAndRejectsLinkedPaths()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var node = new TestNode();
        var setup = new NodeGitHubSetup(node.Root, node.RunAsync, unrelatedAuthentication: () => false);
        var request = new ProvisioningCommandRequest("server", "github-cli", ProvisioningCommandAction.PrepareAuthentication);
        Assert.Equal(ProvisioningCommandStatus.Succeeded, (await setup.ExecuteAsync(request, CancellationToken.None)).Status);
        var managed = Path.Combine(node.Root, "github");
        var unrelated = Path.Combine(node.Root, "operator-auth");
        await File.WriteAllTextAsync(unrelated, "unrelated");
        var hosts = Path.Combine(managed, "hosts.yml");
        await File.WriteAllTextAsync(hosts, "private-sentinel");
        Assert.Equal(ProvisioningCommandStatus.Succeeded, (await setup.ExecuteAsync(request with { Action = ProvisioningCommandAction.Logout }, CancellationToken.None)).Status);
        Assert.False(File.Exists(hosts));
        Assert.Equal("unrelated", await File.ReadAllTextAsync(unrelated));
        File.CreateSymbolicLink(hosts, unrelated);
        await Assert.ThrowsAsync<IOException>(() => setup.ExecuteAsync(request with { Action = ProvisioningCommandAction.Logout }, CancellationToken.None));
        Assert.Equal("unrelated", await File.ReadAllTextAsync(unrelated));
        Assert.Equal(2, node.Calls.Count);
    }

    [Fact]
    public async Task SetupRefusesUnrelatedAuthenticationBeforeCreatingProductState()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var node = new TestNode();
        var setup = new NodeGitHubSetup(node.Root, node.RunAsync, unrelatedAuthentication: () => true);
        var result = await setup.ExecuteAsync(new("server", "github-cli", ProvisioningCommandAction.PrepareAuthentication), CancellationToken.None);
        Assert.Equal(ProvisioningDiagnostic.Denied, result.Diagnostic);
        Assert.False(Directory.Exists(node.Root));
        Assert.Empty(node.Calls);
    }

    [Fact]
    public async Task GitHubLoginUsesPreparedProductDirectoryPublishesOnlyDeviceChallengeAndVerifiesStatus()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var node = new TestNode();
        var managedDirectory = Path.Combine(node.Root, "github");
        string? loginDirectory = null;
        var setup = new NodeGitHubSetup(node.Root,
            (_, arguments, _) =>
            {
                node.Calls.Add(("/usr/bin/gh", arguments));
                return Task.FromResult((0, ""));
            }, unrelatedAuthentication: () => false,
            login: async (directory, publish, token) =>
            {
                loginDirectory = directory;
                await publish(new("https://github.com/login/device", "ABCD-1234"), token);
                return 0;
            });
        var request = new ProvisioningCommandRequest("server", "github-cli", ProvisioningCommandAction.PrepareAuthentication);
        Assert.Equal(ProvisioningCommandStatus.Succeeded, (await setup.ExecuteAsync(request, CancellationToken.None)).Status);

        CodexLoginInstructions? observed = null;
        var login = await setup.ExecuteAsync(request with { Action = ProvisioningCommandAction.Login }, CancellationToken.None,
            instructions => { observed = instructions; return Task.CompletedTask; });

        Assert.Equal(ProvisioningCommandStatus.Succeeded, login.Status);
        Assert.Equal(managedDirectory, loginDirectory);
        Assert.Equal(new CodexLoginInstructions("https://github.com/login/device", "ABCD-1234"), observed);
        var verification = Assert.Single(node.Calls);
        Assert.Equal("/usr/bin/gh", verification.Tool);
        Assert.Equal(new[] { "auth", "status", "--hostname", "github.com" }, verification.Args);
        Assert.DoesNotContain("ABCD-1234", JsonSerializer.Serialize(login), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHubLoginFailureAndCancellationDoNotReportAuthenticationSuccess()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var node = new TestNode();
        var setup = new NodeGitHubSetup(node.Root, (_, _, _) => Task.FromResult((1, "private-token")),
            unrelatedAuthentication: () => false,
            login: (_, _, _) => Task.FromResult(1));
        var request = new ProvisioningCommandRequest("server", "github-cli", ProvisioningCommandAction.PrepareAuthentication);
        await setup.ExecuteAsync(request, CancellationToken.None);
        var failed = await setup.ExecuteAsync(request with { Action = ProvisioningCommandAction.Login }, CancellationToken.None,
            _ => Task.CompletedTask);
        Assert.Equal(ProvisioningCommandStatus.Failed, failed.Status);
        Assert.DoesNotContain("private-token", JsonSerializer.Serialize(failed), StringComparison.Ordinal);

        using var cancellation = new CancellationTokenSource();
        var cancelling = new NodeGitHubSetup(node.Root, unrelatedAuthentication: () => false,
            login: (_, _, token) => { cancellation.Cancel(); return Task.FromCanceled<int>(token); });
        var executor = new NodeProvisioningCommandExecutor(
            new NodeCapabilityDiscovery((_, _, _) => Task.FromResult((0, "1.0"))), githubSetup: cancelling);
        var now = DateTimeOffset.UtcNow;
        var command = new ProvisioningCommand(Guid.NewGuid().ToString("N"), request with { Action = ProvisioningCommandAction.Login },
            now, ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing, now, now.AddMinutes(1));
        var cancelled = await executor.ExecuteAsync(command, permitted: true, cancellationToken: cancellation.Token,
            reportProgress: (_, _) => Task.CompletedTask);
        Assert.Equal(ProvisioningCommandStatus.Cancelled, cancelled.Status);
    }

    [Fact]
    public async Task ExecutorDenialCancellationAndFailureDoNotLeakOutputOrRunUnpermittedSetup()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var node = new TestNode();
        var discovery = new NodeCapabilityDiscovery((_, _, _) => Task.FromResult((0, "1.0")));
        var setup = new NodeGitHubSetup(node.Root, node.RunAsync, unrelatedAuthentication: () => false);
        var executor = new NodeProvisioningCommandExecutor(discovery, githubSetup: setup);
        var now = DateTimeOffset.UtcNow;
        var command = new ProvisioningCommand(Guid.NewGuid().ToString("N"), Request(ProvisioningCommandAction.GenerateSshKey), now,
            ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing, now, now.AddMinutes(1));
        Assert.Equal(ProvisioningDiagnostic.Denied, (await executor.ExecuteAsync(command, false)).Diagnostic);
        Assert.Empty(node.Calls);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Equal(ProvisioningDiagnostic.Cancelled, (await executor.ExecuteAsync(command, true, cancelled.Token)).Diagnostic);
        Assert.Empty(node.Calls);
        node.Fail = true;
        var report = await executor.ExecuteAsync(command, true);
        Assert.Equal(ProvisioningDiagnostic.ProcessFailed, report.Diagnostic);
        Assert.Null(report.PublicIdentity);
        Assert.DoesNotContain("private-sentinel", JsonSerializer.Serialize(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoreValidatesPublicReportsAndRetainsMetadataAcrossRestart()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var node = new TestNode();
        var report = await new NodeGitHubSetup(node.Root, node.RunAsync, unrelatedAuthentication: () => false).ExecuteAsync(Request(ProvisioningCommandAction.GenerateSshKey), CancellationToken.None);
        var database = Path.Combine(node.Root, "commands.db");
        var store = new ProvisioningCommandStore(database);
        await store.InitializeAsync();
        var created = await store.CreateAsync(Request(ProvisioningCommandAction.GenerateSshKey));
        await store.ClaimAsync("server");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReportAsync(created.Id, "server", report with { PublicIdentity = new("private-sentinel", "private-sentinel") }));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReportAsync(created.Id, "server", report with { PublicIdentity = null }));
        await store.ReportAsync(created.Id, "server", report);
        var restarted = new ProvisioningCommandStore(database);
        Assert.Equal(report.PublicIdentity, Assert.Single(await restarted.ListAsync()).PublicIdentity);
        var detect = await store.CreateAsync(Request(ProvisioningCommandAction.Detect));
        await store.ClaimAsync("server");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReportAsync(detect.Id, "server", report));
    }

    private static ProvisioningCommandRequest Request(ProvisioningCommandAction action) => new("server", "git", action);

    private sealed class TestNode : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "github-setup-" + Guid.NewGuid().ToString("N"));
        public string Key => Path.Combine(Root, "ssh", "github_ed25519");
        public int Generations { get; private set; }
        public bool Mismatch { get; set; }
        public bool Fail { get; set; }
        public List<(string Tool, IReadOnlyList<string> Args)> Calls { get; } = [];
        public static string PublicKey => "ssh-ed25519 " + Convert.ToBase64String([0, 0, 0, 11, .. System.Text.Encoding.ASCII.GetBytes("ssh-ed25519"), 0, 0, 0, 32, .. new byte[32]]);
        public async Task<(int ExitCode, string Output)> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls.Add((executable, arguments));
            if (Fail) return (1, "private-sentinel");
            if (executable == "/usr/bin/gh")
            {
                if (arguments.Contains("logout"))
                {
                    File.Delete(Path.Combine(Root, "github", "hosts.yml"));
                    return (0, "private-sentinel");
                }
                return (1, "private-sentinel");
            }
            if (arguments.Contains("-t"))
            {
                Generations++;
                await File.WriteAllTextAsync(Key, "private-sentinel", token);
                await File.WriteAllTextAsync(Key + ".pub", PublicKey + " codex-provisioning", token);
            }
            if (arguments.Contains("-y")) return (0, Mismatch ? "invalid" : PublicKey);
            return (0, "private-sentinel");
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
