namespace CodexServer;

using CodexProvisioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class LocalProvisioningCommandService(ProvisioningCommandStore store, NodeProvisioningCommandExecutor executor,
    ServerConfiguration configuration, ILogger<LocalProvisioningCommandService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            var command = await store.ClaimAsync("server", stoppingToken);
            if (command is not null)
            {
                logger.LogInformation("Executing local provisioning command {CommandId} for {CapabilityId} {Action}.",
                    command.Id, command.Request.CapabilityId, command.Request.Action);
                var permitted = command.Request.Action is ProvisioningCommandAction.Detect or
                    ProvisioningCommandAction.CheckAuthentication or ProvisioningCommandAction.CheckConfiguration or
                    ProvisioningCommandAction.InspectSshKey or ProvisioningCommandAction.VerifyRepositoryAccess ||
                    configuration.EnableLocalProvisioning && (NodeGitHubSetup.Handles(command.Request) ||
                    command.Request.Action is ProvisioningCommandAction.Logout or ProvisioningCommandAction.Login ||
                        configuration.AllowLocalProvisioningElevation && command.Request.AllowElevation);
                var report = await executor.ExecuteAsync(command, permitted, stoppingToken,
                    async (progress, token) => { await store.ReportAsync(command.Id, "server", progress, token); });
                var completed = await store.ReportAsync(command.Id, "server", report, CancellationToken.None);
                if (completed is { Status: ProvisioningCommandStatus.Failed or ProvisioningCommandStatus.TimedOut })
                    logger.LogWarning("Local provisioning command {CommandId} for {CapabilityId} {Action} ended as {Status} ({Diagnostic}): {FailureReason}{ProcessExitCode}.",
                        completed.Id, completed.Request.CapabilityId, completed.Request.Action, completed.Status,
                        completed.Diagnostic, completed.FailureDetail?.Description ?? completed.Diagnostic.ToString(),
                        completed.FailureDetail?.ProcessExitCode is { } exitCode ? $"; process exit code {exitCode}" : string.Empty);
                else if (completed is not null)
                    logger.LogInformation("Local provisioning command {CommandId} for {CapabilityId} {Action} completed as {Status}.",
                        completed.Id, completed.Request.CapabilityId, completed.Request.Action, completed.Status);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
