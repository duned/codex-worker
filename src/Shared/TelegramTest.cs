namespace CodexProvisioning;

using System.Text.Json.Serialization;

public sealed record TelegramTestOperation(string Id, string Version, string Status, DateTimeOffset DeadlineUtc,
    string? Diagnostic = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TelegramTestReport(string Id, string Diagnostic);
public static class TelegramTestProtocol
{
    public static bool ValidDiagnostic(string value) => value is "accepted" or "telegram-rejected" or "delivery-unavailable" or "configuration-changed";
}
