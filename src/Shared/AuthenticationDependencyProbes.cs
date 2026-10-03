namespace CodexProvisioning;

/// <summary>Product-owned probes for typed node-local authentication dependencies.</summary>
internal static class AuthenticationDependencyProbes
{
    internal static ToolProvisioningStep Get(AuthenticationDependencyKind dependency) => dependency switch
    {
        AuthenticationDependencyKind.GitHubCliLogin => new("gh", ["auth", "status", "--hostname", "github.com"]),
        AuthenticationDependencyKind.CodexCliLogin => new(CodexServiceEnvironment.Executable, ["login", "status"]),
        _ => throw new InvalidOperationException("Unsupported authentication dependency.")
    };
}
