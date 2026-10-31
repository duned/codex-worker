namespace CodexServer;

using System.Text.Json;

/// <summary>Fetch only operator-trusted HTTPS CIMD origins; redirects, cookies and credentials are disabled.</summary>
public sealed class McpOAuthClients(HttpClient http, ServerConfiguration configuration)
{
    public async Task<bool> ValidAsync(string client, string redirect, CancellationToken ct)
    {
        if (!Uri.TryCreate(client, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath == "/" ||
            !configuration.OAuthClientMetadataOrigins.Contains(uri.GetLeftPart(UriPartial.Authority), StringComparer.Ordinal) ||
            !Uri.TryCreate(redirect, UriKind.Absolute, out var callback) || callback.Scheme != "https" ||
            callback.UserInfo.Length != 0 || callback.Fragment.Length != 0 || callback.HostNameType == UriHostNameType.Unknown) return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var timeout = deadline.Token;
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 64 * 1024) return false;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout);
            using var buffer = new MemoryStream();
            var bytes = new byte[4096];
            int count;
            while ((count = await stream.ReadAsync(bytes, timeout)) != 0)
            {
                if (buffer.Length + count > 64 * 1024) return false;
                buffer.Write(bytes, 0, count);
            }
            using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().GroupBy(x => x.Name, StringComparer.Ordinal).Any(group => group.Count() > 1)) return false;
            if (root.GetProperty("client_id").GetString() != client ||
                !root.GetProperty("redirect_uris").EnumerateArray().Any(x => x.GetString() == redirect)) return false;
            // Accept the current plural declaration, falling back to the legacy singular field.
            return root.TryGetProperty("token_endpoint_auth_methods_supported", out var methods)
                ? methods.EnumerateArray().Any(x => x.GetString() == "none")
                : root.TryGetProperty("token_endpoint_auth_method", out var method) && method.GetString() == "none";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }
}
