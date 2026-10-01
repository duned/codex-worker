namespace CodexServer;

using CodexProvisioning;
using Microsoft.Extensions.Hosting;

public sealed class LocalProvisioningCommandService(ProvisioningCommandStore store, NodeProvisioningCommandExecutor executor,
    ServerConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            var command = await store.ClaimAsync("server", stoppingToken);
            if (command is not null)
            {
                var permitted = command.Request.Action is ProvisioningCommandAction.Detect or
                    ProvisioningCommandAction.CheckAuthentication or ProvisioningCommandAction.CheckConfiguration or
                    ProvisioningCommandAction.InspectSshKey or ProvisioningCommandAction.VerifyRepositoryAccess ||
                    configuration.EnableLocalProvisioning && (NodeGitHubSetup.Handles(command.Request) ||
                        configuration.AllowLocalProvisioningElevation && command.Request.AllowElevation);
                var report = await executor.ExecuteAsync(command, permitted, stoppingToken);
                await store.ReportAsync(command.Id, "server", report, CancellationToken.None);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
