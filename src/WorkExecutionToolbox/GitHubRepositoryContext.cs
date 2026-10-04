namespace WorkExecutionToolbox;

/// <summary>GitHub.com repository syntax and optional, host-supplied local remote discovery.</summary>
public static class GitHubRepositoryContext
{
    public static RepositoryContext Create(string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        var parts = repository.Split('/');
        if (parts.Length != 2 || !ValidPart(parts[0], owner: true) || !ValidPart(parts[1], owner: false))
            throw new ArgumentException("GitHub repository context must be an explicit owner/name.", nameof(repository));
        return new RepositoryContext(repository);
    }

    public static RepositoryContext? Discover(IEnumerable<string> remoteUrls)
    {
        ArgumentNullException.ThrowIfNull(remoteUrls);
        var repositories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var remote in remoteUrls)
        {
            string path;
            if (remote.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
                path = remote[15..];
            else if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) &&
                uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
                uri.Scheme is "https" or "ssh" && uri.IsDefaultPort &&
                uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
                (uri.UserInfo.Length == 0 || uri.Scheme == "ssh" && uri.UserInfo == "git"))
                path = uri.AbsolutePath.TrimStart('/');
            else
                return null;
            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
            try { repositories.Add(Create(path).Repository); }
            catch (ArgumentException) { return null; }
        }
        return repositories.Count == 1 ? Create(repositories.Single()) : null;
    }

    private static bool ValidPart(string part, bool owner) =>
        part.Length is > 0 and <= 100 && part is not ("." or "..") &&
        part.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || !owner && c is '_' or '.');
}
