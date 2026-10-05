namespace CodexWorker;

/// <summary>The outbound Worker-to-Server protocol transport. Never follows redirects.</summary>
internal static class WorkerServerHttpTransport
{
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        // TLS uses normal platform chain, validity and hostname verification.
    };

    internal static string[] RequestSecrets(HttpRequestMessage request) =>
        new[] { request.Headers.Authorization?.Parameter ?? string.Empty }
            .Concat(request.Headers.Where(header => header.Key.Contains("Token", StringComparison.OrdinalIgnoreCase))
                .SelectMany(header => header.Value)).ToArray();

    internal static string SafeRequestContext(HttpResponseMessage response, string[] secrets)
    {
        var context = string.Empty;
        if (response.Headers.TryGetValues("X-Codex-Request-Id", out var identifiers))
        {
            var requestId = identifiers.FirstOrDefault();
            if (requestId is { Length: > 0 and <= 100 } && requestId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or ':' or '.'))
                context = $" Request ID: {FailureDiagnosticRedactor.Redact(requestId, secrets)}.";
        }
        return context;
    }

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        WorkerServerSettings.ValidateUrl(request.RequestUri?.AbsoluteUri ?? string.Empty);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        try
        {
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var correlation = SafeRequestContext(response, RequestSecrets(request));
                throw new HttpRequestException($"Codex Server returned HTTP {(int)response.StatusCode} ({response.StatusCode}). Redirects are disabled; configure the final Server endpoint.{correlation}",
                    null, response.StatusCode);
            }

            // Include the response body in the deadline and bound allocations, including
            // chunked responses. Error diagnostics retain their existing smaller bound.
            var limit = response.IsSuccessStatusCode ? 4 * 1024 * 1024 : 16 * 1024;
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            while (body.Length <= limit)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit + 1 - body.Length)), deadline.Token);
                if (read == 0) break;
                await body.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
            }
            if (response.IsSuccessStatusCode && body.Length > limit)
                throw new HttpRequestException("Codex Server response exceeds the protocol size limit.", null, response.StatusCode);
            var content = new ByteArrayContent(body.ToArray());
            foreach (var header in response.Content.Headers)
                if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content.Dispose();
            response.Content = content;
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
