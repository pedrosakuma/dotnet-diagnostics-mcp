using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Memory;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnosticsMcp.Core.Tests;

/// <summary>
/// Verifies that source-level resolution (file:line, optional SourceLink) flows
/// from <see cref="CpuSampleTraceArtifact.ResolvedSources"/> into both the in-memory
/// <see cref="HotspotSummary.Source"/> and the markdown render. The sampler itself
/// (PDB / SymbolReader pipeline) is covered by integration tests that need a live
/// process; the unit tests here only assert wiring.
/// </summary>
public class SourceResolutionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 19, 0, 0, 0, TimeSpan.Zero);

    private static CpuSampleTraceArtifact ArtifactWithSource()
    {
        var hot = new SampledFrame("MyApp.dll", "MyApp.HotPath.DoWork");
        var cold = new SampledFrame("MyApp.dll", "MyApp.Cold.Sub");
        var children = new[]
        {
            new CallTreeNode(hot, 100, 80, Array.Empty<CallTreeNode>()),
            new CallTreeNode(cold, 10, 5, Array.Empty<CallTreeNode>()),
        };
        var root = new CallTreeNode(new SampledFrame(string.Empty, "<root>"), 1000, 0, children);
        var sources = new Dictionary<SymbolRef, SourceLocation>
        {
            [new SymbolRef("MyApp.dll", "MyApp.HotPath.DoWork")]
                = new SourceLocation("src/HotPath.cs", 42, "https://github.com/me/repo/blob/abc123/src/HotPath.cs#L42"),
        };
        return new CpuSampleTraceArtifact(1234, T0, TimeSpan.FromSeconds(10), 1000, root, sources);
    }

    [Fact]
    public void Export_PopulatesSourceLocation_OnMatchingHotspot()
    {
        var artifact = ArtifactWithSource();
        var exporter = new InvestigationSummaryExporter(
            new FixedProv(),
            clock: new FixedClk(T0),
            idFactory: () => "inv-src-1");

        var exported = exporter.Export(new ExportRequest("h-1", artifact, TopHotspots: 5));

        var hot = exported.Summary.Findings.TopHotspots.Single(h => h.Symbol.MethodFullName == "MyApp.HotPath.DoWork");
        hot.Source.Should().NotBeNull();
        hot.Source!.File.Should().Be("src/HotPath.cs");
        hot.Source.StartLine.Should().Be(42);
        hot.Source.SourceLink.Should().StartWith("https://");

        var cold = exported.Summary.Findings.TopHotspots.Single(h => h.Symbol.MethodFullName == "MyApp.Cold.Sub");
        cold.Source.Should().BeNull();
    }

    [Fact]
    public void Export_Markdown_IncludesFileAndLineWhenResolved()
    {
        var artifact = ArtifactWithSource();
        var exporter = new InvestigationSummaryExporter(
            new FixedProv(),
            clock: new FixedClk(T0),
            idFactory: () => "inv-src-2");

        var exported = exporter.Export(new ExportRequest("h-1", artifact, Format: SummaryFormat.Markdown));

        exported.Rendered.Should().Contain("src/HotPath.cs:42");
    }

    [Fact]
    public void Artifact_DefaultsResolvedSourcesToEmpty_WhenNotProvided()
    {
        var root = new CallTreeNode(new SampledFrame(string.Empty, "<root>"), 0, 0, Array.Empty<CallTreeNode>());
        var artifact = new CpuSampleTraceArtifact(1, T0, TimeSpan.FromSeconds(1), 0, root);
        artifact.ResolvedSources.Should().NotBeNull();
        artifact.ResolvedSources.Should().BeEmpty();
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
