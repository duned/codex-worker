namespace CodexProvisioning;

using System.Text.Json.Serialization;

/// <summary>Public node-produced handoff; contains no authentication material.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkerPairingRequest(int ContractVersion, string WorkerId, string Operation, string Server)
{
    public bool IsValid() => ContractVersion == 1 && Guid.TryParseExact(WorkerId, "N", out _) &&
        Operation is "enroll" or "associate" && Uri.TryCreate(Server, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        Server == uri.GetLeftPart(UriPartial.Authority);
}
