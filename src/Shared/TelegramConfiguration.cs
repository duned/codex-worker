namespace CodexProvisioning;

/// <summary>Explicit per-Worker override. Null preserves local YAML and process environment.</summary>
public sealed record WorkerTelegramConfiguration(bool Enabled, string? ChatId, string? CredentialId)
{
    public bool TokenConfigured => CredentialId is not null;

    public void Validate()
    {
        if (CredentialId is not null && !Guid.TryParseExact(CredentialId, "N", out _))
            throw new InvalidDataException("Telegram credential reference is invalid.");
        if (ChatId is not null && (ChatId.Length > 200 || ChatId.Any(char.IsControl) ||
            !System.Text.RegularExpressions.Regex.IsMatch(ChatId, @"^(?:-?[0-9]+|@[A-Za-z][A-Za-z0-9_]{4,})$")))
            throw new InvalidDataException("Telegram chat ID must be a numeric ID or @channel username.");
        if (Enabled && (string.IsNullOrWhiteSpace(ChatId) || CredentialId is null))
            throw new InvalidDataException("Enabled Telegram requires a chat ID and a configured bot token.");
    }
}
