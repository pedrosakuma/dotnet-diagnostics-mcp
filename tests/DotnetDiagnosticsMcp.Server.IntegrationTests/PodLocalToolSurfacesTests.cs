using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using DotnetDiagnosticsMcp.Server.Hosting;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

/// <summary>
/// Verifies <see cref="PodLocalToolSurfaces"/> stays the single source of truth for tool
/// registration. Every consumer (scope registry, deprecation registry, orchestrator proxy
/// allowlist, and the SDK's <c>WithTools&lt;&gt;()</c> chain) reads from it; if a new tool
/// surface is added there, these tests guarantee the registries and the allowlist will see
/// it without a parallel edit.
/// </summary>
public sealed class PodLocalToolSurfacesTests
{
    [Fact]
    public void Always_Includes_Every_PodLocal_ToolSurface()
    {
        PodLocalToolSurfaces.Always.Should().Contain(new[]
        {
            typeof(DiagnosticTools),
            typeof(CollectEventsTool),
            typeof(CollectSampleTool),
            typeof(GetBytesTool),
            typeof(InspectProcessTool),
            typeof(InspectHeapTool),
            typeof(QuerySnapshotTool),
        });
    }

    [Fact]
    public void OrchestratorOnly_Lists_Orchestrator_Management_Surfaces_Only()
    {
        PodLocalToolSurfaces.OrchestratorOnly.Should().BeEquivalentTo(new[]
        {
            typeof(OrchestratorTools),
            typeof(ListOrchestratorTool),
        });
    }

    [Fact]
    public void Proxyable_Equals_Always_And_Excludes_OrchestratorOnly()
    {
        PodLocalToolSurfaces.Proxyable.Should().BeEquivalentTo(PodLocalToolSurfaces.Always);
        PodLocalToolSurfaces.Proxyable.Should().NotIntersectWith(PodLocalToolSurfaces.OrchestratorOnly);
    }

    [Fact]
    public void GetSurfaceTypes_Without_Orchestrator_Returns_Only_Always()
    {
        var surfaces = PodLocalToolSurfaces.GetSurfaceTypes(enableOrchestratorTools: false);

        surfaces.Should().BeEquivalentTo(PodLocalToolSurfaces.Always);
    }

    [Fact]
    public void GetSurfaceTypes_With_Orchestrator_Returns_Always_Plus_OrchestratorOnly()
    {
        var surfaces = PodLocalToolSurfaces.GetSurfaceTypes(enableOrchestratorTools: true);

        surfaces.Should().BeEquivalentTo(
            PodLocalToolSurfaces.Always.Concat(PodLocalToolSurfaces.OrchestratorOnly));
    }

    [Fact]
    public void GetSurfaceTypes_Returns_Defensive_Copy()
    {
        var first = PodLocalToolSurfaces.GetSurfaceTypes(enableOrchestratorTools: false);
        first[0] = typeof(object);

        var second = PodLocalToolSurfaces.GetSurfaceTypes(enableOrchestratorTools: false);

        second[0].Should().NotBe(typeof(object));
    }

    /// <summary>
    /// Regression for the omission seen during the RFC 0002 fleet cascade — the orchestrator
    /// proxy allowlist used to hard-code a smaller subset and silently dropped any new
    /// pod-local tool surface (CollectEventsTool / InspectHeapTool went missing in Wave 2).
    /// Sourcing the allowlist from <see cref="PodLocalToolSurfaces.Proxyable"/> means the
    /// list now grows automatically as new surfaces are added.
    /// </summary>
    [Fact]
    public void InvestigationProxyToolAllowlist_Includes_Every_PodLocal_Surface_Tool()
    {
        InvestigationProxyToolAllowlist.AllowedToolNames.Should().Contain("collect_events");
        InvestigationProxyToolAllowlist.AllowedToolNames.Should().Contain("inspect_heap");
        InvestigationProxyToolAllowlist.AllowedToolNames.Should().Contain("inspect_process");
        InvestigationProxyToolAllowlist.AllowedToolNames.Should().Contain("get_bytes");
        InvestigationProxyToolAllowlist.AllowedToolNames.Should().Contain("query_snapshot");
    }

    /// <summary>
    /// The AOT-friendly <c>WithTools&lt;T&gt;()</c> chain in
    /// <c>DiagnosticServiceRegistration.AddDiagnosticServer</c> is intentionally still
    /// hand-rolled (it requires a compile-time generic argument so the source generator can
    /// emit the dispatch glue). It is therefore the one site that can silently drift from
    /// <see cref="PodLocalToolSurfaces"/>. This test parses the source file and asserts the
    /// chain mentions every type in <see cref="PodLocalToolSurfaces.Always"/> and every type
    /// in <see cref="PodLocalToolSurfaces.OrchestratorOnly"/> — without it, a future Wave 3
    /// PR could add a surface to the helper, see the scope/deprecation/proxy registries pick
    /// it up, and never notice the SDK is not dispatching it.
    /// </summary>
    [Fact]
    public void WithTools_Chain_In_DiagnosticServiceRegistration_Matches_PodLocalToolSurfaces()
    {
        var registrationSource = ReadDiagnosticServiceRegistrationSource();

        var withToolsTypeNames = Regex
            .Matches(registrationSource, @"\.WithTools<\s*([A-Za-z_][A-Za-z0-9_]*)\s*>\(\s*\)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var surface in PodLocalToolSurfaces.Always.Concat(PodLocalToolSurfaces.OrchestratorOnly))
        {
            withToolsTypeNames.Should().Contain(
                surface.Name,
                $"DiagnosticServiceRegistration.AddDiagnosticServer must call .WithTools<{surface.Name}>() for every type listed in PodLocalToolSurfaces");
        }
    }

    private static string ReadDiagnosticServiceRegistrationSource()
    {
        // [CallerFilePath] is unusable in deterministic CI builds (paths collapse to "/_/...").
        // Walk up from the test assembly location until we find the repo root (marked by
        // DotnetDiagnosticsMcp.slnx), then read the source file from src/.
        var dir = Path.GetDirectoryName(typeof(PodLocalToolSurfacesTests).Assembly.Location);
        while (dir is not null && !File.Exists(Path.Combine(dir, "DotnetDiagnosticsMcp.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        if (dir is null)
        {
            throw new FileNotFoundException(
                "Could not locate repo root (DotnetDiagnosticsMcp.slnx) by walking up from " +
                typeof(PodLocalToolSurfacesTests).Assembly.Location);
        }
        var path = Path.Combine(dir, "src", "DotnetDiagnosticsMcp.Server", "Hosting", "DiagnosticServiceRegistration.cs");
        return File.ReadAllText(path);
    }
}
