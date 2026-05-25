using System.Reflection;
using DotnetDiagnosticsMcp.Server.Security;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;
using ModelContextProtocol.Server;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Azure;

/// <summary>
/// Scope-coverage tests for <see cref="DiscoverAzureTool"/> (#232). The MCP scope filter
/// is exercised end-to-end by <see cref="ToolScopeAttributesTests"/>; these tests pin
/// the attribute declaration so a future refactor that drops <c>[RequireScope]</c> or
/// renames the scope fails loudly.
/// </summary>
public sealed class DiscoverAzureScopeTests
{
    [Fact]
    public void DiscoverAzure_Declares_AzureDiscovery_Scope()
    {
        var method = typeof(DiscoverAzureTool).GetMethod(
            nameof(DiscoverAzureTool.DiscoverAzureAsync),
            BindingFlags.Public | BindingFlags.Instance);

        method.Should().NotBeNull();
        var require = method!.GetCustomAttribute<RequireScopeAttribute>();
        require.Should().NotBeNull(
            "every [McpServerTool] must declare a scope (RFC 0001 §2 / B5.2 coverage rule)");
        require!.Scopes.Should().Equal("azure-discovery");
    }

    [Fact]
    public void DiscoverAzure_Scope_Constant_Matches_Attribute()
    {
        DiscoverAzureTool.Scope.Should().Be("azure-discovery");
    }

    [Fact]
    public void Registry_Build_Denies_DiscoverAzure_When_Principal_Lacks_Scope()
    {
        var registry = ToolScopeRegistry.Build(new[] { typeof(DiscoverAzureTool) });
        var req = registry.TryGet("discover_azure");
        req.Should().NotBeNull();

        // Principal without azure-discovery → denied (structured 403-style envelope path).
        var anon = TestPrincipalAccessors.WithScopes("orchestrator-list").Current;
        ToolScopeAuthorizationFilter.Authorize(req!.Value, anon)
            .IsAllowed.Should().BeFalse();

        // Holding the literal scope → allowed.
        var allowed = TestPrincipalAccessors.WithScopes("azure-discovery").Current;
        ToolScopeAuthorizationFilter.Authorize(req!.Value, allowed)
            .IsAllowed.Should().BeTrue();

        // No principal at all → denied.
        ToolScopeAuthorizationFilter.Authorize(req!.Value, principal: null)
            .IsAllowed.Should().BeFalse();
    }
}
