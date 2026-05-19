using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Memory;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnosticsMcp.Core.Tests;

/// <summary>
/// Wiring tests for the handoff contract (issue #18 / dotnet-assembly-mcp).
/// Each hotspot returned by collect_cpu_sample must carry an optional
/// <see cref="MethodIdentity"/> so the LLM can pass it verbatim to the
/// assembly-inspector MCP (<c>get_method</c>, <c>decompile_method</c>) without
/// any string parsing on either side.
/// </summary>
public class MethodIdentityHandoffTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 19, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MethodIdentity_HasCanonicalShape()
    {
        var id = new MethodIdentity(
            ModuleName: "MyApp.dll",
            ModulePath: "/app/MyApp.dll",
            ModuleVersionId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            MetadataToken: 0x06000028,
            TypeFullName: "MyApp.Services.OrderService",
            MethodName: "Process",
            GenericArity: 0);

        // The two fields the companion MCP actually consumes:
        id.ModuleVersionId.Should().NotBeNull();
        id.MetadataToken.Should().Be(0x06000028);
    }

    [Fact]
    public void Exporter_PropagatesIdentity_FromArtifactToHotspotSummary()
    {
        var hot = new SampledFrame("MyApp.dll", "MyApp.Services.OrderService.Process");
        var children = new[]
        {
            new CallTreeNode(hot, 100, 80, Array.Empty<CallTreeNode>()),
        };
        var root = new CallTreeNode(new SampledFrame(string.Empty, "<root>"), 1000, 0, children);

        var identity = new MethodIdentity(
            ModuleName: "MyApp.dll",
            ModulePath: "/app/MyApp.dll",
            ModuleVersionId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            MetadataToken: 0x06000099,
            TypeFullName: "MyApp.Services.OrderService",
            MethodName: "Process",
            GenericArity: 0);

        var ids = new Dictionary<SymbolRef, MethodIdentity>
        {
            [new SymbolRef("MyApp.dll", "MyApp.Services.OrderService.Process")] = identity,
        };

        var artifact = new CpuSampleTraceArtifact(
            1234, T0, TimeSpan.FromSeconds(5), 1000, root,
            ResolvedSources: null,
            MethodIdentities: ids);

        var exporter = new InvestigationSummaryExporter(
            new FixedProv(),
            clock: new FixedClk(T0),
            idFactory: () => "inv-id-1");

        var exported = exporter.Export(new ExportRequest("h-1", artifact));

        var hotspot = exported.Summary.Findings.TopHotspots.Single();
        hotspot.Identity.Should().NotBeNull();
        hotspot.Identity!.ModuleVersionId.Should().Be(identity.ModuleVersionId);
        hotspot.Identity.MetadataToken.Should().Be(0x06000099);
        hotspot.Identity.MethodName.Should().Be("Process");
    }

    [Fact]
    public void Artifact_DefaultsMethodIdentities_ToEmpty()
    {
        var root = new CallTreeNode(new SampledFrame(string.Empty, "<root>"), 0, 0, Array.Empty<CallTreeNode>());
        var artifact = new CpuSampleTraceArtifact(1, T0, TimeSpan.FromSeconds(1), 0, root);
        artifact.MethodIdentities.Should().NotBeNull();
        artifact.MethodIdentities.Should().BeEmpty();
    }

    private sealed class FixedClk : TimeProvider
    {
        private readonly DateTimeOffset _n;
        public FixedClk(DateTimeOffset n) { _n = n; }
        public override DateTimeOffset GetUtcNow() => _n;
    }

    private sealed class FixedProv : IProvenanceCollector
    {
        public InvestigationProvenance Collect(int processId, string? buildAssemblyName = null)
            => new(Hostname: "test") { Build = null, Container = null };
    }
}
