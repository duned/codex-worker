namespace CodexServer;

/// <summary>Presentation-only catalog shared by the local administration adapters.</summary>
internal static class ServerCommandHelp
{
    private sealed record Command(string Syntax, string Notes);

    private static readonly IReadOnlyDictionary<string, Command> Commands = new Dictionary<string, Command>(StringComparer.Ordinal)
    {
        ["status"] = new("[--json]", "Read-only local persistence readiness; process health is not observed."),
        ["diagnostics"] = new("[--json]", "Read-only aggregate local queue and Worker states. Use worker list to discover Worker IDs."),
        ["config show"] = new("[--json]", "Read-only redacted resolved configuration. Does not open the database."),
        ["config validate"] = new("[--json]", "Read-only offline configuration validation. Does not open the database."),
        ["config set"] = new("<setting> <value> [--json]", "Mutating: atomically updates the installed environment file; restart codex-server.service to apply. Requires the installed sudo helper. Settings: ListenUrl, DataDirectory, DatabasePath, EnableLocalProvisioning, AllowLocalProvisioningElevation, WorkerStaleAfterSeconds, ExecutionLeaseDurationSeconds, ExecutionLeaseRenewalIntervalSeconds. No command-line configuration overrides."),
        ["projects list"] = new("[--json]", "Read-only managed project definitions and revisions."),
        ["projects show"] = new("<project-id> [--json]", "Read-only project definition. project-id is the Server-managed project identifier."),
        ["projects create"] = new("<definition-json-file> [--json]", "Mutating: reads a required CentralProjectDefinition JSON file and creates its project. Example: codex-server projects create project.json"),
        ["projects update"] = new("<project-id> <expected-revision> <definition-json-file> [--json]", "Mutating: replaces the definition from a CentralProjectDefinition JSON file. expected-revision must match the current positive revision; use projects show first."),
        ["projects enable"] = new("<project-id> <expected-revision> [--json]", "Mutating: enables the project at its current positive revision."),
        ["projects disable"] = new("<project-id> <expected-revision> [--json]", "Mutating: disables the project at its current positive revision."),
        ["projects delete"] = new("<project-id> <expected-revision> [--json]", "Mutating: deletes an unused project at its current positive revision. Projects in use cannot be deleted."),
        ["executions list"] = new("[--project <project-id>] [--state <state>] [--work-type <type>] [--work-id <id>] [--limit 1..100] [--offset 0..10000] [--json]", "Read-only queue page. Defaults: limit 50, offset 0."),
        ["executions show"] = new("<execution-id> [--json]", "Read-only request and attempt history. execution-id is the Server request ID from executions list."),
        ["executions cancel"] = new("<execution-id> [--json]", "Mutating: cancels queued requests only; active work retains its lease."),
        ["executions reconcile"] = new("<execution-id> <NotIntegrated|Integrated> <evidence> [integrated-commit-id] [--json]", "Mutating: reconciles only expired uncertain attempts. Requires explicit recovery disposition and evidence; Integrated requires the full commit ID. Verify integration before reconciliation."),
        ["worker list"] = new("[--json] [--limit 1..100] [--offset 0..10000] [database-path]", "Read-only registered Worker identities and scheduling states, ordered by Worker ID. Defaults: limit 100, offset 0. JSON: contractVersion, limit, offset, hasMore, workers (bounded summaries). Example: sudo codex-server worker list --json"),
        ["worker show"] = new("<worker-id> [database-path]", "Read-only Worker detail and credential-delivery authorization metadata; never secret payloads. worker-id is the 32-character ID from worker list."),
        ["worker enable"] = new("<worker-id> [database-path]", "Mutating: permits new assignments. worker-id is the 32-character ID from worker list."),
        ["worker drain"] = new("<worker-id> [database-path]", "Mutating: stops new assignments; active assignments retain their leases. worker-id comes from worker list."),
        ["worker disable"] = new("<worker-id> [database-path]", "Mutating: disables new scheduling. worker-id comes from worker list."),
        ["worker revoke-token"] = new("<worker-id> [database-path]", "Mutating: revokes per-Worker API authentication; active leases may expire into recovery. A configured shared registration-token fallback remains server-wide."),
        ["worker revoke-delivery-token"] = new("<worker-id> [database-path]", "Mutating: revokes credential delivery authorization; Worker API authentication is unchanged."),
        ["worker-token create"] = new("[database-path]", "Mutating: creates a one-use bootstrap token expiring in 15 minutes. Printed only to stdout; protect that output. Example: sudo codex-server worker-token create. Register using codex-worker register --token-stdin."),
        ["worker-token revoke"] = new("<registration-token> [database-path]", "Mutating: revokes a bootstrap registration token. Protect the token and shell history."),
        ["worker-token revoke-worker"] = new("<worker-id> [database-path]", "Mutating: revokes per-Worker API authentication. Discover worker-id with worker list. Active leases may expire; shared registration-token fallback remains server-wide if configured."),
        ["backup export"] = new("<archive-path> <database-path>", "Writes a backup archive from the specified local Server database. Protect archives as sensitive Server state."),
        ["backup validate"] = new("<archive-path>", "Read-only validation of a backup archive; does not restore the database."),
        ["backup restore"] = new("<archive-path> <database-path>", "Mutating: restores an archive to the specified database. Stop the Server first; restore takes an exclusive database access lock."),
        ["credential list"] = new("[--json]", "Read-only credential metadata; secret values are never printed."),
        ["credential show"] = new("<credential-id> [--json]", "Read-only credential metadata and assignments. Discover credential-id using credential list."),
        ["credential create"] = new("<provider> <type> [--secret-stdin] [--json]", "Mutating: creates a credential for the specified provider and credential type; use credential assign to authorize a Worker. Secret input is required: hidden interactive prompt by default, or piped --secret-stdin. Never supply secrets as arguments."),
        ["credential assign"] = new("<credential-id> <worker-id> [--json]", "Mutating: assigns an existing credential to a registered Worker. IDs come from credential list and worker list."),
        ["credential replace"] = new("<credential-id> [--secret-stdin] [--json]", "Mutating: replaces a credential secret. Required hidden interactive input by default, or piped --secret-stdin; never a secret argument."),
        ["credential revoke"] = new("<credential-id> [--json]", "Mutating: revokes a credential discovered using credential list."),
        ["provision list"] = new("[--limit 1..100] [--offset 0..10000] [--json]", "Read-only command history. Defaults: limit 100, offset 0."),
        ["provision show"] = new("<command-id> [--json]", "Read-only command status. command-id is the 32-character ID from provision list."),
        ["provision create"] = new("<node-id|server> <capability-id> <typed-action> [--timeout-seconds 5..600] [--allow-elevation] [--repository owner/repository] [--json]", "Mutating: queues an allowlisted capability action. capability-id is an ID from the capability catalog; typed-action is a supported action such as Detect, Install, Update, CheckAuthentication or VerifyRepositoryAccess. Default timeout: 120 seconds; elevation is opt-in and node-local policy remains authoritative. --repository is required only for VerifyRepositoryAccess. No shell commands or executable paths are accepted."),
        ["provision cancel"] = new("<command-id> [--json]", "Mutating: cancels queued commands only. Running operations stop at their deadline."),
        ["provision reconcile"] = new("<command-id> --node-quiescent [--json]", "Mutating: reconciles uncertain operations only after verifying the target node is quiescent. --node-quiescent is required."),
        ["github access"] = new("<project-id> [--json]", "Read-only repository access using the Server service account's gh login. Read access does not establish Issue write or Git push authorization."),
        ["github issues"] = new("<project-id> [--state open|closed|all] [--limit 1..100] [--label <label>] [--json]", "Read-only project repository Issues. Defaults: state open, limit 50."),
        ["github issue"] = new("<project-id> <issue-number> [--json]", "Read-only Issue detail and managed eligibility. Issue numbers are positive decimal integers."),
        ["github relationships"] = new("<project-id> <issue-number> [--json]", "Read-only parent, sub-issue and dependency relationships."),
        ["github graph"] = new("<project-id> <issue-number> [--max-depth 0..20] [--max-issues 1..200] [--max-edges 1..2000] [--json]", "Read-only bounded relationship graph. Defaults: depth 5, issues 100, edges 500."),
        ["github enqueue"] = new("<project-id> <issue-number> [--json]", "Mutating: queues an eligible GitHub Issue for managed execution."),
        ["github refresh"] = new("<project-id> <issue-number> [--json]", "Mutating: refreshes queued Issue eligibility."),
        ["github create"] = new("<project-id> --title <title> --body <body> [--preview] [--json]", "Mutating: creates an Issue; title and body are required. --preview validates and displays without applying changes."),
        ["github update"] = new("<project-id> <issue-number> [--title <title>] [--body <body>] [--preview] [--json]", "Mutating: updates title/body; at least one is required. --preview validates without applying changes."),
        ["github label"] = new("<project-id> <issue-number> <add|remove> <label> [--preview] [--json]", "Mutating: changes configured eligibility labels only. Worker execution labels, comments and Issue closure remain Worker-owned. --preview does not apply changes."),
        ["github dependency"] = new("<project-id> <issue-number> <add|remove> <blocking-issue-number> [--preview] [--json]", "Mutating: changes a blocked-by relationship; --preview does not apply changes."),
        ["github parent"] = new("<project-id> <issue-number> <parent-issue-number|none> [--preview] [--json]", "Mutating: sets or clears the parent; --preview does not apply changes."),
        ["github sub-issues"] = new("<project-id> <parent-issue-number> <add|remove> <comma-separated-child-issue-numbers> [--preview] [--json]", "Mutating: changes parent relationships for 1 to 50 children. --preview does not apply changes."),
        ["github dependency-batch"] = new("<project-id> <issue-number> <add|remove> <comma-separated-blocking-issue-numbers> [--preview] [--json]", "Mutating: changes 1 to 50 blocked-by relationships. --preview does not apply changes.")
    };

