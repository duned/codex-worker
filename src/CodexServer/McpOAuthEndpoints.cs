namespace CodexServer;

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using System.Net;
using System.Security.Cryptography;
using System.Text;

public static class McpOAuthEndpoints
{
    public static void MapMcpOAuth(this WebApplication app)
    {
        app.MapGet("/api/v1/administration/mcp-connections", async (HttpContext context, AdministrationSessions sessions, McpOAuthStore store) =>
            sessions.Authorized(context) && BrowserAllowed(context, sessions)
                ? Results.Json(await store.ListAsync(context.RequestAborted)) : Results.Unauthorized());
        app.MapDelete("/api/v1/administration/mcp-connections/{id}", async (string id, HttpContext context, AdministrationSessions sessions, McpOAuthStore store) =>
        {
            if (!sessions.Authorized(context) || !BrowserAllowed(context, sessions)) return Results.Unauthorized();
            if (!Guid.TryParseExact(id, "N", out _)) return Error();
            await store.RevokeConnectionAsync(id, context.RequestAborted);
            return Results.NoContent();
        });
        app.MapGet("/.well-known/oauth-protected-resource/mcp", Resource);
        app.MapGet("/.well-known/oauth-protected-resource", Resource);
        app.MapGet("/.well-known/oauth-authorization-server", (ServerConfiguration configuration) =>
            Issuer(configuration) is { } issuer ? Results.Json(new
            {
                issuer, authorization_endpoint = issuer + "/oauth/authorize", token_endpoint = issuer + "/oauth/token",
                revocation_endpoint = issuer + "/oauth/revoke", response_types_supported = new[] { "code" },
                grant_types_supported = new[] { "authorization_code", "refresh_token" },
                token_endpoint_auth_methods_supported = new[] { "none" }, code_challenge_methods_supported = new[] { "S256" },
                scopes_supported = new[] { McpOAuthStore.Scope }, client_id_metadata_document_supported = true,
                authorization_response_iss_parameter_supported = true
            }) : Results.NotFound());
        app.MapMethods("/oauth/authorize", ["GET", "POST"], AuthorizeAsync).RequireRateLimiting("mcp-oauth");
        app.MapPost("/oauth/token", TokenAsync).RequireRateLimiting("mcp-oauth");
        app.MapPost("/oauth/revoke", RevokeAsync).RequireRateLimiting("mcp-oauth");
    }
    public static string? Issuer(ServerConfiguration configuration) =>
        configuration.AdministrationOrigin is { } origin && origin.StartsWith("https://", StringComparison.Ordinal) ? origin : null;
    private static IResult Resource(ServerConfiguration configuration) => Issuer(configuration) is { } issuer
        ? Results.Json(new { resource = issuer + "/mcp", authorization_servers = new[] { issuer }, scopes_supported = new[] { McpOAuthStore.Scope }, bearer_methods_supported = new[] { "header" } })
        : Results.NotFound();
    private static IResult Error(string error = "invalid_request") => Results.Json(new { error }, statusCode: 400);
    private static bool Single(IEnumerable<KeyValuePair<string, StringValues>> fields, params string[] allowed) =>
        fields.All(pair => allowed.Contains(pair.Key, StringComparer.Ordinal) && pair.Value.Count == 1 &&
            pair.Value.ToString().Length <= 2048 && !pair.Value.ToString().Any(char.IsControl));
    private static async Task<IFormCollection?> FormAsync(HttpContext context)
    {
        if (!context.Request.HasFormContentType || context.Request.ContentLength > 16 * 1024) return null;
        if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            size.MaxRequestBodySize = 16 * 1024;
        try
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            return form.Files.Count == 0 ? form : null;
        }
        catch (InvalidDataException) { return null; }
        catch (BadHttpRequestException) { return null; }
    }
    private static bool BrowserAllowed(HttpContext context, AdministrationSessions sessions) =>
        !context.Request.Headers.ContainsKey("Origin") || sessions.OriginAllowed(context);
    private static async Task<IResult> AuthorizeAsync(HttpContext context, ServerConfiguration configuration,
        AdministrationSessions sessions, McpOAuthClients clients, McpOAuthStore store)
    {
        var issuer = Issuer(configuration);
        if (issuer is null) return Results.NotFound();
        if (sessions.ConfigurationFailure(context) is not null || !BrowserAllowed(context, sessions)) return Results.StatusCode(403);
        var mutation = HttpMethods.IsPost(context.Request.Method);
        var session = sessions.Validate(context);
        // Navigation from another site does not carry the Strict cookie. A same-site continuation
        // retains the request while the administrator uses the existing dashboard login in another tab.
        if (session is null)
            return mutation ? Results.Unauthorized() : Html("<h1>Administrator session required</h1><p>Sign in through the existing Server dashboard, then continue.</p><a href='/home' target='_blank' rel='noopener'>Open dashboard</a> <a href='" + WebUtility.HtmlEncode(context.Request.Path + context.Request.QueryString) + "'>Continue authorization</a>");
        if (context.Request.QueryString.Value?.Length > 8192) return Error();
        var form = mutation ? await FormAsync(context) : null;
        if (mutation && (form is null || !sessions.OriginAllowed(context) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(form["csrf"].ToString()), Encoding.UTF8.GetBytes(session.CsrfToken))))
            return Results.StatusCode(403);
        IEnumerable<KeyValuePair<string, StringValues>> fields = mutation && form is not null ? form : context.Request.Query;
        var values = fields.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.Ordinal);
        string Get(string key) => values.GetValueOrDefault(key, "");
        if ((!mutation && (values.ContainsKey("csrf") || values.ContainsKey("decision"))) ||
            (mutation && context.Request.Query.Count != 0)) return Error();
        if (!Single(fields, "client_id", "redirect_uri", "resource", "response_type", "scope", "state", "code_challenge", "code_challenge_method", "csrf", "decision") ||
            Get("response_type") != "code" || Get("resource") != issuer + "/mcp" || Get("state").Length is < 1 or > 1024 ||
            Get("code_challenge_method") != "S256" || Get("code_challenge").Length != 43 ||
            !Get("code_challenge").All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return Error();
        if (Get("scope") != "" && Get("scope") != McpOAuthStore.Scope) return Error("invalid_scope");
        if (!await clients.ValidAsync(Get("client_id"), Get("redirect_uri"), context.RequestAborted)) return Error("invalid_client");
        // Browsers also apply form-action to the OAuth redirect after a form submission.
        // Permit only the validated callback origin in addition to the same-origin consent POST.
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; form-action 'self' " +
            new Uri(Get("redirect_uri")).GetLeftPart(UriPartial.Authority) + "; frame-ancestors 'none'; base-uri 'none'";
        if (!mutation)
        {
            var hidden = string.Concat(values.Select(x => "<input type='hidden' name='" + WebUtility.HtmlEncode(x.Key) + "' value='" + WebUtility.HtmlEncode(x.Value) + "'>"));
            return Html("<h1>Authorize Codex Server MCP</h1><p>Client: " + WebUtility.HtmlEncode(Get("client_id")) +
                "</p><p>Redirect: " + WebUtility.HtmlEncode(Get("redirect_uri")) +
                "</p><p>Permission mcp:admin: read Server, project, Worker and execution status; create and modify Issues and work packages; dispatch and cancel executions; request Worker maintenance and provisioning. This grants all current MCP tools.</p>" +
                "<form method='post' action='/oauth/authorize'>" + hidden + "<input type='hidden' name='csrf' value='" + WebUtility.HtmlEncode(session.CsrfToken) +
                "'><button name='decision' value='allow'>Allow connection</button> <button name='decision' value='deny'>Deny</button></form>");
        }
        var response = new Dictionary<string, string?> { ["state"] = Get("state"), ["iss"] = issuer };
        if (Get("decision") == "deny") response["error"] = "access_denied";
        else if (Get("decision") != "allow") return Error();
        else
        {
            // Recheck the live session after metadata I/O, before creating durable authority.
            if (sessions.Validate(context) != session) return Results.Unauthorized();
            var code = await store.AuthorizeAsync(Get("client_id"), Get("redirect_uri"), Get("resource"), Get("code_challenge"), context.RequestAborted);
            if (code is null) return Results.StatusCode(503);
            response["code"] = code;
        }
        return Results.Redirect(QueryHelpers.AddQueryString(Get("redirect_uri"), response));
    }
    private static IResult Html(string body) => Results.Content("<!doctype html><html lang='en'><meta charset='utf-8'><meta name='viewport' content='width=device-width'><title>Codex Server authorization</title><body>" + body + "</body></html>", "text/html");
    private static async Task<IResult> TokenAsync(HttpContext context, ServerConfiguration configuration, AdministrationSessions sessions, McpOAuthStore store)
    {
        if (Issuer(configuration) is not { } issuer) return Results.NotFound();
        if (!BrowserAllowed(context, sessions)) return Results.StatusCode(403);
        var form = await FormAsync(context);
        if (form is null || !Single(form, "grant_type", "code", "client_id", "redirect_uri", "resource", "code_verifier", "refresh_token", "scope") ||
            context.Request.Headers.ContainsKey("Authorization")) return Error();
        var refresh = form["grant_type"] == "refresh_token";
        if (!refresh && form["grant_type"] != "authorization_code") return Error("unsupported_grant_type");
        if (form["resource"] != issuer + "/mcp") return Error("invalid_target");
        if (form["scope"].ToString() is not ("" or McpOAuthStore.Scope)) return Error("invalid_scope");
        var token = form[refresh ? "refresh_token" : "code"].ToString();
        if (token.Length != 43 || form["client_id"].ToString().Length == 0) return Error("invalid_grant");
        var result = await store.ExchangeAsync(token, form["client_id"].ToString(), form["resource"].ToString(), refresh,
            form["redirect_uri"].ToString(), form["code_verifier"].ToString(), context.RequestAborted);
        return result is null ? Error("invalid_grant") : Results.Json(result);
    }
    private static async Task<IResult> RevokeAsync(HttpContext context, ServerConfiguration configuration, AdministrationSessions sessions, McpOAuthStore store)
    {
        if (Issuer(configuration) is null) return Results.NotFound();
        if (!BrowserAllowed(context, sessions)) return Results.StatusCode(403);
        var form = await FormAsync(context);
        if (form is null || !Single(form, "token", "client_id", "token_type_hint") ||
            string.IsNullOrEmpty(form["token"].ToString()) || string.IsNullOrEmpty(form["client_id"].ToString()) ||
            context.Request.Headers.ContainsKey("Authorization")) return Error();
        await store.RevokeAsync(form["token"].ToString(), form["client_id"].ToString(), context.RequestAborted);
        return Results.Ok();
    }
}
