namespace CodexWorker.Tests;

/// <summary>A real, offline child process: no native Claude installation or credentials are used.</summary>
internal sealed class FakeClaudeCli : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fake-claude-" + Guid.NewGuid().ToString("N"));
    private readonly string _script;
    internal int Invocations { get; private set; }
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal FakeClaudeCli(string mode = "success")
    {
        Directory.CreateDirectory(_directory);
        _script = Path.Combine(_directory, "claude.sh");
        File.WriteAllText(_script, """
            case "$1" in
              --version) printf '%s\n' '2.1.259 (Claude Code)'; exit 0 ;;
              --help) printf '%s\n' '--restricted --safe-mode --permission-prompts --permission-mode --json-schema --effort --model --tools --allowedTools --strict-mcp-config --no-session-persistence'; exit 0 ;;
              auth) if [ "$FAKE_CLAUDE_MODE" = auth-error ]; then exit 1; fi; printf '%s\n' '{"loggedIn":true}'; exit 0 ;;
              --print) printf '%s\n' '{"type":"result","is_error":false,"result":"OK"}'; exit 0 ;;
            esac
            prompt=$(/bin/cat)
            case "$prompt" in *'Worker performs validation.'*) ;; *) exit 9 ;; esac
            case "$*" in *'--restricted'*'--safe-mode'*) ;; *) exit 10 ;; esac
            printf '%s\n' 'fake implementation' > implemented.txt
            printf '%s\n' 'private-cli-diagnostic' >&2
            case "$FAKE_CLAUDE_MODE" in
              wait) printf '%s\n' 'fake-cli-started'; /bin/sleep 60; exit 1 ;;
              auth-error) exit 1 ;;
              malformed) printf '%s\n' 'private-cli-diagnostic'; exit 0 ;;
              denied) printf '%s\n' '{"type":"result","is_error":false,"permission_denials":[{"tool":"Bash","input":"private-cli-diagnostic"}]}'; exit 0 ;;
            esac
            printf '%s\n' '{"type":"result","is_error":false,"structured_output":{"status":"success","summary":"implemented","testsOrValidationPerformed":[],"needsHumanInput":false,"question":null,"blockerType":null}}'
            """);
        Mode = mode;
    }

    internal string Mode { get; }
    internal ClaudeImplementationEngine CreateEngine() => new(RunAsync, new CodexSettings());

    internal async Task<ProcessResult> RunAsync(IReadOnlyList<string> args, string cwd, string? input,
        IReadOnlyDictionary<string, string?> environment, TimeSpan timeout, CancellationToken ct)
    {
        Directory.CreateDirectory(cwd);
        if (args[0] == "-p") Invocations++;
        var childEnvironment = environment.ToDictionary(pair => pair.Key, pair => pair.Value);
        childEnvironment["FAKE_CLAUDE_MODE"] = Mode;
        return await new ProcessRunner().RunAsync("/bin/sh", new[] { _script }.Concat(args), cwd, timeout, ct,
            childEnvironment, standardInput: input, standardOutputObserver: output =>
            {
                if (output.Contains("fake-cli-started", StringComparison.Ordinal)) Started.TrySetResult();
                return Task.CompletedTask;
            });
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
