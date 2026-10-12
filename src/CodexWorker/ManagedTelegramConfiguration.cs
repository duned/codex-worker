namespace CodexWorker;

using CodexProvisioning;

internal sealed record TelegramRuntimeSettings(bool Enabled, string? Token, string? ChatId)
{
    public override string ToString() => $"TelegramRuntimeSettings {{ Enabled = {Enabled}, Token = [redacted] }}";
}

internal static class ManagedTelegramConfiguration
{
    public static async Task<IReadOnlyList<(string Path, WorkerConfiguration Configuration)>> SynchronizeAsync(
        ManagedConfigurationSynchronizer synchronizer, TelegramNotifier notifier, TelegramRuntimeSettings local,
        Func<CancellationToken, Task<ServerManagedConfigurationContract>> retrieveConfiguration,
        Func<string, CancellationToken, Task<CredentialDeliveryResponse?>> retrieveCredential,
        CancellationToken cancellationToken)
    {
        TelegramRuntimeSettings? effective = null;
        var resolving = false;
        IReadOnlyList<(string Path, WorkerConfiguration Configuration)> projects;
        try
        {
            projects = await synchronizer.RetrieveAndApplyAsync(async token =>
            {
                var snapshot = await retrieveConfiguration(token);
                ManagedConfigurationSynchronizer.ValidateSnapshot(snapshot);
                resolving = true;
                effective = await ResolveAsync(snapshot.Telegram, local.Enabled, local.Token, local.ChatId, retrieveCredential, token);
                resolving = false;
                return snapshot;
            }, cancellationToken);
        }
        catch (Exception error) when (resolving && (error is InvalidDataException or HttpRequestException or OperationCanceledException) &&
            !cancellationToken.IsCancellationRequested)
        {
            synchronizer.RecordTelegramFailure();
            throw;
        }
        if (effective is null) throw new InvalidOperationException("Telegram configuration was not resolved.");
        notifier.Configure(effective.Enabled, effective.Token, effective.ChatId);
        return projects;
    }

    public static async Task<TelegramRuntimeSettings> ResolveAsync(WorkerTelegramConfiguration? managed,
        bool localEnabled, string? environmentToken, string? environmentChatId,
        Func<string, CancellationToken, Task<CredentialDeliveryResponse?>> retrieve, CancellationToken cancellationToken)
    {
        var settings = new TelegramRuntimeSettings(localEnabled, environmentToken, environmentChatId);
        if (managed is not null)
        {
            managed.Validate();
            settings = new(managed.Enabled, null, managed.ChatId);
            if (managed.Enabled && managed.CredentialId is { } credentialId)
            {
                CredentialDeliveryResponse? credential;
                try { credential = await retrieve(credentialId, cancellationToken); }
                catch (WorkerStartupException)
                {
                    throw new InvalidDataException("Telegram token delivery is unavailable; configure Worker credential delivery authorization or disable Telegram.");
                }
                if (credential is not { Provider: "telegram", Type: "bot-token" } || credential.Id != credentialId ||
                    credential.Version < 1 || credential.Secret is null || credential.Secret.Length > 200 ||
                    !System.Text.RegularExpressions.Regex.IsMatch(credential.Secret, @"^[0-9]+:[A-Za-z0-9_-]{20,}$"))
                    throw new InvalidDataException("Telegram bot token is unavailable; replace the token or restore Worker credential delivery authorization.");
                settings = settings with { Token = credential.Secret };
            }
        }
        TelegramNotifier.ValidateConfiguration(settings.Enabled, settings.Token, settings.ChatId);
        return settings;
    }
}