    private static readonly IReadOnlyDictionary<string, string> FamilyNotes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["config"] = "Show and validate are read-only offline operations. Set atomically updates supported installed settings and requires a service restart.",
        ["projects"] = "Managed project definitions in local Server state. Create/update read CentralProjectDefinition JSON files; existing-project mutations require the current positive revision.",
        ["executions"] = "Inspect the bounded local execution queue. Cancel affects queued work only; reconcile requires an expired uncertain attempt and explicit integration evidence.",
        ["worker"] = "List discovers registered Worker IDs; show inspects detail. Enable, drain and disable control scheduling; drain retains active leases. Token revocation affects authentication or credential delivery.",
        ["worker-token"] = "Creates or revokes registration/API tokens. Bootstrap tokens are one-use, expire in 15 minutes and are printed only to stdout; protect the output.",
        ["backup"] = "Export writes a sensitive archive; validate inspects an archive without restoring it. Restore requires the Server to be stopped and takes an exclusive database access lock. export/restore require explicit database-path.",
        ["credential"] = "Inspect metadata and manage protected credentials and Worker assignments. Create/replace require hidden interactive input or --secret-stdin. Never supply secrets as arguments; secret values are never printed.",
        ["provision"] = "Inspect history and queue typed allowlisted capability actions; no remote shell text. Cancellation affects queued commands only; reconcile requires a verified quiescent node. Node-local policy remains authoritative.",
        ["github"] = "Uses the Server service account's gh login and project-scoped repository. Read access does not establish write authorization. Mutation --preview validates without applying changes; execution labels, comments and Issue closure remain Worker-owned."
    };

    public static string Context(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0 || !Commands.Keys.Any(key => key == arguments[0] || key.StartsWith(arguments[0] + " ", StringComparison.Ordinal))) return "";
        var family = arguments[0];
        var leaf = arguments.Count > 1 ? family + " " + arguments[1] : family;
        return Commands.ContainsKey(leaf) ? leaf : family;
    }

    public static string UsageHint(IReadOnlyList<string> arguments) => $"Run 'codex-server{(Context(arguments) is { Length: > 0 } context ? " " + context : "")} --help' for usage.";

    public static bool TryWrite(IReadOnlyList<string> arguments, TextWriter output)
    {
        // Only recognized family/leaf prefixes select help. Positional values and secrets
        // are never interpolated into either help or usage hints.
        var context = Context(arguments);
        var depth = context.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (context.Length == 0 || arguments.Count != depth + 1 || arguments[depth] is not ("--help" or "-h")) return false;
        if (Commands.TryGetValue(context, out var command))
        {
            output.WriteLine($"Usage: codex-server {context} {command.Syntax}");
            output.WriteLine(command.Notes);
        }
        else
        {
            output.WriteLine($"Usage: codex-server {context} <command> [arguments] [options]");
            foreach (var entry in Commands.Where(entry => entry.Key.StartsWith(context + " ", StringComparison.Ordinal)))
                output.WriteLine($"  {entry.Key[(context.Length + 1)..]} {entry.Value.Syntax}");
            output.WriteLine(FamilyNotes[context]);
            output.WriteLine($"Use 'codex-server {context} <command> --help' for arguments, defaults and safety notes.");
        }
        if (context is "worker" or "worker-token" || context.StartsWith("worker ", StringComparison.Ordinal) || context.StartsWith("worker-token ", StringComparison.Ordinal))
            output.WriteLine("database-path is the local Server SQLite database. Omit only with explicit Server:DataDirectory / Server:DatabasePath (Server__DataDirectory / Server__DatabasePath), or the installed sudo helper's service context; no invoking-user home fallback.");
        else if (context != "backup" && !context.StartsWith("backup ", StringComparison.Ordinal) && context != "config set")
            output.WriteLine("Uses configured local Server state. Options: --Server:<setting>=<value>, --Logging:<setting>=<value>. --json prints structured output. Installed helper: sudo codex-server; configuration comes from /etc/codex-server/server.env.");
        output.WriteLine("--help and -h show this help without opening state or starting the Server.");
        return true;
    }
}
