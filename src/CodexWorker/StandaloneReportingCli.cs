namespace CodexWorker;

using CodexProvisioning;
using System.Security.Cryptography;
using System.Text.Json;

internal sealed class StandaloneReportingCli(StandaloneReportingClient client, TextWriter output,
    Func<CancellationToken, Task<string>> readSecret)
{
    internal const string Help = "Usage: codex-worker reporting connect --server <https-origin> [--config <path>]\n" +
        "       codex-worker reporting <status|disconnect|disable> [--config <path>]\n" +
        "Connect prints a public observer request for the dashboard, then reads a hidden one-time authorization.\n" +
        "Empty hidden input recovers an uncertain redemption using retained credentials. Never pass tokens as arguments.\n" +
        "Reporting is off until paired. Status is local only; disconnect deletes the local credential and stops reporting.\n" +
        "A running standalone Worker applies setup on its next reporting interval; start it if it is stopped.\n" +
        "Packaged Linux: use sudo codex-worker reporting ... to use the Worker service account. Dashboard revocation is separate.";

    internal static (string Action, string? Server) Parse(IReadOnlyList<string> args)
    {
        if (args is ["status"] or ["disconnect"] or ["disable"]) return (args[0], null);
        if (args is ["connect", "--server", var server] && ObserverProtocol.ValidOrigin(server)) return ("connect", server);
        throw new ArgumentException("Use reporting connect --server <exact HTTPS origin>, reporting status, or reporting disconnect. Authorization must be entered at the hidden prompt.");
    }

    internal async Task<int> ExecuteAsync(WorkerCommandLine command, CancellationToken ct)
    {
        try
        {
            var (action, server) = Parse(command.Arguments);
            var config = GlobalWorkerConfiguration.Load(command.ConfigurationPath ?? WorkerCommandLine.DefaultConfigurationPath);
            var store = new StandaloneReportingStore(config.Reporting.CredentialFile);
            if (action == "status")
            {
                var current = await store.LoadAsync(ct);
                output.WriteLine(current is null ? "Standalone reporting: disabled. No Server requests are made." :
                    $"Standalone reporting: {(current.Accepted && current.Enabled ? "enabled" : "pending")}; observer {current.ObserverId}; Server {current.Server}; last result: {current.LastResult}; last report: {current.LastReportAtUtc?.ToString("O") ?? "none"}.");
                output.WriteLine("Status reads local state; it does not contact the Server or the local Management API.");
                return 0;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(action == "connect" ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(15));
            await using var gate = await store.LockAsync(deadline.Token);
            if (action is "disconnect" or "disable")
            {
                store.Delete();
                output.WriteLine("Standalone reporting disabled now; local observer credential deleted. Existing Server history remains; revoke the observer in the dashboard if required. A future connect creates a new observer identity.");
                return 0;
            }
            if (config.Projects.Ownership != "standalone") throw new ArgumentException("Observer reporting connect requires standalone project ownership.");
            var local = await store.LoadAsync(deadline.Token);
            if (local is not null && local.Server != server) throw new ArgumentException("A different observer Server is retained. Recover it, or explicitly disconnect before pairing with another Server.");
            local ??= new(1, "obs_" + Guid.NewGuid().ToString("N"), server ?? throw new ArgumentException("Server is required."),
                "obr_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), false, false);
            // Publish the recovery proof before any redemption request. It is never displayed.
            await store.SaveAsync(local, deadline.Token);
            output.WriteLine("Copy this public observer request into the Server dashboard's standalone reporting pairing action:");
            output.WriteLine(JsonSerializer.Serialize(new ObserverPairingRequest(1, ObserverProtocol.Mode, local.ObserverId, local.Server), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            output.WriteLine("Paste the one-time authorization (hidden), or press Enter to recover retained credentials:");
            await output.FlushAsync(deadline.Token);
            var authorization = await readSecret(deadline.Token).WaitAsync(deadline.Token);
            output.WriteLine();
            if (authorization.Length != 0 && !ObserverProtocol.ValidAuthorization(authorization)) throw new ArgumentException("Invalid observer authorization format. Paste only the one-time dashboard authorization.");
            await client.PairAsync(local, authorization, deadline.Token);
            await store.SaveAsync(local with { Enabled = true, Accepted = true, LastResult = "paired" }, deadline.Token);
            output.WriteLine($"Reporting setup saved. A running standalone Worker applies it within {config.Reporting.IntervalSeconds} seconds; if stopped, start the Worker. Local execution ownership is unchanged.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            output.WriteLine("Reporting command cancelled or timed out. Pending identity and credentials are retained; recover using the same Server.");
            return WorkerCliOutput.Cancelled;
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException or HttpRequestException or WorkerStartupException)
        {
            // Never print exception messages, response bodies, paths or credentials.
            output.WriteLine("Reporting setup unavailable or rejected. Check command usage, local file access and the HTTPS Server. Retain pending state; retry connect to the same Server with empty hidden input to recover an uncertain redemption.");
            return ProcessExitCodes.StartupFailure;
        }
    }
}
