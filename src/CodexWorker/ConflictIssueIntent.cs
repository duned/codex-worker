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
        "issue-parent-changed" => "The Issue parent changed after original capture. Preserve the implementation and review the parent history; recovery cannot override changed intent.",
        "issue-parent-metadata-missing" => "Legacy history does not distinguish an uncaptured parent from an absent parent. Use managed recovery inspection for native GitHub history proof; no historical value was fabricated.",
        "issue-parent-history-unavailable" => "Native GitHub parent history is unavailable. Check original Worker GitHub access/API support, then refresh recovery inspection; no override is authorized.",
        "issue-parent-history-environment" => "Original Worker rejected the managed GitHub environment before launching gh. Inspect service-account HOME, managed authentication ownership/permissions, linked paths and unrelated gh authentication; repair locally, then wait for a new Worker poll and refresh.",
        "issue-parent-history-process-start" => "Original Worker could not launch gh. Check the service PATH, executable/interpreter permissions and working directory; then wait for a new Worker poll and refresh.",
        "issue-parent-history-process-failed" => "Original Worker gh process failed while reading parent history. Inspect local service diagnostics and GitHub connectivity/API support; then wait for a new Worker poll and refresh.",
        "issue-parent-history-authentication" => "Original Worker gh authentication or repository authorization failed. Verify repository access in the managed service-account authentication context; then wait for a new Worker poll and refresh.",
        "issue-parent-history-timeout" => "Original Worker parent-history query exceeded its process or 20-second inspection deadline. Check service connectivity and GitHub availability; then wait for a new Worker poll and refresh.",
        "issue-parent-history-graphql" => "GitHub returned GraphQL errors to the original Worker parent-history query. Check API schema support and repository access in the managed service context; then wait for a new Worker poll and refresh.",
        "issue-parent-history-result-invalid" => "Original Worker could not parse the parent-history response contract. Check deployed Worker/API schema compatibility and bounded-output truncation; then wait for a new Worker poll and refresh. Raw provider output is withheld.",
        "issue-parent-history-incomplete" => "Parent history is incomplete, ambiguous, transferred or inconsistent. Preserve the workspace; restore reliable history access and refresh, or use a separately reviewed fresh task. Manual parent attestation is unsupported.",
        "issue-original-parent-absent" => "Original capture or complete history shows no parent at the original boundary, but a parent exists now. Preserve the implementation; recovery cannot invent an original parent.",
        "issue-parent-proof-changed" => "Parent evidence differs from the operator-reviewed preview. Refresh recovery inspection before requesting another attempt.",
        "issue-original-metadata-missing" => "Original Issue metadata is missing or was irreversibly redacted without a fingerprint. Preserve this execution; use a separately reviewed fresh implementation request if original intent cannot be proven.",
        "issue-recovery-label-missing" => "No configured conflict, recovery or working label is present. Review the configured recovery labels, then refresh.",
        "issue-conflicting-lifecycle-label" => "A configured ready, done, blocked or failed label conflicts with recovery. Reconcile lifecycle labels, then refresh.",
        _ => "GitHub could not be queried reliably. Check Worker GitHub access and diagnostics, then refresh; no recovery was authorized."
    });
}
