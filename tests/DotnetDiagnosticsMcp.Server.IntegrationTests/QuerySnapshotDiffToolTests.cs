using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.Memory;
using DotnetDiagnosticsMcp.Core.Security;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

public sealed class QuerySnapshotDiffToolTests
{
    [Fact]
    public async Task Diff_RejectsMixedKinds()
    {
        var store = new MemoryDiagnosticHandleStore();
        var cpuHandle = store.Register(123, "cpu-sample", CpuArtifact(1), TimeSpan.FromMinutes(10));
        var heapHandle = store.Register(123, "heap-snapshot", HeapSnapshot(("System.Byte[]", 128, 1)), TimeSpan.FromMinutes(10));

        var result = await QuerySnapshot(store, cpuHandle.Id, heapHandle.Id);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
        result.Error.Message.Should().Contain("Accepted pairs");
        result.Error.Message.Should().Contain("cpu-sample");
        result.Error.Message.Should().Contain("heap-snapshot");
    }

    [Fact]
    public async Task Diff_CpuHandle_ReturnsSampleDiffEnvelope()
    {
        var store = new MemoryDiagnosticHandleStore();
        var baselineHandle = store.Register(123, "cpu-sample", CpuArtifact(2), TimeSpan.FromMinutes(10));
        var currentHandle = store.Register(123, "cpu-sample", CpuArtifact(6), TimeSpan.FromMinutes(10));

        var result = await QuerySnapshot(store, currentHandle.Id, baselineHandle.Id);

        result.Error.Should().BeNull();
        var diff = result.Data.Should().BeOfType<SampleDiff<MethodDiffKey, CpuDiffMetric>>().Subject;
        diff.Kind.Should().Be("cpu-sample");
        diff.Verdict.Should().Be("regression");
        diff.Changed.Should().ContainSingle(row => row.Direction == "up" && row.Key.Symbol.MethodFullName.Contains("DoWork", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Diff_HeapHandle_ReturnsSampleDiffEnvelope()
    {
        var store = new MemoryDiagnosticHandleStore();
        var baselineHandle = store.Register(123, "heap-snapshot", HeapSnapshot(("System.Byte[]", 128, 1)), TimeSpan.FromMinutes(10));
        var currentHandle = store.Register(123, "heap-snapshot", HeapSnapshot(("System.Byte[]", 512, 4)), TimeSpan.FromMinutes(10));

        var result = await QuerySnapshot(store, currentHandle.Id, baselineHandle.Id);

        result.Error.Should().BeNull();
        var diff = result.Data.Should().BeOfType<SampleDiff<TypeIdentity, HeapDiffMetric>>().Subject;
        diff.Kind.Should().Be("heap-snapshot");
        diff.Verdict.Should().Be("regression");
        diff.Changed.Should().ContainSingle(row => row.Key.TypeFullName == "System.Byte[]");
    }

    [Fact]
    public async Task Diff_AllocationHandle_ReturnsSampleDiffEnvelope()
    {
        var store = new MemoryDiagnosticHandleStore();
        var baselineHandle = store.Register(123, "allocation-sample", AllocationArtifact(2_000, 20, 4), TimeSpan.FromMinutes(10));
        var currentHandle = store.Register(123, "allocation-sample", AllocationArtifact(8_000, 80, 4), TimeSpan.FromMinutes(10));

        var result = await QuerySnapshot(store, currentHandle.Id, baselineHandle.Id);

        result.Error.Should().BeNull();
        var diff = result.Data.Should().BeOfType<SampleDiff<TypeIdentity, AllocationDiffMetric>>().Subject;
        diff.Kind.Should().Be("allocation-sample");
        diff.Verdict.Should().Be("regression");
        diff.Changed.Should().ContainSingle(row => row.Key.TypeFullName == "System.String");
    }

    private static async Task<DotnetDiagnosticsMcp.Core.DiagnosticResult<object>> QuerySnapshot(MemoryDiagnosticHandleStore store, string currentHandle, string baselineHandle)
        => await QuerySnapshotTool.QuerySnapshot(
            store,
            new StubDumpInspector(),
            new SensitiveDataRedactor(null),
            new SensitiveValueGate(null),
            TestPrincipalAccessors.Root,
            handle: currentHandle,
            view: "diff",
            baselineHandle: baselineHandle,
            cancellationToken: CancellationToken.None);

    private static CpuSampleTraceArtifact CpuArtifact(long exclusiveSamples)
        => new(
            123,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(5),
            100,
            new CallTreeNode(
                new SampledFrame(string.Empty, "<root>"),
                100,
                0,
                [new CallTreeNode(new SampledFrame("MyApp.dll", "MyApp.Worker.DoWork"), exclusiveSamples, exclusiveSamples, Array.Empty<CallTreeNode>())]));

    private static HeapSnapshotArtifact HeapSnapshot(params (string typeName, long bytes, long instances)[] rows)
    {
        var stats = rows.Select(row =>
            new TypeStat(
                row.typeName,
                ModuleName: null,
                InstanceCount: row.instances,
                TotalBytes: row.bytes,
                TotalBytesPercent: 0,
                Identity: new TypeIdentity(row.typeName))).ToArray();

        return new(
            Origin: HeapSnapshotOrigin.Live,
            ProcessId: 123,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(10),
            Runtime: new DumpRuntimeInfo("CoreCLR", "10.0.0", "x64", IsServerGC: false, HeapCount: 1),
            Heap: new DumpHeapSummary(1024, 0, 0, 1024, 0, 0, 1024),
            TopTypesByBytes: stats,
            TopTypesByInstances: stats);
    }

    private static AllocationSampleArtifact AllocationArtifact(long totalBytes, long totalEvents, int seconds)
    {
        var summary = new AllocationSample(
            123,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(seconds),
            TotalEvents: totalEvents,
            TotalBytes: totalBytes,
            TopByBytes:
            [
                new AllocatedType("System.String", totalBytes, totalEvents, HeapKind.Small, new TypeIdentity("System.String")),
            ],
            TopByCount:
            [
                new AllocatedType("System.String", totalBytes, totalEvents, HeapKind.Small, new TypeIdentity("System.String")),
            ]);

        var trace = new CpuSampleTraceArtifact(
            123,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(seconds),
            totalEvents,
            new CallTreeNode(new SampledFrame(string.Empty, "<root>"), totalEvents, 0, Array.Empty<CallTreeNode>()));
        return new AllocationSampleArtifact(summary, trace);
    }

    private sealed class StubDumpInspector : IDumpInspector
    {
        public Task<HeapSnapshotArtifact> InspectAsync(string dumpFilePath, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapSnapshotArtifact> InspectLiveAsync(int processId, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapObjectInspection> InspectObjectAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapGcRootInspection> InspectGcRootAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapObjectSizeInspection> InspectObjectSizeAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
