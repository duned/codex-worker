namespace CodexServer;

using CodexProvisioning;
using System.Text.RegularExpressions;

/// <summary>Stages immutable encrypted credentials before the registry's optimistic update.</summary>
internal static class WorkerTelegramSettings
{
    public static async Task<WorkerRegistrationResponse?> UpdateAsync(string workerId,
        WorkerConfigurationUpdateRequest request, WorkerRegistrationResponse current,
        IRegistryStore registry, ICredentialStore credentials, CancellationToken cancellationToken)
    {
        WorkerTelegramConfiguration? telegram = null;
        CredentialMetadata? staged = null;
        var mayRevokeStaged = true;
        try
        {
            if (request.Telegram is { } input)
            {
                if (input.ClearToken && input.BotToken is not null)
                    throw new InvalidDataException("telegram-token-action-invalid");
                var credentialId = input.ClearToken ? null : current.Telegram?.CredentialId;
                var chatId = string.IsNullOrWhiteSpace(input.ChatId) ? null : input.ChatId.Trim();
                if (input.BotToken is { } token && (token.Value is null || token.Value.Length > 200 ||
                    !Regex.IsMatch(token.Value, @"^[0-9]+:[A-Za-z0-9_-]{20,}$")))
                    throw new InvalidDataException("telegram-token-invalid");
                // Validate before creating a secret; disabled configurations may omit values.
                telegram = new(input.Enabled, chatId, input.BotToken is null ? credentialId : Guid.Empty.ToString("N"));
                telegram.Validate();
                if (input.BotToken is { } replacement)
                {
                    staged = await credentials.CreateAsync(new("telegram", "bot-token", replacement), cancellationToken);
                    await credentials.AssignAsync(staged.Id, workerId, cancellationToken);
                    telegram = telegram with { CredentialId = staged.Id };
                }
                else if (input.Enabled && credentialId is not null)
                {
                    var metadata = await credentials.GetAsync(credentialId, cancellationToken);
                    if (metadata is not { Status: "Ready", Provider: "telegram", Type: "bot-token" } || metadata.AssignedWorkerId != workerId)
                        throw new InvalidDataException("telegram-token-unavailable");
                }
            }
            mayRevokeStaged = false; // Preserve staged credentials if the commit outcome is uncertain.
            var updated = await registry.UpdateWorkerConfigurationAsync(workerId, request.DisplayName.Trim(),
                request.MaximumParallelExecutions, request.ExpectedRevision, cancellationToken, request.WorkerExecutionSlots, telegram);
            mayRevokeStaged = updated is null;
            if (updated is not null && telegram is not null && current.Telegram?.CredentialId is { } previousId && previousId != telegram.CredentialId)
                await credentials.RevokeAsync(previousId, CancellationToken.None);
            return updated;
        }
        finally
        {
            // A rejected optimistic update must not leave a usable, unreferenced credential.
            if (mayRevokeStaged && staged is not null)
                await credentials.RevokeAsync(staged.Id, CancellationToken.None);
        }
    }
}
