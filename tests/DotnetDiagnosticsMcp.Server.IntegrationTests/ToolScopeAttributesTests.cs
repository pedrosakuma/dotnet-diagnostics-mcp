using System.Collections.Immutable;
using DotnetDiagnosticsMcp.Server.Security;
using FluentAssertions;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

/// <summary>
/// B5.2 — coverage for the attribute surface and the reflective registry build that
/// pre-computes the per-tool scope map. These tests live in-process (no
/// WebApplicationFactory) and run sub-second.
/// </summary>
public sealed class ToolScopeAttributesTests
{
    [Fact]
    public void RequireScope_Accepts_NonEmpty_Scopes()
    {
        var attr = new RequireScopeAttribute("read-counters");
        attr.Scopes.Should().ContainSingle().Which.Should().Be("read-counters");

        var stacked = new RequireScopeAttribute("ptrace", "dump-write");
        stacked.Scopes.Should().Equal("ptrace", "dump-write");
    }

    [Fact]
    public void RequireScope_Rejects_Empty_Arg_List()
    {
        var act = () => new RequireScopeAttribute();
        act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("scopes");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void RequireScope_Rejects_Empty_Or_Whitespace_Entries(string? bad)
    {
        var act = () => new RequireScopeAttribute("ok", bad!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RequireAnyScope_Round_Trips()
    {
        var attr = new RequireAnyScopeAttribute("read-counters", "eventpipe");
        attr.Scopes.Should().Equal("read-counters", "eventpipe");
    }

    [Fact]
    public void ToolScopeRegistry_Build_Indexes_Decorated_Tools()
    {
        var registry = ToolScopeRegistry.Build(new[] { typeof(SampleSurface) });

        var req = registry.TryGet("sample_tool");
        req.Should().NotBeNull();
        req!.Value.All.Should().Equal("read-counters");

        var anyReq = registry.TryGet("sample_any_tool");
        anyReq.Should().NotBeNull();
        anyReq!.Value.IsAny.Should().BeTrue();
        anyReq.Value.Any.Should().Equal("read-counters", "eventpipe");

        var stacked = registry.TryGet("sample_stacked");
        stacked!.Value.All.Should().Equal("ptrace", "dump-write");
    }

    [Fact]
    public void ToolScopeRegistry_Throws_When_Tool_Has_No_Scope()
    {
        var act = () => ToolScopeRegistry.Build(new[] { typeof(MissingScopeSurface) });
        act.Should().Throw<InvalidOperationException>().WithMessage("*Missing*sample_unscoped*");
    }

    [Fact]
    public void ToolScopeRegistry_Throws_When_Tool_Has_Both_Attributes()
    {
        var act = () => ToolScopeRegistry.Build(new[] { typeof(ConflictingSurface) });
        act.Should().Throw<InvalidOperationException>().WithMessage("*both [RequireScope] and [RequireAnyScope]*");
    }

    [Fact]
    public void ToolScopeRegistry_Production_Surface_Has_Full_Coverage()
    {
        // Every [McpServerTool] in the shipping tool surface must declare a scope, including
        // the orchestrator surface (the conditional registration in DiagnosticServiceRegistration
        // only flips whether OrchestratorTools is *registered*, not whether its members declare
        // a scope). A missing scope fails Build() — the assertion is "this does not throw".
        var registry = ToolScopeRegistry.Build(new[]
        {
            typeof(DotnetDiagnosticsMcp.Server.Tools.DiagnosticTools),
            typeof(DotnetDiagnosticsMcp.Server.Tools.OrchestratorTools),
            typeof(DotnetDiagnosticsMcp.Server.Tools.ListOrchestratorTool),
        });

        // Spot-check a representative tool from each scope family to detect accidental
        // regressions in the mapping table.
        registry.TryGet("snapshot_counters")!.Value.All.Should().Equal("read-counters");
        registry.TryGet("collect_cpu_sample")!.Value.All.Should().Equal("eventpipe");
        registry.TryGet("inspect_live_heap")!.Value.All.Should().Equal("heap-read", "ptrace");
        registry.TryGet("collect_process_dump")!.Value.All.Should().Equal("dump-write", "ptrace");
        registry.TryGet("query_collection")!.Value.Any.Should().Equal("read-counters", "eventpipe");
        registry.TryGet("export_investigation_summary")!.Value.All.Should().Equal("investigation-export");
        registry.TryGet("list_pods")!.Value.All.Should().Equal("orchestrator-list");
        registry.TryGet("attach_to_pod")!.Value.All.Should().Equal("orchestrator-attach");
        registry.TryGet("list_orchestrator")!.Value.Any.Should().Equal("orchestrator-list", "orchestrator-attach");
    }

    // --- fixtures -----------------------------------------------------------------

    private static class SampleSurface
    {
        [RequireScope("read-counters")]
        [McpServerTool(Name = "sample_tool")]
        public static int A() => 0;

        [RequireAnyScope("read-counters", "eventpipe")]
        [McpServerTool(Name = "sample_any_tool")]
        public static int B() => 0;

        [RequireScope("ptrace", "dump-write")]
        [McpServerTool(Name = "sample_stacked")]
        public static int C() => 0;
    }

    private static class MissingScopeSurface
    {
        [McpServerTool(Name = "sample_unscoped")]
        public static int A() => 0;
    }

    private static class ConflictingSurface
    {
        [RequireScope("read-counters")]
        [RequireAnyScope("eventpipe")]
        [McpServerTool(Name = "sample_conflicting")]
        public static int A() => 0;
    }
}

/// <summary>
/// Pure-function tests for <see cref="ToolScopeAuthorizationFilter.Authorize"/>: covers
/// wildcard, AND, OR, missing-principal, and partial-match cases without spinning up an
/// MCP server.
/// </summary>
public sealed class ToolScopeAuthorizationTests
{
    private static BearerPrincipal With(params string[] scopes) => new(
        name: "test",
        scopes: ImmutableHashSet.Create(scopes));

    private static ToolScopeRegistry.Requirement All(params string[] scopes) =>
        new(All: ImmutableArray.Create(scopes), Any: ImmutableArray<string>.Empty);

    private static ToolScopeRegistry.Requirement Any(params string[] scopes) =>
        new(All: ImmutableArray<string>.Empty, Any: ImmutableArray.Create(scopes));

    [Fact]
    public void Authorize_Denies_When_No_Principal()
    {
        var decision = ToolScopeAuthorizationFilter.Authorize(All("read-counters"), principal: null);
        decision.IsAllowed.Should().BeFalse();
        decision.MissingScope.Should().Be("read-counters");
    }

    [Fact]
    public void Authorize_Allows_Single_Match()
    {
        var decision = ToolScopeAuthorizationFilter.Authorize(
            All("read-counters"), With("read-counters", "eventpipe"));
        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Authorize_Stacked_Scope_Requires_All()
    {
        var req = All("ptrace", "dump-write");
        ToolScopeAuthorizationFilter.Authorize(req, With("ptrace", "dump-write"))
            .IsAllowed.Should().BeTrue();

        var partial = ToolScopeAuthorizationFilter.Authorize(req, With("ptrace"));
        partial.IsAllowed.Should().BeFalse();
        partial.MissingScope.Should().Be("dump-write");
    }

    [Fact]
    public void Authorize_AnyOf_Matches_First_Held_Scope()
    {
        var req = Any("read-counters", "eventpipe");
        ToolScopeAuthorizationFilter.Authorize(req, With("eventpipe"))
            .IsAllowed.Should().BeTrue();

        var none = ToolScopeAuthorizationFilter.Authorize(req, With("orchestrator-list"));
        none.IsAllowed.Should().BeFalse();
        none.MissingScope.Should().Be("read-counters");
    }

    [Fact]
    public void Authorize_Root_Wildcard_Satisfies_Every_Requirement()
    {
        var root = With(BearerPrincipal.RootScope);
        ToolScopeAuthorizationFilter.Authorize(All("ptrace", "dump-write"), root)
            .IsAllowed.Should().BeTrue();
        ToolScopeAuthorizationFilter.Authorize(Any("read-counters", "eventpipe"), root)
            .IsAllowed.Should().BeTrue();

        var star = With(BearerPrincipal.RootScopeAlt);
        ToolScopeAuthorizationFilter.Authorize(All("heap-read"), star)
            .IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void HasExplicitScope_Does_Not_Honour_Wildcard()
    {
        // Modifier-scope guards (RFC §2.3-§2.7) must NOT fire just because the principal
        // is root — sensitive-heap-read / symbols-remote / eventsource-any / orchestrator-admin
        // are explicit additive opt-ins by design.
        var root = With(BearerPrincipal.RootScope);
        root.HasScope("sensitive-heap-read").Should().BeTrue();        // wildcard honoured
        root.HasExplicitScope("sensitive-heap-read").Should().BeFalse(); // literal membership only

        var explicitGrant = With(BearerPrincipal.RootScope, "sensitive-heap-read");
        explicitGrant.HasExplicitScope("sensitive-heap-read").Should().BeTrue();
    }
}
