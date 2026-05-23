using System.Security.Claims;
using DotnetDiagnosticsMcp.Server.Auth;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

public sealed class OidcJwtAuthOptionsTests
{
    [Fact]
    public void FromConfiguration_WithNoOidcKeys_ReturnsDisabled()
    {
        var options = OidcJwtAuthOptions.FromConfiguration(new ConfigurationBuilder().Build());

        options.IsEnabled.Should().BeFalse();
        options.ScopeClaimNames.Should().BeEmpty();
    }

    [Fact]
    public void FromConfiguration_ParsesCustomScopeClaim_AndRequiredClaims()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MCP_OIDC_ISSUER"] = "https://issuer.example.test/tenant",
            ["MCP_OIDC_AUDIENCE"] = "dotnet-diagnostics-mcp",
            ["MCP_OIDC_SCOPE_CLAIM"] = "roles",
            ["MCP_OIDC_REQUIRED_CLAIMS_JSON"] = "{\"azp\":\"diag-client\",\"tenant\":null}",
        }).Build();

        var options = OidcJwtAuthOptions.FromConfiguration(configuration);

        options.IsEnabled.Should().BeTrue();
        options.MetadataAddress!.AbsoluteUri.Should().Be("https://issuer.example.test/tenant/.well-known/openid-configuration");
        options.ScopeClaimNames.Should().Equal("roles");
        options.RequiredClaims.Should().HaveCount(2);
    }

    [Fact]
    public void TryCreatePrincipal_Merges_Default_Scope_Claims_And_Required_Claims()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MCP_OIDC_ISSUER"] = "https://issuer.example.test",
            ["MCP_OIDC_AUDIENCE"] = "dotnet-diagnostics-mcp",
            ["MCP_OIDC_REQUIRED_CLAIMS_JSON"] = "{\"azp\":\"diag-client\"}",
        }).Build();
        var options = OidcJwtAuthOptions.FromConfiguration(configuration);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("scp", "read-counters eventpipe"),
            new Claim("scope", "heap-read"),
            new Claim("azp", "diag-client"),
            new Claim("preferred_username", "entra-client"),
        }));

        var ok = options.TryCreatePrincipal(principal, out var bearerPrincipal, out var failureMessage);

        ok.Should().BeTrue();
        failureMessage.Should().BeNull();
        bearerPrincipal.Should().NotBeNull();
        bearerPrincipal!.Name.Should().Be("entra-client");
        bearerPrincipal.Scopes.Should().BeEquivalentTo(new[] { "read-counters", "eventpipe", "heap-read" });
    }
}
