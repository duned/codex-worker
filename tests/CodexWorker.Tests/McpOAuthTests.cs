namespace CodexWorker.Tests;

using CodexServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

[Collection("ServerTokenEnvironment")]
public sealed class McpOAuthTests
{
    private const string Origin = "https://server.example";
    private const string Client = "https://client.example/oauth/client.json";
    private const string Redirect = "https://client.example/callback";
    private const string Verifier = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUV";
    private static string Challenge => WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)));

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    public async Task ServerHostOAuthSessionDiscoversToolsEnforcesScopesRefreshesAndRevokes()
    {
        using var directory = new TemporaryDirectory();
        var previous = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "host-oauth-test-management");
        try
        {
            // Only outbound CIMD retrieval is stubbed; all protocol requests use the real host.
            using var metadataHttp = new HttpClient(new MetadataHandler());
            var clients = new McpOAuthClients(metadataHttp, new() { OAuthClientMetadataOrigins = ["https://client.example"] });
            await using var app = await ServerApplication.BuildAsync(
                ["--Server:ListenUrl=http://127.0.0.1:5090", $"--Server:DatabasePath={Path.Combine(directory.Path, "host.db")}",
                 $"--Server:AdministrationOrigin={Origin}", "--Server:OAuthClientMetadataOrigins:0=https://client.example",
                 "--Logging:LogLevel:Default=Warning"],
                githubReadService: new AlwaysEligibleServerGitHubReadService(), oauthClients: clients);
            app.Urls.Clear();
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();
            using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
                { BaseAddress = new Uri(Assert.Single(app.Urls)), Timeout = TimeSpan.FromSeconds(15) };
            browser.DefaultRequestHeaders.Host = "server.example";
            using var http = new HttpClient { BaseAddress = browser.BaseAddress, Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            using (var challenge = await http.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" }))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
                Assert.Contains(Origin + "/.well-known/oauth-protected-resource/mcp", challenge.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
                Assert.Equal("no-store", challenge.Headers.CacheControl?.ToString());
            }
            using var resourceResponse = await http.GetAsync("/.well-known/oauth-protected-resource/mcp");
            var resource = await resourceResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(Origin + "/mcp", resource.GetProperty("resource").GetString());
            Assert.Equal(Origin, Assert.Single(resource.GetProperty("authorization_servers").EnumerateArray()).GetString());
            var metadata = await http.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server");
            Assert.Equal(Origin, metadata.GetProperty("issuer").GetString());
            Assert.Equal(Origin + "/oauth/authorize", metadata.GetProperty("authorization_endpoint").GetString());
            Assert.Equal(Origin + "/oauth/token", metadata.GetProperty("token_endpoint").GetString());
            Assert.Equal(Origin + "/oauth/revoke", metadata.GetProperty("revocation_endpoint").GetString());
            Assert.Contains("S256", metadata.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(x => x.GetString()));
            Assert.True(metadata.GetProperty("client_id_metadata_document_supported").GetBoolean());

            var fields = AuthorizationFields();
            var authorizeUrl = QueryHelpers.AddQueryString("/oauth/authorize", fields.Select(x => new KeyValuePair<string, string?>(x.Key, x.Value)));
            Assert.Contains("Administrator session required", await browser.GetStringAsync(authorizeUrl));
            using (var unauthenticated = await browser.PostAsync("/oauth/authorize", new FormUrlEncodedContent(fields)))
                Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
            browser.DefaultRequestHeaders.Add("Origin", Origin);
            browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "host-oauth-test-management");
            using var login = await browser.PostAsync("/api/v1/administration/session", null);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var session = await login.Content.ReadFromJsonAsync<JsonElement>();
            browser.DefaultRequestHeaders.Authorization = null;
            browser.DefaultRequestHeaders.Add("Cookie", Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0]);
            fields["csrf"] = session.GetProperty("csrfToken").GetString() ?? throw new InvalidOperationException("Missing CSRF token.");
            fields["decision"] = "allow";

            var store = app.Services.GetRequiredService<IRegistryStore>();
            var project = await store.CreateProjectAsync(new("OAuth test", "example/repo", "main", "Controlled OAuth session test"));
            var execution = await store.EnqueueExecutionAsync(new(project.Id, new("issue", "42")));
            var cancelArguments = new { executionRequestId = execution.Id };
            // A second, explicit consent is required to gain write scope; refresh cannot escalate.
            foreach (var scopes in new[] { ServerMcpScopes.Read, ServerMcpScopes.Read + " " + ServerMcpScopes.Write })
            {
                fields["scope"] = scopes;
                var consentUrl = QueryHelpers.AddQueryString("/oauth/authorize", AuthorizationFields()
                    .Select(x => new KeyValuePair<string, string?>(x.Key, x.Key == "scope" ? scopes : x.Value)));
                Assert.Contains("Requested permissions: " + scopes, await browser.GetStringAsync(consentUrl));
                using var consent = await browser.PostAsync("/oauth/authorize", new FormUrlEncodedContent(fields));
                Assert.Equal(HttpStatusCode.Redirect, consent.StatusCode);
                Assert.Equal("no-store", consent.Headers.CacheControl?.ToString());
                var callback = Assert.IsType<Uri>(consent.Headers.Location);
                Assert.Equal(Redirect, callback.GetLeftPart(UriPartial.Path));
                var query = QueryHelpers.ParseQuery(callback.Query);
                Assert.Equal("opaque-state", query["state"]);
                Assert.Equal(Origin, query["iss"]);
                using var exchange = await http.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code", ["client_id"] = Client, ["redirect_uri"] = Redirect,
                    ["resource"] = Origin + "/mcp", ["code"] = query["code"].ToString(), ["code_verifier"] = Verifier
                }));
                Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
                Assert.Equal("no-store", exchange.Headers.CacheControl?.ToString());
                var tokens = Assert.IsType<McpOAuthStore.Tokens>(await exchange.Content.ReadFromJsonAsync<McpOAuthStore.Tokens>());
                Assert.Equal(scopes, tokens.scope);
                using var refreshed = await http.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token", ["client_id"] = Client, ["resource"] = Origin + "/mcp",
                    ["refresh_token"] = tokens.refresh_token
                }));
                Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.access_token);
                using (var replaced = await http.GetAsync("/mcp")) Assert.Equal(HttpStatusCode.Unauthorized, replaced.StatusCode);
                var rotated = Assert.IsType<McpOAuthStore.Tokens>(await refreshed.Content.ReadFromJsonAsync<McpOAuthStore.Tokens>());
                Assert.NotEqual(tokens.refresh_token, rotated.refresh_token);
                Assert.Equal(scopes, rotated.scope);
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rotated.access_token);
                var initialized = await RpcAsync(http, "initialize", new
                {
                    protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "controlled-oauth-client", version = "1" }
                });
                Assert.Equal("Codex Server", initialized.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
                http.DefaultRequestHeaders.Remove("MCP-Protocol-Version");
                http.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-11-25");
                var discovery = await RpcAsync(http, "tools/list", new { });
                var tools = discovery.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
                var cancel = Assert.Single(tools, t => t.GetProperty("name").GetString() == "cancel_execution");
                Assert.False(cancel.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
                Assert.True(cancel.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
                Assert.False(cancel.GetProperty("annotations").GetProperty("openWorldHint").GetBoolean());
                var scheme = Assert.Single(cancel.GetProperty("_meta").GetProperty("securitySchemes").EnumerateArray());
                Assert.Equal("oauth2", scheme.GetProperty("type").GetString());
                Assert.Equal(ServerMcpScopes.Write, Assert.Single(scheme.GetProperty("scopes").EnumerateArray()).GetString());
                var health = await RpcAsync(http, "tools/call", new { name = "server_health", arguments = new { } });
                using var healthBody = JsonDocument.Parse(health.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString() ?? "null");
                Assert.True(healthBody.RootElement.GetProperty("persistenceAvailable").GetBoolean());
                var called = (await RpcAsync(http, "tools/call", new { name = "cancel_execution", arguments = cancelArguments })).GetProperty("result");
                if (scopes == ServerMcpScopes.Read)
                {
                    Assert.True(called.GetProperty("isError").GetBoolean());
                    var denied = Assert.Single(called.GetProperty("_meta").GetProperty("mcp/www_authenticate").EnumerateArray()).GetString();
                    Assert.Contains("insufficient_scope", denied, StringComparison.Ordinal);
                    Assert.Contains("scope=\"mcp:write\"", denied, StringComparison.Ordinal);
                    Assert.Equal("Queued", (await store.GetExecutionAsync(execution.Id))?.State);
                }
                else
                {
                    Assert.False(called.TryGetProperty("isError", out var error) && error.GetBoolean());
                    using var result = JsonDocument.Parse(called.GetProperty("content")[0].GetProperty("text").GetString() ?? "null");
                    Assert.Equal("accepted", result.RootElement.GetProperty("outcome").GetString());
                    Assert.Equal("Cancelled", (await store.GetExecutionAsync(execution.Id))?.State);
                }
                // Scoped OAuth credentials cannot administer connections.
                using (var management = await http.GetAsync("/api/v1/administration/mcp-connections"))
                    Assert.Equal(HttpStatusCode.Unauthorized, management.StatusCode);
                http.DefaultRequestHeaders.Authorization = null;
                using var revoked = await http.PostAsync("/oauth/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
                    { ["token"] = rotated.refresh_token, ["client_id"] = Client }));
                Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rotated.access_token);
                using (var rejected = await http.GetAsync("/mcp")) Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
                http.DefaultRequestHeaders.Authorization = null;
                using var revokedRefresh = await http.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token", ["client_id"] = Client, ["resource"] = Origin + "/mcp", ["refresh_token"] = rotated.refresh_token
                }));
                Assert.Equal(HttpStatusCode.BadRequest, revokedRefresh.StatusCode);
            }
            await app.StopAsync();
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", previous); }
    }

    private static async Task<JsonElement> RpcAsync(HttpClient http, string method, object parameters)
    {
        using var response = await http.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method, @params = parameters });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var json = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? body.Split('\n').Last(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..] : body;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task ScopedReferencesValidateIssuerAudienceAndGrantWithoutRefreshEscalation()
    {
        using var directory = new TemporaryDirectory();
        var previous = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "scoped-oauth-management");
        try
        {
            var database = Path.Combine(directory.Path, "oauth.db");
            var store = new McpOAuthStore(database, new() { AdministrationOrigin = Origin }, new Clock());
            await store.InitializeAsync();
            Assert.Null(await store.AuthorizeAsync(Client, Redirect, Origin + "/mcp", Challenge, default, "mcp:admin"));
            const string scopes = ServerMcpScopes.Read + " " + ServerMcpScopes.Write;
            var code = Assert.IsType<string>(await store.AuthorizeAsync(Client, Redirect, Origin + "/mcp", Challenge, default, scopes));
            var tokens = Assert.IsType<McpOAuthStore.Tokens>(await store.ExchangeAsync(code, Client, Origin + "/mcp", false, Redirect, Verifier, default));
            Assert.Equal(scopes, await store.GrantedScopesAsync(tokens.access_token, default));
            Assert.Null(await store.ExchangeAsync(tokens.refresh_token, Client, Origin + "/mcp", true, null, null, default, ServerMcpScopes.Maintain));
            Assert.True(await store.AuthorizedAsync(tokens.access_token, default));
            tokens = Assert.IsType<McpOAuthStore.Tokens>(await store.ExchangeAsync(tokens.refresh_token, Client, Origin + "/mcp", true, null, null, default,
                ServerMcpScopes.Write + " " + ServerMcpScopes.Read));
            Assert.Equal(scopes, tokens.scope);

            await using var connection = new SqliteConnection($"Data Source={database}");
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT body FROM mcp_oauth_grants";
            var original = Assert.IsType<string>(await command.ExecuteScalarAsync());
            var grant = Assert.IsType<McpOAuthStore.Grant>(JsonSerializer.Deserialize<McpOAuthStore.Grant>(original));
            foreach (var invalid in new[]
            {
                grant with { Issuer = "https://wrong.example" },
                grant with { Resource = "https://wrong.example/mcp" },
                grant with { ApprovedScope = "mcp:admin" },
                grant with { Expires = DateTimeOffset.UnixEpoch }
            })
            {
                command.CommandText = "UPDATE mcp_oauth_grants SET body=$body";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(invalid));
                await command.ExecuteNonQueryAsync();
                Assert.False(await store.AuthorizedAsync(tokens.access_token, default));
            }
            command.Parameters.Clear();
            command.CommandText = "UPDATE mcp_oauth_grants SET body=$body";
            command.Parameters.AddWithValue("$body", original);
            await command.ExecuteNonQueryAsync();
            Assert.True(await store.AuthorizedAsync(tokens.access_token, default));
            await store.RevokeAsync(tokens.refresh_token, Client, default);
            Assert.Null(await store.GrantedScopesAsync(tokens.access_token, default));
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", previous); }
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task CredentialsSurviveRestartAreDigestedRotateAndRevokeAsAFamily()
    {
        using var directory = new TemporaryDirectory();
        var previous = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "oauth-management-sentinel");
        try
        {
            var clock = new Clock();
            var configuration = new ServerConfiguration { AdministrationOrigin = Origin };
            var database = Path.Combine(directory.Path, "oauth.db");
            var store = new McpOAuthStore(database, configuration, clock);
            await store.InitializeAsync();
            var code = Assert.IsType<string>(await store.AuthorizeAsync(Client, Redirect, Origin + "/mcp", Challenge, default));
            Assert.Null(await store.ExchangeAsync(code, Client, "https://other.example/mcp", false, Redirect, Verifier, default));
            Assert.Null(await store.ExchangeAsync(code, Client + "wrong", Origin + "/mcp", false, Redirect, Verifier, default));
            Assert.Null(await store.ExchangeAsync(code, Client, Origin + "/mcp", false, Redirect + "wrong", Verifier, default));
            Assert.Null(await store.ExchangeAsync(code, Client, Origin + "/mcp", false, Redirect, new string('a', 43), default));
            Assert.Null(await store.ExchangeAsync(code, Client, Origin + "/mcp", false, Redirect, "short", default));
            var tokens = Assert.IsType<McpOAuthStore.Tokens>(await store.ExchangeAsync(code, Client, Origin + "/mcp", false, Redirect, Verifier, default));
            Assert.True(await store.AuthorizedAsync(tokens.access_token, default));
            Assert.False(await store.AuthorizedAsync(tokens.refresh_token, default));
            store = new McpOAuthStore(database, configuration, clock);
            await store.InitializeAsync();
            Assert.True(await store.AuthorizedAsync(tokens.access_token, default));
            await using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT body FROM mcp_oauth_grants UNION ALL SELECT hash FROM mcp_oauth_credentials";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    foreach (var secret in new[] { code, tokens.access_token, tokens.refresh_token, "oauth-management-sentinel" })
                        Assert.DoesNotContain(secret, reader.GetString(0), StringComparison.Ordinal);
            }
            var rotated = Assert.IsType<McpOAuthStore.Tokens>(await store.ExchangeAsync(tokens.refresh_token, Client, Origin + "/mcp", true, null, null, default));
            Assert.NotEqual(tokens.refresh_token, rotated.refresh_token);
            Assert.False(await store.AuthorizedAsync(tokens.access_token, default));
            Assert.True(await store.AuthorizedAsync(rotated.access_token, default));
            Assert.Null(await store.ExchangeAsync(tokens.refresh_token, Client, Origin + "/mcp", true, null, null, default));
            Assert.False(await store.AuthorizedAsync(rotated.access_token, default));
            Assert.Null(await store.ExchangeAsync(rotated.refresh_token, Client, Origin + "/mcp", true, null, null, default));

            code = Assert.IsType<string>(await store.AuthorizeAsync(Client, Redirect, Origin + "/mcp", Challenge, default));
            tokens = Assert.IsType<McpOAuthStore.Tokens>(await store.ExchangeAsync(code, Client, Origin + "/mcp", false, Redirect, Verifier, default));
            Assert.Null(await store.ExchangeAsync(code, Client, Origin + "/mcp", false, Redirect, Verifier, default));
            Assert.False(await store.AuthorizedAsync(tokens.access_token, default));

            code = Assert.IsType<string>(await store.AuthorizeAsync(Client, Redirect, Origin + "/mcp", Challenge, default));
            tokens = Assert.IsType<McpOAuthStore.Tokens>(await store.ExchangeAsync(code, Client, Origin + "/mcp", false, Redirect, Verifier, default));
            await store.RevokeAsync(tokens.refresh_token, Client + "wrong", default);
            Assert.True(await store.AuthorizedAsync(tokens.access_token, default));
            await store.RevokeAsync(tokens.refresh_token, Client, default);
            Assert.False(await store.AuthorizedAsync(tokens.access_token, default));
            Assert.Null(await store.ExchangeAsync(tokens.refresh_token, Client, Origin + "/mcp", true, null, null, default));

            code = Assert.IsType<string>(await store.AuthorizeAsync(Client, Redirect, Origin + "/mcp", Challenge, default));
            clock.Now += TimeSpan.FromMinutes(5);
            Assert.Null(await store.ExchangeAsync(code, Client, Origin + "/mcp", false, Redirect, Verifier, default));
            code = Assert.IsType<string>(await store.AuthorizeAsync(Client, Redirect, Origin + "/mcp", Challenge, default));
            tokens = Assert.IsType<McpOAuthStore.Tokens>(await store.ExchangeAsync(code, Client, Origin + "/mcp", false, Redirect, Verifier, default));
            clock.Now += TimeSpan.FromMinutes(10);
            Assert.False(await store.AuthorizedAsync(tokens.access_token, default));
            Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "changed-management-sentinel");
            Assert.Null(await store.ExchangeAsync(tokens.refresh_token, Client, Origin + "/mcp", true, null, null, default));
            Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "oauth-management-sentinel");
            clock.Now += TimeSpan.FromDays(90);
            Assert.Null(await store.ExchangeAsync(tokens.refresh_token, Client, Origin + "/mcp", true, null, null, default));
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", previous); }
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    public async Task DiscoveryConsentPkceAndAdministrativeRevocationUseExistingSessionBoundary()
    {
        using var directory = new TemporaryDirectory();
        var previous = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "oauth-test-management");
        try
        {
            var configuration = new ServerConfiguration { AdministrationOrigin = Origin, OAuthClientMetadataOrigins = ["https://client.example"] };
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(configuration);
            builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
            builder.Services.AddSingleton<AdministrationSessions>();
            builder.Services.AddSingleton(new McpOAuthStore(Path.Combine(directory.Path, "oauth.db"), configuration, TimeProvider.System));
            using var metadataHttp = new HttpClient(new MetadataHandler());
            builder.Services.AddSingleton(new McpOAuthClients(metadataHttp, configuration));
            builder.Services.AddRateLimiter(options => Microsoft.AspNetCore.RateLimiting.RateLimiterOptionsExtensions.AddFixedWindowLimiter(options, "mcp-oauth", limiter =>
            { limiter.PermitLimit = 100; limiter.Window = TimeSpan.FromMinutes(1); }));
            await using var app = builder.Build();
            app.UseRateLimiter();
            app.MapMcpOAuth();
            await app.Services.GetRequiredService<McpOAuthStore>().InitializeAsync();
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(Assert.Single(app.Urls)) };
            http.DefaultRequestHeaders.Host = "server.example";
            http.DefaultRequestHeaders.Add("X-Forwarded-Host", "attacker.example");
            var metadata = await http.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server");
            Assert.Equal(Origin, metadata.GetProperty("issuer").GetString());
            Assert.True(metadata.GetProperty("client_id_metadata_document_supported").GetBoolean());
            var resource = await http.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource/mcp");
            Assert.Equal(Origin + "/mcp", resource.GetProperty("resource").GetString());
            var fields = AuthorizationFields();
            var url = QueryHelpers.AddQueryString("/oauth/authorize", fields.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)));
            using (var response = await http.PostAsync("/oauth/authorize", new FormUrlEncodedContent(fields)))
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("Administrator session required", await http.GetStringAsync(url));
            var context = new DefaultHttpContext();
            context.Request.Host = new("server.example");
            var sessions = app.Services.GetRequiredService<AdministrationSessions>();
            var session = Assert.IsType<AdministrationSessions.Session>(sessions.Create(context));
            http.DefaultRequestHeaders.Add("Cookie", context.Response.Headers.SetCookie.ToString().Split(';')[0]);
            Assert.Contains("Requested permissions: mcp:read", await http.GetStringAsync(url));
            using (var duplicate = await http.GetAsync(url + "&state=duplicate"))
                Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
            http.DefaultRequestHeaders.Host = "attacker.example";
            using (var wrongHost = await http.GetAsync(url)) Assert.Equal(HttpStatusCode.Forbidden, wrongHost.StatusCode);
            http.DefaultRequestHeaders.Host = "server.example";
            fields["decision"] = "allow";
            using (var denied = await http.PostAsync("/oauth/authorize", new FormUrlEncodedContent(fields)))
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            fields["csrf"] = session.CsrfToken;
            http.DefaultRequestHeaders.Add("Origin", Origin);
            foreach (var (key, value) in new[] { ("resource", "https://evil.example/mcp"), ("scope", "unknown"), ("redirect_uri", Redirect + "evil"), ("code_challenge_method", "plain"), ("state", ""), ("client_id", "https://untrusted.example/client.json") })
            {
                var original = fields[key];
                fields[key] = value;
                using var invalid = await http.PostAsync("/oauth/authorize", new FormUrlEncodedContent(fields));
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                fields[key] = original;
            }
            using var consent = await http.PostAsync("/oauth/authorize", new FormUrlEncodedContent(fields));
            Assert.Equal(HttpStatusCode.Redirect, consent.StatusCode);
            var location = Assert.IsType<Uri>(consent.Headers.Location);
            var query = QueryHelpers.ParseQuery(location.Query);
            Assert.Equal("opaque-state", query["state"]);
            Assert.Equal(Origin, query["iss"]);
            var tokenFields = new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["client_id"] = Client,
                ["code"] = query["code"].ToString(), ["redirect_uri"] = Redirect, ["resource"] = Origin + "/mcp", ["code_verifier"] = Verifier };
            http.DefaultRequestHeaders.Remove("Origin");
            using var exchange = await http.PostAsync("/oauth/token", new FormUrlEncodedContent(tokenFields));
            Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
            var tokens = Assert.IsType<McpOAuthStore.Tokens>(await exchange.Content.ReadFromJsonAsync<McpOAuthStore.Tokens>());
            Assert.True(await app.Services.GetRequiredService<McpOAuthStore>().AuthorizedAsync(tokens.access_token, default));
            var refreshFields = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["client_id"] = Client,
                ["refresh_token"] = tokens.refresh_token, ["resource"] = Origin + "/mcp", ["scope"] = "unknown" };
            using (var invalidScope = await http.PostAsync("/oauth/token", new FormUrlEncodedContent(refreshFields)))
            {
                Assert.Equal(HttpStatusCode.BadRequest, invalidScope.StatusCode);
                Assert.Contains("invalid_scope", await invalidScope.Content.ReadAsStringAsync());
            }
            refreshFields.Remove("scope");
            using (var refreshed = await http.PostAsync("/oauth/token", new FormUrlEncodedContent(refreshFields)))
            {
                Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
                Assert.False(await app.Services.GetRequiredService<McpOAuthStore>().AuthorizedAsync(tokens.access_token, default));
                tokens = Assert.IsType<McpOAuthStore.Tokens>(await refreshed.Content.ReadFromJsonAsync<McpOAuthStore.Tokens>());
            }
            using (var badCsrf = await http.DeleteAsync("/api/v1/administration/mcp-connections/00000000000000000000000000000000"))
                Assert.Equal(HttpStatusCode.Unauthorized, badCsrf.StatusCode);
            var connections = Assert.IsType<McpOAuthStore.Connection[]>(await http.GetFromJsonAsync<McpOAuthStore.Connection[]>("/api/v1/administration/mcp-connections"));
            http.DefaultRequestHeaders.Add("Origin", Origin);
            http.DefaultRequestHeaders.Add(AdministrationSessions.CsrfHeader, session.CsrfToken);
            using var revoked = await http.DeleteAsync("/api/v1/administration/mcp-connections/" + Assert.Single(connections).Id);
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
            Assert.False(await app.Services.GetRequiredService<McpOAuthStore>().AuthorizedAsync(tokens.access_token, default));
            http.DefaultRequestHeaders.Remove("Origin");
            using (var unknown = await http.PostAsync("/oauth/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["token"] = new string('x', 43), ["client_id"] = Client }))) Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
            using (var alreadyRevoked = await http.PostAsync("/oauth/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["token"] = tokens.refresh_token, ["client_id"] = Client }))) Assert.Equal(HttpStatusCode.OK, alreadyRevoked.StatusCode);
            http.DefaultRequestHeaders.Add("Origin", Origin);
            fields["decision"] = "deny";
            using var denial = await http.PostAsync("/oauth/authorize", new FormUrlEncodedContent(fields));
            Assert.Contains("error=access_denied", denial.Headers.Location?.Query);
            Assert.Contains("iss=", denial.Headers.Location?.Query);
            await app.StopAsync();
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", previous); }
    }

    [Theory]
    [InlineData("https://client.example/oauth/client.json", Redirect, true)]
    [InlineData("https://client.example/oauth/client.json", Redirect + "/other", false)]
    [InlineData("https://other.example/oauth/client.json", Redirect, false)]
    [InlineData("https://client.example/oauth/other.json", Redirect, false)]
    [InlineData("http://client.example/oauth/client.json", Redirect, false)]
    [InlineData("https://client.example/oauth/client.json#fragment", Redirect, false)]
    [InlineData("https://client.example/oauth/client.json", "https://user:pass@client.example/callback", false)]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public async Task CimdRequiresTrustedHttpsOriginExactIdentityRedirectAndPublicClientMethod(string client, string redirect, bool expected)
    {
        using var http = new HttpClient(new MetadataHandler());
        var clients = new McpOAuthClients(http, new() { OAuthClientMetadataOrigins = ["https://client.example"] });
        Assert.Equal(expected, await clients.ValidAsync(client, redirect, default));
    }
    [Fact]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public async Task CimdRejectsInvalidDocumentsAndHonorsCancellation()
    {
        foreach (var payload in new[]
        {
            "{}", "[]", "invalid-json", new string(' ', 65537),
            JsonSerializer.Serialize(new { client_id = Client, redirect_uris = new[] { Redirect }, token_endpoint_auth_method = "client_secret_post" }),
            JsonSerializer.Serialize(new { client_id = Client, redirect_uris = new[] { Redirect }, token_endpoint_auth_methods_supported = new[] { "private_key_jwt" }, token_endpoint_auth_method = "none" }),
            "{\"client_id\":\"" + Client + "\",\"client_id\":\"" + Client + "\",\"redirect_uris\":[\"" + Redirect + "\"],\"token_endpoint_auth_method\":\"none\"}"
        })
        {
            using var http = new HttpClient(new PayloadHandler(payload));
            var clients = new McpOAuthClients(http, new() { OAuthClientMetadataOrigins = ["https://client.example"] });
            Assert.False(await clients.ValidAsync(Client, Redirect, default));
        }
        using var legacyHttp = new HttpClient(new PayloadHandler(JsonSerializer.Serialize(new
        { client_id = Client, redirect_uris = new[] { Redirect }, token_endpoint_auth_method = "none" })));
        var legacy = new McpOAuthClients(legacyHttp, new() { OAuthClientMetadataOrigins = ["https://client.example"] });
        Assert.True(await legacy.ValidAsync(Client, Redirect, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => legacy.ValidAsync(Client, Redirect, cancellation.Token));
    }
    private sealed class PayloadHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });
    }
    private static Dictionary<string, string> AuthorizationFields() => new()
    {
        ["client_id"] = Client, ["redirect_uri"] = Redirect, ["resource"] = Origin + "/mcp", ["scope"] = McpOAuthStore.Scope,
        ["response_type"] = "code", ["state"] = "opaque-state", ["code_challenge"] = Challenge, ["code_challenge_method"] = "S256"
    };
    private sealed class MetadataHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { client_id = Client, redirect_uris = new[] { Redirect }, token_endpoint_auth_methods_supported = new[] { "none", "private_key_jwt" }, token_endpoint_auth_method = "private_key_jwt" })
        });
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mcp-oauth-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
