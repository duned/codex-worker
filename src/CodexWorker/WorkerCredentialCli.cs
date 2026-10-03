namespace CodexWorker;

using CodexProvisioning;
using System.Text.Json;

/// <summary>Presentation only; shared credential handlers own authentication and provider-local storage.</summary>
public sealed class WorkerCredentialCli(NodeCredentialAdministration service, string nodeId,
    TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static ProvisioningCliCommand Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0 || arguments[0] is not ("status" or "login" or "logout" or "check"))
            throw new ArgumentException("Use 'codex-worker credential <status|check|login|logout> --help' for usage.");
        var mapped = arguments.ToArray();
        if (mapped[0] == "check") mapped[0] = "check-authentication";
        var command = ProvisioningCli.Parse(mapped);
        if (!command.IsStatus && command.CapabilityId is not ("github-cli" or "codex-cli"))
            throw new ArgumentException("Credential operations require github-cli or codex-cli.");
        return command;
    }

    public async Task<int> RunAsync(ProvisioningCliCommand command, CancellationToken cancellationToken = default)
    {
        var result = command.IsStatus ? await service.StatusAsync(cancellationToken) :
            await service.ExecuteAsync(new(nodeId,
                command.CapabilityId ?? throw new InvalidOperationException("Missing credential provider."),
                command.Action ?? throw new InvalidOperationException("Missing credential operation."), command.TimeoutSeconds),
                cancellationToken, async (report, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (command.Json) await error.WriteLineAsync(JsonSerializer.Serialize(report, JsonOptions));
                    else if (report.LoginInstructions is { } challenge)
                    {
                        await output.WriteLineAsync($"Open {challenge.VerificationUri} and enter code {challenge.UserCode}.");
                        await output.FlushAsync(token);
                    }
                });

        if (command.Json) await output.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions));
        else
        {
            await output.WriteLineAsync($"Credentials {result.Operation}: {result.Report.Status.ToString().ToLowerInvariant()} ({result.Report.Diagnostic}).");
            if (result.Report.FailureDetail is { } failure)
                await output.WriteLineAsync($"Failure: {failure.Description}");
            foreach (var credential in result.Credentials)
                await output.WriteLineAsync($"{credential.CapabilityId}: installation={credential.Installation.ToString().ToLowerInvariant()}, " +
                    $"authentication={credential.Authentication?.ToString().ToLowerInvariant() ?? "not-required"}, " +
                    $"ready={credential.Ready.ToString().ToLowerInvariant()}, dependencies={string.Join(",", credential.AuthenticationDependencies)}");
            if (result.Report.Diagnostic == ProvisioningDiagnostic.Denied)
                await error.WriteLineAsync("Review worker.provisioning.enabled, allowCredentials, allowNonPrivileged, and deniedActions in local configuration.");
        }
        return result.Report.Status switch
        {
            ProvisioningCommandStatus.Succeeded => ProcessExitCodes.Success,
            ProvisioningCommandStatus.Cancelled or ProvisioningCommandStatus.TimedOut => WorkerCliOutput.Cancelled,
            _ => ProcessExitCodes.StartupFailure
        };
    }
}
