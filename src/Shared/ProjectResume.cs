namespace CodexProvisioning;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

/// <summary>An operator request to lift a project pause held in one managed Worker's memory. Dispatched once, never replayed.</summary>
public sealed record ProjectResumeOperation(string Id, string ProjectId, string Status, DateTimeOffset DeadlineUtc,
    string? Diagnostic = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProjectResumeReport(string Id, string Diagnostic);
public static class ProjectResumeProtocol
{
    public const int DetailLength = 300;
    public const int TimeoutSeconds = 120;
    /// <summary>The Worker-reported code for a project paused on that Worker (GitHub reconciliation, ambiguous completion, failed preparation).</summary>
    public const string PausedCode = "project-recovery-verification-failed";
    public static bool ValidDiagnostic(string value) => value is "resumed" or "not-paused" or "project-unknown" or "delivery-unavailable";
    public static bool ValidProjectId([NotNullWhen(true)] string? value) => value is { Length: > 0 and <= 120 } && !value.Any(char.IsControl);

    /// <summary>Bounds a Worker pause reason for the heartbeat; whitespace and control characters collapse to single spaces.</summary>
    public static string? Detail(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var text = string.Join(' ', reason.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => new string(part.Where(c => !char.IsControl(c)).ToArray())).Where(part => part.Length > 0));
        return text.Length <= DetailLength ? text : text[..(DetailLength - 1)] + "…";
    }
}
