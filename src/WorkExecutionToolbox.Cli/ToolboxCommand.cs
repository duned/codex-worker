using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkExecutionToolbox.Cli;

/// <summary>Parsing and presentation only; all relationship work belongs to the provider.</summary>
public static class ToolboxCommand
{
    public const string Help = """
        Usage: wet COMMAND [--repo owner/name] [--json] [--refresh]
          parent set CHILD... PARENT       Set one parent for 1–50 distinct children (clear before replacing).
          parent clear CHILD            Clear an organizational parent.
          roots [--state open|closed|all] List parentless repository Issues (default all).
          children PARENT               List direct organizational children.
          dependency add ISSUE BLOCKER...     ISSUE is blocked by each BLOCKER (1–50).
          dependency remove ISSUE BLOCKER...  Remove those prerequisites.
          relationships ISSUE           Show parent, children, prerequisites and dependents.
          graph ISSUE                   Inspect a bounded graph (default depth 5).
        All numbers are positive GitHub Issue numbers in the selected repository.
        Without --repo, local remotes must identify one unambiguous GitHub.com repository.
        Explicit --repo overrides discovery; GH_REPO and gh defaults are never used.
        Parent/child organization does not imply execution dependencies.
        Options may appear anywhere. --help / -h shows help without authentication.
        --version / -v / v shows the package version without repository or authentication access.
        Authenticate with: gh auth login --hostname github.com
        Exit codes: 0 success; 1 provider/operation failure; 2 usage; 3 missing Issue;
                    4 conflict; 5 partial/uncertain write; 130 cancelled.
        After a failed, partial or cancelled write, refresh relationships before retrying.
        --refresh fetches fresh Issue data for roots, children, relationships and graph; stable IDs stay cached.
        --json emits a versioned JSON envelope; errors go to stderr, results to stdout.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Handles commands that need no provider, cache, authentication or repository.</summary>
    public static async Task<int?> RunInformationAsync(string[] args, TextWriter output)
    {
        if (args.Length == 1 && args[0] is "--version" or "-v" or "v")
        {
            var version = typeof(ToolboxCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? throw new InvalidOperationException("WET assembly version metadata is missing.");
            await output.WriteLineAsync($"wet {version.Split('+')[0]}");
            return 0;
        }
        if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
        {
            await output.WriteLineAsync(Help);
            return 0;
        }
        return null;
    }

    public static async Task<int> RunAsync(string[] args, IIssueRelationshipProvider relationships,
        IIssueGraphProvider graphs, TextWriter output, TextWriter error, CancellationToken cancellationToken = default,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? readRemoteUrls = null)
    {
        if (await RunInformationAsync(args, output) is { } informationExit) return informationExit;
        var json = args.Contains("--json", StringComparer.Ordinal);
        Command command;
        try { command = Parse(args); }
        catch (ArgumentException)
        {
            return await ErrorAsync(error, json, 2, "usage", "Invalid command, Issue numbers or repository. Use --repo owner/name and see wet --help.");
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command.Repository is null)
            {
                var remotes = await (readRemoteUrls ?? LocalRepositoryDiscovery.ReadRemoteUrlsAsync)(cancellationToken);
                var repository = GitHubRepositoryContext.Discover(remotes);
                if (repository is null)
                    return await ErrorAsync(error, json, 2, "usage",
                        "Cannot discover one unambiguous GitHub.com repository from this checkout. Use --repo owner/name and see wet --help.");
                command = command with { Repository = repository };
            }
            try { ValidateMutation(command); }
            catch (ArgumentException)
            {
                return await ErrorAsync(error, json, 2, "usage", "Invalid command, Issue numbers or repository. Use --repo owner/name and see wet --help.");
            }
            using var readScope = (command.Name == "graph" ? graphs : (IIssueProvider)relationships)
                is ICacheAwareIssueProvider cacheAware ? cacheAware.BeginReadOperation(command.Refresh) : null;
            if (command.Refresh && readScope is null)
                throw new GitHubIssueException(GitHubIssueFailure.Provider, "Provider does not support explicit refresh.");
            object? data;
            var lines = new List<string>();
            var exit = 0;
            switch (command.Name)
            {
                case "parent set" when command.Numbers.Length > 2:
                    var parents = await relationships.SetParentsAsync(new(new(command.Issue.Repository, command.Numbers[^1]),
                        command.Numbers[..^1]), cancellationToken);
                    data = parents;
                    exit = ExitCode(parents.Status);
                    lines.Add($"Parent set batch: {parents.Status}.");
                    foreach (var relation in parents.Relations)
                    {
                        lines.Add($"Issue {relation.ChildIssueNumber}: parent {command.Numbers[^1]}: {relation.Result.Status}.");
                        AddDiagnostic(lines, relation.Result.Diagnostic);
                    }
                    break;
                case "parent set":
                case "parent clear":
                    var parent = command.Numbers.Length == 2 ? command.Numbers[1] : (int?)null;
                    var change = await relationships.SetParentAsync(new(command.Issue, parent), cancellationToken);
                    data = change;
                    exit = ExitCode(change.Status);
                    lines.Add($"Issue {command.Issue.Number}: parent {(parent is { } p ? p.ToString(CultureInfo.InvariantCulture) : "cleared")}: {change.Status}.");
                    AddDiagnostic(lines, change.Diagnostic);
                    break;
                case "dependency add":
                case "dependency remove":
                    var batch = await relationships.SetDependenciesAsync(new(command.Issue, command.Numbers[1..],
                        command.Name == "dependency add"), cancellationToken);
                    data = batch;
                    exit = ExitCode(batch.Status);
                    lines.Add($"Dependency {command.Name.Split(' ')[1]}: {batch.Status}.");
                    foreach (var relation in batch.Relations)
                    {
                        lines.Add($"{command.Name}: Issue {command.Issue.Number} blocked by Issue {relation.BlockerIssueNumber}: {relation.Result.Status}.");
                        AddDiagnostic(lines, relation.Result.Diagnostic);
                    }
                    break;
                case "roots":
                    if (relationships is not IIssueRepositoryProvider repositories)
                        throw new GitHubIssueException(GitHubIssueFailure.Provider, "Provider does not support repository inspection.");
                    var roots = await repositories.ListRootsAsync(command.RepositoryContext, command.State,
                        cancellationToken: cancellationToken);
                    data = roots;
                    lines.Add(command.RepositoryContext.Repository);
                    AddIssues(lines, "Roots", roots);
                    break;
                case "children":
                case "relationships":
                    var result = await relationships.GetRelationshipsAsync(command.Issue, cancellationToken);
                    data = command.Name == "children" ? result?.Children : (object?)result;
                    if (result is not null)
                    {
                        lines.Add($"{command.Issue.Repository.Repository} Issue {command.Issue.Number}: {Safe(result.Issue.Title)}");
                        if (command.Name == "relationships")
                            lines.Add($"Parent: {(result.Parent is null ? "none" : result.Parent.Issue.Number.ToString(CultureInfo.InvariantCulture))}");
                        AddIssues(lines, "Children", result.Children);
                        if (command.Name == "relationships")
                        {
                            AddIssues(lines, $"Issue {command.Issue.Number} is blocked by", result.BlockedBy);
                            AddIssues(lines, $"Issue {command.Issue.Number} blocks", result.Blocking);
                        }
                    }
                    break;
                default:
                    var graph = await graphs.GetGraphAsync(command.Issue, cancellationToken: cancellationToken);
                    data = graph;
                    if (graph is not null && !json) lines.AddRange(IssueGraphTextRenderer.Render(graph));
                    break;
            }
            if (data is null)
                return await ErrorAsync(error, json, 3, "missingIssue", "Issue is missing or not visible in the selected repository.");
            if (exit is 1 or 5 or 130) lines.Add("Refresh relationships before retrying a write; it may have taken effect.");
            await output.WriteLineAsync(json
                ? JsonSerializer.Serialize(new { schemaVersion = 1, command = command.Name,
                    repository = command.RepositoryContext.Repository, issue = command.Name == "roots" ? (int?)null : command.Issue.Number, data }, JsonOptions)
                : string.Join(Environment.NewLine, lines));
            return exit;
        }
        catch (OperationCanceledException)
        {
            return await ErrorAsync(error, json, cancellationToken.IsCancellationRequested ? 130 : 1,
                cancellationToken.IsCancellationRequested ? "cancelled" : "timeout",
                "Operation cancelled or timed out. Refresh relationships before retrying a write.");
        }
        catch (GitHubIssueException ex)
        {
            return await ErrorAsync(error, json, 1, JsonNamingPolicy.CamelCase.ConvertName(ex.Failure.ToString()),
                ex.Failure == GitHubIssueFailure.Authorization
                    ? "GitHub authorization unavailable. Check gh auth status --hostname github.com, gh auth login and repository access."
                    : ex.Message + " Refresh relationships before retrying a write.");
        }
        catch (Exception)
        {
            // Unexpected host exceptions may contain credentials; never print their contents.
            return await ErrorAsync(error, json, 1, "hostFailure", "Toolbox operation failed. Check gh installation and repository access; refresh relationships before retrying a write.");
        }
    }

    private static Command Parse(string[] args)
    {
        string? repository = null;
        var jsonSeen = false;
        var refresh = false;
        IssueListState? state = null;
        var words = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--repo" && repository is null && i + 1 < args.Length)
                repository = args[++i];
            else if (args[i] == "--state" && state is null && i + 1 < args.Length)
                state = args[++i] switch
                {
                    "open" => IssueListState.Open, "closed" => IssueListState.Closed, "all" => IssueListState.All,
                    _ => throw new ArgumentException("Invalid state.")
                };
            else if (args[i] == "--refresh" && !refresh) refresh = true;
            else if (args[i] == "--json" && !jsonSeen) jsonSeen = true;
            else if (args[i].StartsWith('-')) throw new ArgumentException("Unknown or duplicate option.");
            else words.Add(args[i]);
        }
        if (words.Count < 1) throw new ArgumentException("Command required.");
        var name = words[0] is "parent" or "dependency" ? string.Join(' ', words.Take(2)) : words[0];
        var numberWords = words.Skip(name.Contains(' ') ? 2 : 1).ToArray();
        var numbers = numberWords.Select(word => int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n : throw new ArgumentException("Positive Issue number required.")).ToArray();
        var valid = name switch
        {
            "roots" => numbers.Length == 0,
            "parent set" => numbers.Length is >= 2 and <= 51,
            "parent clear" or "children" or "relationships" or "graph" => numbers.Length == 1,
            "dependency add" or "dependency remove" => numbers.Length is >= 2 and <= 51,
            _ => false
        };
        if (state is not null && name != "roots") valid = false;
        if (refresh && name is not ("roots" or "children" or "relationships" or "graph")) valid = false;
        if (!valid) throw new ArgumentException("Invalid command arguments.");
        return new Command(name, repository is null ? null : GitHubRepositoryContext.Create(repository), numbers, refresh, state ?? IssueListState.All);
    }

    private static void ValidateMutation(Command command)
    {
        // Validate library contracts before any provider call or authentication.
        if (command.Name == "parent set")
            _ = new SetParentsRequest(new(command.Issue.Repository, command.Numbers[^1]), command.Numbers[..^1]);
        if (command.Name.StartsWith("dependency", StringComparison.Ordinal))
            _ = new SetDependenciesRequest(command.Issue, command.Numbers[1..], command.Name == "dependency add");
    }

    private static int ExitCode(RelationshipChangeStatus status) => status switch
    {
        RelationshipChangeStatus.Changed or RelationshipChangeStatus.Unchanged or RelationshipChangeStatus.Preview => 0,
        RelationshipChangeStatus.Conflict => 4,
        RelationshipChangeStatus.Partial => 5,
        _ => 1
    };

    private static void AddIssues(List<string> lines, string label, IReadOnlyList<IssueSummary> issues)
    {
        lines.Add(label + (issues.Count == 0 ? ": none" : ":"));
        foreach (var issue in issues)
            lines.Add($"  Issue {issue.Issue.Number} [{issue.State}]: {Safe(issue.Title)}");
    }

    private static string Safe(string text) => new(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
    private static void AddDiagnostic(List<string> lines, string? diagnostic)
    {
        if (diagnostic is not null) lines.Add(Safe(diagnostic));
    }

    private static async Task<int> ErrorAsync(TextWriter error, bool json, int exit, string code, string message)
    {
        await error.WriteLineAsync(json ? JsonSerializer.Serialize(new { schemaVersion = 1, error = new { code, message } }, JsonOptions) : message);
        return exit;
    }

    private sealed record Command(string Name, RepositoryContext? Repository, int[] Numbers, bool Refresh, IssueListState State)
    {
        public RepositoryContext RepositoryContext => Repository ?? throw new InvalidOperationException("Repository has not been resolved.");
        public IssueReference Issue => new(RepositoryContext, Numbers[0]);
    }
}
