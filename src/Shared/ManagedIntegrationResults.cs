namespace CodexProvisioning;

/// <summary>Integration results a Worker reports for a failed managed execution.</summary>
public static class ManagedIntegrationResults
{
    /// <summary>The implementation could not be rebased onto the advanced base. The rebase was
    /// aborted, nothing was integrated and the implementation is preserved.</summary>
    public const string RebaseConflict = "rebase-conflict";
}
