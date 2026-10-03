namespace CodexServer;

internal static class ServerAdministrationTimeoutPolicy
{
    internal static int Seconds(IReadOnlyList<string> arguments) => arguments.Count > 0 && arguments[0] == "github" ? 120 : 15;

    internal static string Diagnostic(IReadOnlyList<string> arguments)
    {
        if (Seconds(arguments) == 15)
            return "Server administration command timed out after 15 seconds. Check local database availability and try again.";
        // Only the command context is printed, never arbitrary option values or process output.
        var context = string.Join(" ", arguments.Take(3).Select(value =>
            new string(value.Take(100).Select(character => char.IsControl(character) ? ' ' : character).ToArray())));
        return $"GitHub administration {context} timed out after 120 seconds. Check GitHub availability, rate limits and Server service-account authentication.";
    }
}

internal sealed class GitHubCommandTimeoutException : IOException
{
    internal GitHubCommandTimeoutException() : base("GitHub command exceeded its 60 second process deadline.") { }
}
