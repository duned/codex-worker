using CodexWorker;

namespace CodexWorker.Tests;

[Collection("ServerTokenEnvironment")]
public sealed class CodexServiceEnvironmentTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData(null, 1)]
    [InlineData("issue-model", 0)]
    [InlineData("project-model", 0)]
    public async Task ActualInvocationReportsCliModelWithoutForcingSelection(string? explicitModel, int exitCode)
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = CreateDirectory();
        var previous = Environment.GetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE");
        var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            var executable = Path.Combine(directory, "codex");
            await File.WriteAllTextAsync(executable, """
                #!/bin/sh
                printf '%s\n' "$@" > "$0.args"
                printf '%s' "$CODEX_HOME" > "$0.home"
                printf 'OpenAI Codex v1\n--------\nmodel: account-model\n--------\n' >&2
                while [ "$#" -gt 0 ]; do
                  if [ "$1" = --output-last-message ]; then shift; output=$1; fi
                  shift
                done
                printf '%s' '{"status":"success","summary":"Done","testsOrValidationPerformed":[],"needsHumanInput":false,"question":null,"blockerType":null}' > "$output"
                exit EXIT_CODE
                """.Replace("EXIT_CODE", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", executable);
            Environment.SetEnvironmentVariable("CODEX_HOME", directory);
            var instructions = Path.Combine(directory, "AGENTS.md");
            await File.WriteAllTextAsync(instructions, "Project instructions");
            var observed = new List<string?>();
            var executor = new CodexExecutor(new ProcessRunner(), new CodexSettings { Model = explicitModel })
                .WithModelObserver(observed.Add);
            var issue = new GitHubIssue(1, "Task", "Task body", DateTimeOffset.UnixEpoch);
            if (exitCode == 0) await executor.RunAsync(directory, instructions, issue, CancellationToken.None);
            else await Assert.ThrowsAsync<CodexExecutionInfrastructureException>(() => executor.RunAsync(directory, instructions, issue, CancellationToken.None));
            if (explicitModel is null) Assert.Equal("account-model", Assert.Single(observed));
            else Assert.Empty(observed);
            var args = await File.ReadAllLinesAsync(executable + ".args");
            Assert.Equal(explicitModel is not null, args.Contains("--model", StringComparer.Ordinal));
            if (explicitModel is not null) Assert.Contains(explicitModel, args);
            Assert.Contains("model_reasoning_effort=\"medium\"", args);
            Assert.Equal(directory, await File.ReadAllTextAsync(executable + ".home"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", previous);
            Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ExecutionProfileControlsImplementationRepairIntegrationRepairAndConflictInvocations()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = CreateDirectory();
        var previous = Environment.GetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE");
        try
        {
            var executable = Path.Combine(directory, "codex");
            await File.WriteAllTextAsync(executable, """
                #!/bin/sh
                printf '%s\n' "$@" >> "$0.args"
                use_stdin=false
                while [ "$#" -gt 0 ]; do
                  if [ "$1" = --output-last-message ]; then shift; output=$1; fi
                  if [ "$1" = - ]; then use_stdin=true; fi
                  shift
                done
                if [ "$use_stdin" = true ]; then cat > "$0.prompt"; fi
                printf '%s' '{"status":"success","summary":"Done","testsOrValidationPerformed":[],"needsHumanInput":false,"question":null,"blockerType":null}' > "$output"
                """);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", executable);
            var instructions = Path.Combine(directory, "AGENTS.md");
            await File.WriteAllTextAsync(instructions, "Project instructions");
            var defaults = new CodexSettings { Model = "project-model", ReasoningEffort = "high" };
            var executor = new CodexExecutor(new ProcessRunner(), defaults);
            var scoped = executor.WithProfile(new CodexExecutionProfile("task-model", "low"));
            var issue = new GitHubIssue(1, "Task", "Task body", DateTimeOffset.UnixEpoch);
            await scoped.RunAsync(directory, instructions, issue, null, false, 1, CancellationToken.None);
            await scoped.RepairAsync(directory, instructions, issue,
                new ValidationFailure(1, "check", 1, "failed", "", false), 1, 2, CancellationToken.None);
            await scoped.ResolveIntegrationConflictAsync(directory, instructions, issue, "Conflict details", CancellationToken.None);
            var largeDiagnostic = "first actionable failure\n" + new string('a', 160_000) + "\nlast actionable failure";
            var repairContext = new IntegrationRepairContext(new ValidationFailure(1, "check", 1, largeDiagnostic, "stderr detail", false),
                "main", "original-base", "implementation", "integrated-base", "rebased-source");
            await scoped.RepairIntegrationAsync(directory, instructions, issue, repairContext, "Implemented", ["check"], 1, 2, CancellationToken.None);
            var deliveredPrompt = await File.ReadAllTextAsync(executable + ".prompt");
            Assert.Contains(largeDiagnostic, deliveredPrompt);
            Assert.Contains("Effective Codex model: task-model; reasoning effort: low", deliveredPrompt);
            Assert.Contains("rebased-source", deliveredPrompt);
            var arguments = await File.ReadAllLinesAsync(executable + ".args");
            Assert.Equal(4, arguments.Count(argument => argument == "task-model"));
            Assert.Equal(4, arguments.Count(argument => argument == "model_reasoning_effort=\"low\""));
            Assert.DoesNotContain("project-model", arguments);
            Assert.Equal("project-model", defaults.Model);
            Assert.Equal("high", defaults.ReasoningEffort);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", previous);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ResolutionUsesChildPathAndDoesNotDependOnInteractivePath()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = CreateDirectory();
        try
        {
            var executable = Path.Combine(directory, "codex-test-cli");
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nprintf service-cli");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var runner = new ProcessRunner();
            var result = await runner.RunAsync("codex-test-cli", [], directory,
                environment: new Dictionary<string, string?> { ["PATH"] = directory });
            Assert.Equal("service-cli", result.StandardOutput);
            var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync("codex-test-cli", [], directory,
                environment: new Dictionary<string, string?> { ["PATH"] = "/usr/bin:/bin" }));
            Assert.Contains("effective service PATH", missing.Message);
            Assert.Contains("absolute service-accessible executable path", missing.Message);
            var codexMissing = Assert.Throws<InvalidOperationException>(() => ProcessRunner.ResolveExecutable("codex", directory, ""));
            Assert.Contains("CODEX_WORKER_CODEX_EXECUTABLE", codexMissing.Message);
            Assert.Throws<InvalidOperationException>(() => ProcessRunner.ResolveExecutable("genuinely-absent-cli", directory, directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task LaunchDiagnosticsSeparateMissingDirectoryInterpreterAndPermissions()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = CreateDirectory();
        try
        {
            var runner = new ProcessRunner();
            var missingDirectory = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync("/bin/sh", [], Path.Combine(directory, "absent")));
            Assert.Contains("Working directory", missingDirectory.Message);
            var executable = Path.Combine(directory, "cli");
            await File.WriteAllTextAsync(executable, "#!/nonexistent-codex-interpreter\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var interpreter = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(executable, [], directory));
            Assert.Contains("shebang interpreter", interpreter.Message);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead);
            var permissions = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(executable, [], directory));
            Assert.Contains("Permission denied", permissions.Message);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ExplicitCliPreflightRetainsAccessibleDirectoryUntilExitAndCleansItOnColdStarts()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = CreateDirectory();
        var previous = Environment.GetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE");
        try
        {
            var executable = Path.Combine(directory, "codex");
            await File.WriteAllTextAsync(executable, """
                #!/bin/sh
                test -d "$PWD" || exit 10
                test -w "$PWD" || exit 11
                printf '%s' "$PWD" > "$0.directory"
                while [ "$#" -gt 0 ]; do
                  if [ "$1" = --output-last-message ]; then shift; output=$1; fi
                  shift
                done
                sleep 0.05
                test -d "$PWD" || exit 12
                printf OK > "$output"
                """);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", executable);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                Assert.Equal(ProcessExitCodes.Success, await Program.Main(["--codex-preflight"]));
                var workingDirectory = await File.ReadAllTextAsync(executable + ".directory");
                Assert.Contains("codex-worker-preflight-", workingDirectory);
                Assert.False(Directory.Exists(workingDirectory));
            }
            Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", Path.Combine(directory, "absent"));
            Assert.Equal(ProcessExitCodes.StartupFailure, await Program.Main(["--codex-preflight"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", previous);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task PreflightUsesEffectivePathOnEachColdStartAndReportsMissingCapability()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = CreateDirectory();
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        var previousExecutable = Environment.GetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE");
        try
        {
            var executable = Path.Combine(directory, "codex");
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nwhile [ \"$#\" -gt 0 ]; do if [ \"$1\" = --output-last-message ]; then shift; output=$1; fi; shift; done\nprintf OK > \"$output\"\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", null);
            Environment.SetEnvironmentVariable("PATH", directory);
            for (var attempt = 0; attempt < 2; attempt++)
                await new CodexExecutor(new ProcessRunner(), new CodexSettings()).PreflightAsync(CancellationToken.None);

            // A CLI in an unrelated user's directory is excluded from the effective service PATH.
            Environment.SetEnvironmentVariable("PATH", Path.Combine(directory, "unrelated-service-path"));
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var failure = await Assert.ThrowsAsync<WorkerInfrastructureException>(() =>
                    new CodexExecutor(new ProcessRunner(), new CodexSettings()).PreflightAsync(CancellationToken.None));
                Assert.Contains("not installed/resolvable", failure.Message);
                Assert.Contains("Codex execution capability unavailable", failure.Message);
                Assert.Contains("registration/identity remain valid", failure.Message);
            }
            File.SetUnixFileMode(executable, UnixFileMode.UserRead);
            var unusable = Assert.Throws<InvalidOperationException>(() => ProcessRunner.ResolveExecutable("codex", directory, directory));
            Assert.Contains("present but not executable", unusable.Message);
            Assert.Throws<InvalidOperationException>(() => ProcessRunner.ResolveExecutable("codex", directory, "."));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", previousExecutable);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task PreflightDirectoryCreationFailureIsDistinctFromExecutableFailure()
    {
        var directory = CreateDirectory();
        try
        {
            var file = Path.Combine(directory, "not-a-directory");
            await File.WriteAllTextAsync(file, "");
            var failure = await Assert.ThrowsAsync<WorkerInfrastructureException>(() =>
                new CodexExecutor(new ProcessRunner(), new CodexSettings()).PreflightAsync(CancellationToken.None, 60, file));
            Assert.Contains("temporary working directory could not be created/accessed", failure.Message);
            Assert.Contains("TMPDIR", failure.Message);
            Assert.Contains("registration/identity remain valid", failure.Message);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task InaccessibleDirectoryIsReportedAsPermissionFailureRatherThanMissingCodex()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        var directory = CreateDirectory();
        var executable = Path.Combine(directory, "codex");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.None);
            var inaccessible = Assert.Throws<InvalidOperationException>(() =>
                ProcessRunner.ResolveExecutable(executable, Path.GetTempPath(), null));
            Assert.Contains("Executable path is inaccessible", inaccessible.Message);
            var failure = await Assert.ThrowsAsync<WorkerInfrastructureException>(() =>
                new CodexExecutor(new ProcessRunner(), new CodexSettings()).PreflightAsync(CancellationToken.None, 60, directory));
            Assert.Contains("temporary working directory could not be created/accessed", failure.Message);
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(7, "preflight exited with code 7")]
    [InlineData(0, "unexpected response")]
    public async Task FailedProcessAndUnexpectedResponseDoNotExposeAuthenticationMaterial(int exitCode, string expected)
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = CreateDirectory();
        var previous = Environment.GetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE");
        try
        {
            var executable = Path.Combine(directory, "codex");
            await File.WriteAllTextAsync(executable, $"""
                #!/bin/sh
                printf '%s' "$PWD" > "$0.directory"
                while [ "$#" -gt 0 ]; do
                  if [ "$1" = --output-last-message ]; then shift; output=$1; fi
                  shift
                done
                printf 'opaque-authentication-material' > "$output"
                printf 'opaque-authentication-material' >&2
                exit {exitCode}
                """);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", executable);
            var failure = await Assert.ThrowsAsync<WorkerInfrastructureException>(() =>
                new CodexExecutor(new ProcessRunner(), new CodexSettings()).PreflightAsync(CancellationToken.None));
            Assert.Contains("Codex process started", failure.Message);
            Assert.Contains(expected, failure.Message);
            Assert.DoesNotContain("opaque-authentication-material", failure.Message);
            Assert.False(Directory.Exists(await File.ReadAllTextAsync(executable + ".directory")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", previous);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ProvisionedCliUsesSameExecutableAndExistingAuthenticationForPreflightAndTasks()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = CreateDirectory();
        var previousExecutable = Environment.GetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE");
        var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            var executable = Path.Combine(directory, "codex");
            var codexHome = Path.Combine(directory, "existing-codex-home");
            Directory.CreateDirectory(codexHome);
            await File.WriteAllTextAsync(Path.Combine(codexHome, "auth-marker"), "existing");
            await File.WriteAllTextAsync(executable, """
                #!/bin/sh
                test -f "$CODEX_HOME/auth-marker" || exit 21
                test "$HOME" != "$CODEX_HOME" || exit 22
                structured=false
                while [ "$#" -gt 0 ]; do
                  if [ "$1" = --output-schema ]; then structured=true; fi
                  if [ "$1" = --output-last-message ]; then shift; output=$1; fi
                  shift
                done
                if [ "$structured" = true ]; then
                  printf '%s' '{"status":"success","summary":"completed","testsOrValidationPerformed":[],"needsHumanInput":false,"question":null,"blockerType":null}' > "$output"
                else
                  printf OK > "$output"
                fi
                """);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", executable);
            Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
            var instructions = Path.Combine(directory, "AGENTS.md");
            await File.WriteAllTextAsync(instructions, "Test instructions");
            var executor = new CodexExecutor(new ProcessRunner(), new CodexSettings());
            await executor.PreflightAsync(CancellationToken.None);
            var result = await executor.RunAsync(directory, instructions,
                new GitHubIssue(1, "Test", "Test", DateTimeOffset.UtcNow), CancellationToken.None);
            Assert.Equal("success", result.Status);
            Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(codexHome, "auth-marker")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", previousExecutable);
            Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
            Directory.Delete(directory, true);
        }
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codex-service-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
