using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

public sealed class OidcJwtAuthIntegrationTests
{
    private static WebApplicationFactory<Program> CreateFactory(
        TestOidcAuthority authority,
        string? requiredClaimsJson = null,
        params (string Name, string Token, string[] Scopes)[] opaqueTokens)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MCP_OIDC_ISSUER", authority.Issuer);
            builder.UseSetting("MCP_OIDC_AUDIENCE", authority.Audience);
            if (requiredClaimsJson is not null)
            {
                builder.UseSetting("MCP_OIDC_REQUIRED_CLAIMS_JSON", requiredClaimsJson);
            }

            for (var i = 0; i < opaqueTokens.Length; i++)
            {
                builder.UseSetting($"Auth:BearerTokens:{i}:Name", opaqueTokens[i].Name);
                builder.UseSetting($"Auth:BearerTokens:{i}:Token", opaqueTokens[i].Token);
                for (var j = 0; j < opaqueTokens[i].Scopes.Length; j++)
                {
                    builder.UseSetting($"Auth:BearerTokens:{i}:Scopes:{j}", opaqueTokens[i].Scopes[j]);
                }
            }
        });
    }

    private static async Task<McpClient> ConnectWithTokenAsync(WebApplicationFactory<Program> factory, string token)
    {
        var httpClient = factory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
            },
            httpClient,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: CancellationToken.None).ConfigureAwait(false);
    }

    private static JsonElement ParseForbiddenEnvelope(CallToolResult result)
    {
        result.IsError.Should().BeTrue();
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        var json = text[(text.IndexOf('\n') + 1)..];
        return JsonDocument.Parse(json).RootElement.GetProperty("error");
    }

    [Fact]
    public async Task OidcJwt_Can_Start_On_NonLoopback_Binding_Without_Opaque_Tokens()
    {
        await using var authority = await TestOidcAuthority.StartAsync().ConfigureAwait(false);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MCP_OIDC_ISSUER", authority.Issuer);
            builder.UseSetting("MCP_OIDC_AUDIENCE", authority.Audience);
            builder.UseSetting("ASPNETCORE_URLS", "http://0.0.0.0:5130");
        });

        using var client = factory.CreateClient();
        (await client.GetAsync("/health").ConfigureAwait(false)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OidcJwt_And_Opaque_Bearer_Can_Both_Authenticate_When_Enabled()
    {
        await using var authority = await TestOidcAuthority.StartAsync().ConfigureAwait(false);
        var validJwt = authority.CreateToken(
            scopes: new[] { "read-counters" },
            subject: "entra-client",
            claims: new Dictionary<string, string>
            {
                ["azp"] = "diag-client",
                ["preferred_username"] = "entra-client",
            });

        await using var factory = CreateFactory(
            authority,
            requiredClaimsJson: "{\"azp\":\"diag-client\"}",
            ("opaque-viewer", "opaque-secret-123", new[] { "read-counters" }));

        await using var jwtClient = await ConnectWithTokenAsync(factory, validJwt).ConfigureAwait(false);
        var jwtResult = await jwtClient.CallToolAsync(
            "inspect_process",
            arguments: new Dictionary<string, object?> { ["view"] = "list" },
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        (jwtResult.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? string.Empty)
            .Should().NotContain("\"kind\":\"forbidden\"");

        await using var opaqueClient = await ConnectWithTokenAsync(factory, "opaque-secret-123").ConfigureAwait(false);
        var opaqueResult = await opaqueClient.CallToolAsync(
            "inspect_process",
            arguments: new Dictionary<string, object?> { ["view"] = "list" },
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        (opaqueResult.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? string.Empty)
            .Should().NotContain("\"kind\":\"forbidden\"");
    }

    [Fact]
    public async Task OidcJwt_Missing_Tool_Scope_Returns_Forbidden_Envelope()
    {
        await using var authority = await TestOidcAuthority.StartAsync().ConfigureAwait(false);
        var underScopedJwt = authority.CreateToken(
            scopes: new[] { "read-counters" },
            subject: "keycloak-client",
            claims: new Dictionary<string, string> { ["azp"] = "diag-client" });

        await using var factory = CreateFactory(
            authority,
            requiredClaimsJson: "{\"azp\":\"diag-client\"}");

        await using var client = await ConnectWithTokenAsync(factory, underScopedJwt).ConfigureAwait(false);
        var result = await client.CallToolAsync(
            "collect_sample",
            arguments: new Dictionary<string, object?> { ["kind"] = "cpu", ["durationSeconds"] = 1 },
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        var envelope = ParseForbiddenEnvelope(result);
        envelope.GetProperty("kind").GetString().Should().Be("forbidden");
        envelope.GetProperty("tool").GetString().Should().Be("collect_sample");
        envelope.GetProperty("required_scopes").EnumerateArray().Select(x => x.GetString())
            .Should().ContainSingle().Which.Should().Be("eventpipe");
        envelope.GetProperty("principal_scopes").EnumerateArray().Select(x => x.GetString())
            .Should().ContainSingle().Which.Should().Be("read-counters");
    }

    [Fact]
    public async Task OidcJwt_Missing_Required_Claims_Is_Rejected_With401()
    {
        await using var authority = await TestOidcAuthority.StartAsync().ConfigureAwait(false);
        var invalidJwt = authority.CreateToken(
            scopes: new[] { "read-counters" },
            subject: "aws-sso-client");

        await using var factory = CreateFactory(
            authority,
            requiredClaimsJson: "{\"azp\":\"diag-client\"}");

        using var httpClient = factory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", invalidJwt);

        var response = await httpClient.GetAsync("/mcp").ConfigureAwait(false);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Should().Contain("\"kind\":\"unauthenticated\"");
    }
}
