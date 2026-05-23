using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.Security;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

public sealed class HeapSnapshotSecurityGateTests
{
    [Fact]
    public async Task QueryDuplicateStrings_Default_RedactsToMetadataOnly()
    {
        var store = new MemoryDiagnosticHandleStore();
        var artifact = SnapshotWithStrings(new DuplicateStringStat("Authorization: Bearer eyJabcdefghij.payload.signature", 50, 10, 10_240, false));
        var handle = store.Register(123, "heap-snapshot", artifact, TimeSpan.FromMinutes(10));
        var inspector = NoopInspector();

        var result = await DiagnosticTools.QueryHeapSnapshot(
            store, inspector, new SensitiveDataRedactor(null), new SensitiveValueGate(null),
            handle.Id, view: "duplicate-strings", topN: 10);

        result.Error.Should().BeNull();
        result.Data!.DuplicateStrings.Should().HaveCount(1);
        result.Data.DuplicateStrings![0].Preview.Should().Be(SensitiveDataRedactor.MetadataOnlyPlaceholder);
    }

    [Fact]
    public async Task QueryDuplicateStrings_OptedIn_AppliesRedactorButReturnsBenignContent()
    {
        var store = new MemoryDiagnosticHandleStore();
        var artifact = SnapshotWithStrings(
            new DuplicateStringStat("Authorization: Bearer eyJabcdefghij.payload.signature", 50, 10, 10_240, false),
            new DuplicateStringStat("hello, world", 12, 5, 256, false));
        var handle = store.Register(123, "heap-snapshot", artifact, TimeSpan.FromMinutes(10));
        var inspector = NoopInspector();
        var options = new SecurityOptions { AllowSensitiveHeapValues = true };

        var result = await DiagnosticTools.QueryHeapSnapshot(
            store, inspector, new SensitiveDataRedactor(options), new SensitiveValueGate(options),
            handle.Id, view: "duplicate-strings", topN: 10, includeSensitiveValues: true);

        result.Error.Should().BeNull();
        result.Data!.DuplicateStrings.Should().HaveCount(2);
        result.Data.DuplicateStrings![0].Preview.Should().Contain(SensitiveDataRedactor.RedactedPlaceholder);
        result.Data.DuplicateStrings[0].Preview.Should().NotContain("eyJabcdefghij");
        result.Data.DuplicateStrings[1].Preview.Should().Be("hello, world");
    }

    private static HeapSnapshotArtifact SnapshotWithStrings(params DuplicateStringStat[] strings) => new HeapSnapshotArtifact(
        Origin: HeapSnapshotOrigin.Live,
        ProcessId: 123,
        CapturedAt: DateTimeOffset.UtcNow,
        WalkDuration: TimeSpan.FromMilliseconds(50),
        Runtime: new DumpRuntimeInfo("CoreCLR", "10.0.0", "X64", IsServerGC: false, HeapCount: 1),
        Heap: new DumpHeapSummary(1024, 0, 0, 1024, 0, 0, 1024),
        TopTypesByBytes: [],
        TopTypesByInstances: [])
    {
        DuplicateStrings = strings,
    };

    private static IDumpInspector NoopInspector() => new StubInspector();

    private sealed class StubInspector : IDumpInspector
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
