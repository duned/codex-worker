using System.Security.Cryptography;
using System.Text;

namespace CodexWorker;

/// <summary>Identifies the node, service account and Codex store that own a local conversation.</summary>
internal static class CodexSessionOwnership
{
    internal static string Current => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        System.Text.Json.JsonSerializer.Serialize(new { Environment.MachineName, Environment.UserName,
            Home = Path.GetFullPath(CodexProvisioning.CodexServiceEnvironment.Home) }))));
}

public sealed partial class ExecutionHistoryStore
{
}
