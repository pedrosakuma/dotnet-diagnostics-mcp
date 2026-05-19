using System.Text.Json;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Memory;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnosticsMcp.Core.Tests;

public class InvestigationMemoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 18, 22, 0, 0, TimeSpan.Zero);

    private static InvestigationSummaryExporter NewExporter(IProvenanceCollector? prov = null, int seed = 1)
    {
        var counter = seed;
        return new InvestigationSummaryExporter(
            prov ?? new FixedProvenance(),
            clock: new FixedClock(T0),
            idFactory: () => $"inv-test-{counter++}");
    }

    private static CpuSampleTraceArtifact ArtifactFor(params (string module, string method, long incl, long excl)[] frames)
        => ArtifactFor(totalSamples: 1000, frames);

    private static CpuSampleTraceArtifact ArtifactFor(long totalSamples, params (string module, string method, long incl, long excl)[] frames)
    {
        var children = frames.Select(f =>
            new CallTreeNode(new SampledFrame(f.module, f.method), f.incl, f.excl, Array.Empty<CallTreeNode>())).ToArray();
        // Match the synthetic root sentinel produced by EventPipeCpuSampler.CallTreeBuilder.
        var root = new CallTreeNode(new SampledFrame(string.Empty, "<root>"), totalSamples, 0, children);
        return new CpuSampleTraceArtifact(1234, T0, TimeSpan.FromSeconds(10), totalSamples, root);
    }

    [Fact]
    public void Export_ProducesV1Schema_AndStableSymbolRefs()
    {
        var artifact = ArtifactFor(("MyApp.dll", "MyApp.HotPath.DoWork", 100, 80), ("MyApp.dll", "MyApp.Cold", 10, 5));
        var exporter = NewExporter();

        var exported = exporter.Export(new ExportRequest("h-1", artifact, TopHotspots: 5, BuildAssemblyName: "MyApp"));

        exported.Summary.Schema.Should().Be(InvestigationSummary.SchemaV1);
        exported.Summary.InvestigationId.Should().Be("inv-test-1");
        exported.Summary.CreatedAt.Should().Be(T0);
        exported.Summary.Provenance.Build!.AssemblyName.Should().Be("MyApp");
        exported.Summary.Findings.TopHotspots.Should().HaveCount(2);
        exported.Summary.Findings.TopHotspots[0].Symbol.Should().Be(new SymbolRef("MyApp.dll", "MyApp.HotPath.DoWork"));
        exported.Summary.Findings.TopHotspots[0].InclusivePercent.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Export_JsonRoundtripsIntoSameSummary()
    {
        var artifact = ArtifactFor(("M.dll", "M.A", 10, 10));
        var exporter = NewExporter();

        var exported = exporter.Export(new ExportRequest("h-1", artifact, Format: SummaryFormat.Json));
        var back = JsonSerializer.Deserialize<InvestigationSummary>(exported.Rendered);

        back.Should().NotBeNull();
        back!.InvestigationId.Should().Be(exported.Summary.InvestigationId);
        back.Findings.TopHotspots[0].Symbol.MethodFullName.Should().Be("M.A");
    }

    [Fact]
    public void Export_MarkdownIncludesProvenanceAndHotspots()
    {
        var artifact = ArtifactFor(("App.dll", "App.Service.Process", 100, 50));
        var exporter = NewExporter(new FixedProvenance(
            container: new ContainerProvenance("ghcr.io/me/app:v2", "prod", "app-7c-xy", "node-1")));

        var exported = exporter.Export(new ExportRequest("h-1", artifact, Format: SummaryFormat.Markdown,
            TargetsFix: new InvestigationFixTarget(CommitSha: "abc123", PullRequestUrl: "https://github.com/x/y/pull/42")));

        exported.Rendered.Should().Contain("# Investigation `inv-test-1`")
            .And.Contain("App.Service.Process")
            .And.Contain("ghcr.io/me/app:v2")
            .And.Contain("https://github.com/x/y/pull/42");
    }

    [Fact]
    public void Compare_NoChange_ReturnsNoRegressionVerdict()
    {
        var artifact = ArtifactFor(("M.dll", "M.A", 100, 80));
        var exporter = NewExporter();
        var s1 = exporter.Export(new ExportRequest("h", artifact)).Summary;
        var s2 = exporter.Export(new ExportRequest("h", artifact)).Summary;

        var diff = new SummaryComparer().Compare(s1, s2);

        diff.Verdict.Should().Be("no_regression");
        diff.NewHotspots.Should().BeEmpty();
        diff.RemovedHotspots.Should().BeEmpty();
        diff.ChangedHotspots.Should().BeEmpty();
    }

    [Fact]
    public void Compare_NewHotspot_FlagsRegression()
    {
        var exporter = NewExporter();
        var baseline = exporter.Export(new ExportRequest("h", ArtifactFor(("M.dll", "M.A", 100, 80)))).Summary;
        var current = exporter.Export(new ExportRequest("h", ArtifactFor(
            ("M.dll", "M.A", 100, 80),
            ("M.dll", "M.NewlyHot", 60, 60)))).Summary;

        var diff = new SummaryComparer().Compare(baseline, current);

        diff.Verdict.Should().Be("regression_new_hotspot");
        diff.NewHotspots.Should().ContainSingle()
            .Which.Symbol.MethodFullName.Should().Be("M.NewlyHot");
    }

    [Fact]
    public void Compare_RemovedHotspotOnly_IsImprovement()
    {
        var exporter = NewExporter();
        var baseline = exporter.Export(new ExportRequest("h", ArtifactFor(
            ("M.dll", "M.A", 100, 80),
            ("M.dll", "M.GoneSoon", 60, 60)))).Summary;
        var current = exporter.Export(new ExportRequest("h", ArtifactFor(("M.dll", "M.A", 100, 80)))).Summary;

        var diff = new SummaryComparer().Compare(baseline, current);

        diff.Verdict.Should().Be("improvement");
        diff.RemovedHotspots.Should().ContainSingle()
            .Which.Symbol.MethodFullName.Should().Be("M.GoneSoon");
    }

    [Fact]
    public void Compare_DetectsImageJumpInProvenance()
    {
        var artifact = ArtifactFor(("M.dll", "M.A", 100, 80));
        var oldProv = new FixedProvenance(container: new ContainerProvenance("ghcr.io/me/app:v1", "prod", "p1", "n1"));
        var newProv = new FixedProvenance(container: new ContainerProvenance("ghcr.io/me/app:v2", "prod", "p2", "n1"));
        var baseline = NewExporter(oldProv, seed: 1).Export(new ExportRequest("h", artifact)).Summary;
        var current = NewExporter(newProv, seed: 2).Export(new ExportRequest("h", artifact)).Summary;

        var diff = new SummaryComparer().Compare(baseline, current);

        diff.Verdict.Should().Be("no_regression_after_deploy");
        diff.Provenance.ImageChanged.Should().BeTrue();
        diff.Provenance.Summary.Should().Contain("v1").And.Contain("v2");
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FixedProvenance : IProvenanceCollector
    {
        private readonly ContainerProvenance? _container;
        public FixedProvenance(ContainerProvenance? container = null) { _container = container; }

        public InvestigationProvenance Collect(int processId, string? buildAssemblyName = null)
            => new(Hostname: "test-host")
            {
                Build = buildAssemblyName is null ? null : new BuildProvenance(buildAssemblyName, null, null, null, null),
                Container = _container,
            };
    }
}
