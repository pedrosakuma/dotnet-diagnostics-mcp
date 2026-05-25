using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.Memory;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class SampleDifferTests
{
    [Fact]
    public void HeapDiff_UpwardDeltaAtThreshold_FlagsRegression()
    {
        var baseline = HeapSnapshot(("System.Byte[]", 100, 1));
        var current = HeapSnapshot(("System.Byte[]", 105, 1));

        var diff = SampleDiffer.Compare(baseline, "b", current, "c", minDeltaPct: 5, topN: 10);

        diff.Verdict.Should().Be("regression");
        diff.Changed.Should().ContainSingle();
        diff.Changed[0].Direction.Should().Be("up");
        diff.Changed[0].DeltaPct.Should().Be(5);
    }

    [Fact]
    public void HeapDiff_DownwardDeltaAtThreshold_FlagsImprovement()
    {
        var baseline = HeapSnapshot(("System.Byte[]", 100, 1));
        var current = HeapSnapshot(("System.Byte[]", 95, 1));

        var diff = SampleDiffer.Compare(baseline, "b", current, "c", minDeltaPct: 5, topN: 10);

        diff.Verdict.Should().Be("improvement");
        diff.Changed.Should().ContainSingle();
        diff.Changed[0].Direction.Should().Be("down");
        diff.Changed[0].DeltaPct.Should().Be(-5);
    }

    [Fact]
    public void HeapDiff_RegressionAndImprovementSignals_FlagsMixed()
    {
        var baseline = HeapSnapshot(
            ("System.Byte[]", 100, 1),
            ("System.String", 100, 1));
        var current = HeapSnapshot(
            ("System.Byte[]", 130, 1),
            ("System.String", 70, 1));

        var diff = SampleDiffer.Compare(baseline, "b", current, "c", minDeltaPct: 5, topN: 10);

        diff.Verdict.Should().Be("mixed");
        diff.Changed.Should().HaveCount(2);
    }

    [Fact]
    public void HeapDiff_NoOverlap_ForcesNoChange()
    {
        var baseline = HeapSnapshot(("System.Byte[]", 100, 1));
        var current = HeapSnapshot(("System.String", 500, 3));

        var diff = SampleDiffer.Compare(baseline, "b", current, "c", minDeltaPct: 5, topN: 10);

        diff.Verdict.Should().Be("no_change");
        diff.Notes.Should().Contain(note => note.Contains("No overlapping symbols/types", StringComparison.Ordinal));
    }

    [Fact]
    public void CpuDiff_UsesMethodIdentityForOverlapEvenWhenDisplayDiffers()
    {
        var identity = new DotnetDiagnosticsMcp.Core.Memory.MethodIdentity(
            MethodName: "DoWork",
            GenericArity: 0,
            ModuleName: "MyApp.dll",
            ModuleVersionId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            MetadataToken: 0x06000042,
            TypeFullName: "MyApp.Worker");

        var baseline = CpuArtifact(new SymbolRef("MyApp.dll", "MyApp.Worker.DoWork"), identity, exclusive: 10);
        var current = CpuArtifact(new SymbolRef("DifferentDisplay.dll", "Completely.Different.Name"), identity, exclusive: 20);

        var diff = SampleDiffer.Compare(baseline, "b", current, "c", minDeltaPct: 5, topN: 10);

        diff.Verdict.Should().Be("regression");
        diff.Notes.Should().BeNull();
        diff.Changed.Should().ContainSingle();
    }

    private static CpuSampleTraceArtifact CpuArtifact(SymbolRef symbol, MethodIdentity identity, long exclusive)
        => new(
            123,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(5),
            100,
            new CallTreeNode(
                new SampledFrame(string.Empty, "<root>"),
                100,
                0,
                [new CallTreeNode(new SampledFrame(symbol.Module, symbol.MethodFullName), exclusive, exclusive, Array.Empty<CallTreeNode>())]),
            MethodIdentities: new Dictionary<SymbolRef, MethodIdentity>
            {
                [symbol] = identity,
            });

    private static HeapSnapshotArtifact HeapSnapshot(params (string typeName, long bytes, long instances)[] rows)
    {
        var stats = rows.Select(row =>
            new TypeStat(
                TypeFullName: row.typeName,
                ModuleName: null,
                InstanceCount: row.instances,
                TotalBytes: row.bytes,
                TotalBytesPercent: 0,
                Identity: new TypeIdentity(row.typeName))).ToArray();

        return new HeapSnapshotArtifact(
            Origin: HeapSnapshotOrigin.Live,
            ProcessId: 123,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(10),
            Runtime: new DumpRuntimeInfo("CoreCLR", "10.0.0", "x64", IsServerGC: false, HeapCount: 1),
            Heap: new DumpHeapSummary(1024, 0, 0, 1024, 0, 0, 1024),
            TopTypesByBytes: stats,
            TopTypesByInstances: stats);
    }
}
