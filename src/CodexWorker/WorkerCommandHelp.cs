namespace CodexWorker;

/// <summary>Contextual help for local Worker operations, without configuration or service side effects.</summary>
public static class WorkerCommandHelp
{
    public static void Write(string command, TextWriter? writer = null)
    {
        writer ??= Console.Out;
        if (command == "register")
        {
            writer.WriteLine("Usage: codex-worker register [--server <url>] (--token <registration-token> | --token-stdin) [--capacity 1..8] [--identity-file <path>] [--config <path>] [--json]");
            writer.WriteLine();
            writer.WriteLine("Options:");
            writer.WriteLine("  --server <url>                 Codex Server base URL (HTTP or HTTPS); defaults to installed configuration.");
            writer.WriteLine("  --token <registration-token>   Required unless --token-stdin. One-time registration token.");
            writer.WriteLine("  --token-stdin                  Read the token from stdin; mutually exclusive with --token.");
            writer.WriteLine("  --capacity <1..8>              Execution capacity; defaults to configured maxParallelTasks or 1.");
            writer.WriteLine("  --identity-file <path>         Worker identity path; defaults to configuration or ~/.codex-worker/worker-id.");
            writer.WriteLine("  --config <path>               Load Worker configuration; explicit options override its defaults.");
            writer.WriteLine("  --json                        Write a versioned enrollment result without credentials.");
            writer.WriteLine($"  Installed configuration: {WorkerCommandLine.DefaultConfigurationPath}");
            writer.WriteLine("  Registration, including stdin input, has a 30-second deadline. Cancellation exits 130.");
            writer.WriteLine("  -h, --help                     Show help without contacting the Server or writing identity files.");
            writer.WriteLine();
            writer.WriteLine("Example:");
            writer.WriteLine("  codex-worker register --server https://server.example --token <registration-token>");
            writer.WriteLine("  codex-worker register --server https://server.example --token-stdin --capacity 2");
            return;
        }
        var configOption = "[--config <path>]";
        switch (command)
        {
            case "run":
                writer.WriteLine($"Usage: codex-worker run {configOption}");
                writer.WriteLine("Start normal Worker execution.");
                writer.WriteLine($"Default configuration: {WorkerCommandLine.DefaultConfigurationPath}");
                writer.WriteLine("Example: codex-worker run --config /path/to/worker.yml");
                break;
            case "diagnostics":
            case "status":
                writer.WriteLine($"Usage: codex-worker {command} {configOption} [--json]");
                writer.WriteLine("Show local configuration and tool diagnostics without contacting Codex Server or starting execution.");
                writer.WriteLine("Status and diagnostics use the same local snapshot contract. Service lifecycle and active capacity are unknown.");
                writer.WriteLine("Tool versions do not establish authenticated execution readiness.");
                writer.WriteLine("Options: --json  Write the stable versioned status contract as JSON.");
                writer.WriteLine("Examples: codex-worker status; codex-worker status --config /path/to/worker.yml --json");
                break;
            case "config":
                writer.WriteLine(WorkerConfigurationAdministration.HelpText);
                writer.WriteLine("Example: codex-worker config show --json");
                writer.WriteLine("Example: codex-worker config set worker.provisioning.enabled true");
                break;
            case "config-show":
            case "config-validate":
                writer.WriteLine($"Usage: codex-worker config {command[7..]} [--config <path>] [--json]");
                writer.WriteLine(command == "config-show" ? "Show redacted local Worker configuration." :
                    "Validate Worker and project configuration without starting execution. Invalid configuration exits 2.");
                break;
            case "config-set":
                writer.WriteLine("Usage: codex-worker config set <setting> <value> [--config <path>] [--json]");
                writer.WriteLine("Updates a bounded known Worker setting after validating the complete candidate configuration.");
                writer.WriteLine("Action policy settings accept comma-separated keys; an empty value clears a list.");
                writer.WriteLine("Options: --json  Write the versioned update result as JSON.");
                writer.WriteLine("A service restart is required for the change to take effect.");
                break;
            case "capabilities-list":
            case "capabilities-refresh":
                writer.WriteLine($"Usage: codex-worker capabilities {command[13..]} {configOption} [--json]");
                writer.WriteLine("Observe local capabilities; missing tools are inventory state, not command failure.");
                break;
            case "capabilities":
                writer.WriteLine($"Usage: codex-worker capabilities <list|refresh> {configOption} [--json]");
                writer.WriteLine("List local capability observations or force a fresh detection.");
                writer.WriteLine("Missing capabilities are reported as inventory state.");
                writer.WriteLine("Options: --json  Write the typed capability inventory contract as JSON.");
                writer.WriteLine("Examples: codex-worker capabilities list; codex-worker capabilities refresh --json");
                break;
            case "provision":
                writer.WriteLine($"Usage: codex-worker provision <status|operation> {configOption} [options]");
                writer.WriteLine("Show local policy and capability state, or run a bounded typed capability operation.");
                writer.WriteLine("Operations: install, upgrade, uninstall, detect, check-authentication, logout, check-configuration,");
                writer.WriteLine("            prepare-authentication, login, generate-ssh-key, inspect-ssh-key, remove-ssh-key,");
                writer.WriteLine("            verify-repository-access.");
                writer.WriteLine("Options: --json; verify-repository-access uses --repository <owner/repository>.");
                writer.WriteLine("        install/upgrade/uninstall require explicit --allow-elevation.");
                writer.WriteLine("        --timeout-seconds 5..600 bounds operations (default: 120).");
                writer.WriteLine($"Default configuration: {WorkerCommandLine.DefaultConfigurationPath}");
                writer.WriteLine("Examples:");
                writer.WriteLine("  codex-worker provision status");
                writer.WriteLine("  codex-worker provision login codex-cli");
                writer.WriteLine("  codex-worker provision prepare-authentication github-cli");
                writer.WriteLine("  codex-worker provision check-authentication github-cli");
                writer.WriteLine("  codex-worker provision install git --allow-elevation");
                writer.WriteLine("  codex-worker provision upgrade codex-cli --allow-elevation --json");
                writer.WriteLine("  codex-worker provision uninstall github-cli --config /etc/codex-worker/worker.yml --allow-elevation");
                writer.WriteLine("Privileged action policy keys use tool:<capability-id>:<install|update|uninstall>.");
                break;
            default:
                writer.WriteLine("Usage: codex-worker [command] [options]");
                writer.WriteLine("Commands: run, status, diagnostics, config, capabilities, provision, register, update");
                writer.WriteLine("Use 'codex-worker <command> --help' for command options and examples.");
                writer.WriteLine("Lifecycle: run starts foreground execution; Ctrl+C/SIGTERM requests graceful shutdown.");
                writer.WriteLine("Installed service: sudo systemctl <start|stop|restart> codex-worker; the existing cw restart (rs) helper verifies restart.");
                writer.WriteLine("Update: codex-worker update --help; update owns installer activation and service restart.");
                writer.WriteLine("Local administration exits 0 on completed observation, 2 on rejected/failed commands, 130 on cancellation.");
                writer.WriteLine($"Normal execution defaults to {WorkerCommandLine.DefaultConfigurationPath}.");
                break;
        }
        writer.WriteLine("Options: -h, --help  Show this help.");
    }
}
