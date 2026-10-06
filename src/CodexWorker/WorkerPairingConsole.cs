namespace CodexWorker;

using System.Text;

internal static class WorkerPairingConsole
{
    internal static async Task<string> ReadSecretAsync(CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            throw new WorkerStartupException("Pairing requires a local interactive terminal. Do not redirect authorization input or output; use the supported --token-stdin path for protected automation.");
        var value = new StringBuilder();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Console.KeyAvailable)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
                    continue;
                }
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) return value.ToString();
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (value.Length > 0) value.Length--;
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    if (value.Length >= 4096) throw new WorkerStartupException("Authorization exceeds supported bounds.");
                    value.Append(key.KeyChar);
                }
            }
        }
        finally { value.Clear(); }
    }
}
