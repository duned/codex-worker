namespace CodexProvisioning;

/// <summary>Product-owned probes for typed node-local authentication dependencies.</summary>
internal static class AuthenticationDependencyProbes
{
    internal static ToolProvisioningStep Get(AuthenticationDependencyKind dependency) => dependency switch
    {
        AuthenticationDependencyKind.GitHubCliLogin => new("gh", ["auth", "status", "--hostname", "github.com"]),
        AuthenticationDependencyKind.CodexCliLogin => new(CodexServiceEnvironment.Executable, ["login", "status"]),
        AuthenticationDependencyKind.ClaudeCliAuthentication => new(ClaudeServiceEnvironment.Executable, ["auth", "status"]),
        _ => throw new InvalidOperationException("Unsupported authentication dependency.")
    };
}

/// <summary>Interprets structured observations only; never retains authentication payloads.</summary>
public static class ClaudeAuthenticationStatus
{
    public static bool TryRead(int exitCode, string output, out bool authenticated)
    {
        authenticated = false;
        if (exitCode is not (0 or 1) || output.Length > 16384) return false;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !root.TryGetProperty("loggedIn", out var loggedIn) ||
                loggedIn.ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
                return false;
            var observed = loggedIn.GetBoolean();
            if (observed != (exitCode == 0)) return false;
            if (root.TryGetProperty("authMethod", out var method) &&
                (method.ValueKind != System.Text.Json.JsonValueKind.String ||
                 (observed ? method.GetString() is not ("claude.ai" or "oauth_token" or "api_key" or "api_key_helper" or "third_party")
                     : method.GetString() != "none"))) return false;
            authenticated = observed;
            return true;
        }
        catch (System.Text.Json.JsonException) { return false; }
    }
}
