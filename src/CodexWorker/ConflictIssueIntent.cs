using System.Security.Cryptography;
using System.Text;

namespace CodexWorker;

internal sealed class ConflictIntentException(string reasonCode, string explanation)
    : WorkerInfrastructureException($"{reasonCode}: {explanation}")
{
    internal string ReasonCode { get; } = reasonCode;
}

/// <summary>Intent proof is captured before diagnostic redaction; only line endings are canonicalized.</summary>
internal static class ConflictIssueIntent
{
    internal static string NormalizeBody(string body) => body.Replace("\r\n", "\n", StringComparison.Ordinal);
    internal static string BodyFingerprint(string body) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeBody(body))));

    internal static ConflictIntentException Refuse(string code) => new(code, code switch
    {
        "issue-closed" => "The Issue is closed. Review its state before refreshing eligibility.",
        "issue-missing" => "The requested Issue was not returned. Check repository and Issue access, then refresh.",
        "issue-title-changed" => "The Issue title differs from the original intent. Review the change; restore the intended title only if appropriate, then refresh.",
        "issue-body-changed" => "The Issue body differs from the original intent. Review the change; restore the intended body only if appropriate, then refresh.",
        "issue-parent-changed" => "The Issue parent differs from recorded history (or historical parent evidence is absent). Review the parent and retained history before recovery.",
        "issue-original-metadata-missing" => "Original Issue metadata is missing or was irreversibly redacted without a fingerprint. Preserve this execution; use a separately reviewed fresh implementation request if original intent cannot be proven.",
        "issue-recovery-label-missing" => "No configured conflict, recovery or working label is present. Review the configured recovery labels, then refresh.",
        "issue-conflicting-lifecycle-label" => "A configured ready, done, blocked or failed label conflicts with recovery. Reconcile lifecycle labels, then refresh.",
        _ => "GitHub could not be queried reliably. Check Worker GitHub access and diagnostics, then refresh; no recovery was authorized."
    });
}
