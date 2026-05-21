using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

public sealed class HeapSnapshotQueryToolTests
{
    [Fact]
    public async Task QueryHeapSnapshot_ObjectView_ParsesHexAddress_AndReturnsObjectPayload()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(123, "heap-snapshot", Snapshot(), TimeSpan.FromMinutes(10));
        var inspector = new StubDumpInspector
        {
            ObjectInspection = new HeapObjectInspection(0x1234, "System.Byte[]", 128, "Large", "Generation2")
            {
                IsArray = true,
                ArrayLength = 4,
                ArraySample = [new HeapArrayElement(0, "System.Byte", "0")],
            },
        };

        var result = await DiagnosticTools.QueryHeapSnapshot(store, inspector, handle.Id, view: "object", address: "0x1234");

        result.Error.Should().BeNull();
        result.Data.Should().NotBeNull();
        result.Data!.Address.Should().Be(0x1234);
        result.Data.ObjectDetails!.TypeFullName.Should().Be("System.Byte[]");
        result.Data.ObjectDetails.ArrayLength.Should().Be(4);
        inspector.LastAddress.Should().Be(0x1234UL);
    }

    [Fact]
    public async Task QueryHeapSnapshot_GcRootView_ParsesDecimalAddress_AndReturnsChain()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(123, "heap-snapshot", Snapshot(), TimeSpan.FromMinutes(10));
        var inspector = new StubDumpInspector
        {
            GcRootInspection = new HeapGcRootInspection(4660, "System.Byte[]", [
                new RetentionFrame("<root>", 0) { RootKind = "StaticVar" },
                new RetentionFrame("System.Byte[]", 4660),
            ], Truncated: false),
        };

        var result = await DiagnosticTools.QueryHeapSnapshot(store, inspector, handle.Id, view: "gcroot", address: "4660");

        result.Error.Should().BeNull();
        result.Data.Should().NotBeNull();
        result.Data!.Address.Should().Be(4660);
        result.Data.GcRoot!.Chain.Should().HaveCount(2);
        result.Data.GcRoot.Chain[0].RootKind.Should().Be("StaticVar");
        inspector.LastAddress.Should().Be(4660UL);
    }

    [Fact]
    public async Task QueryHeapSnapshot_ObjectSizeView_UsesInspectorResult()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(123, "heap-snapshot", Snapshot(), TimeSpan.FromMinutes(10));
        var inspector = new StubDumpInspector
        {
            ObjectSizeInspection = new HeapObjectSizeInspection(0x1234, "System.Byte[]", 4096, 3, Truncated: false),
        };

        var result = await DiagnosticTools.QueryHeapSnapshot(store, inspector, handle.Id, view: "objsize", address: "0x1234");

        result.Error.Should().BeNull();
        result.Data.Should().NotBeNull();
        result.Data!.ObjectSize!.RetainedBytes.Should().Be(4096);
        result.Data.ObjectSize.ObjectCount.Should().Be(3);
        inspector.LastAddress.Should().Be(0x1234UL);
    }

    private static HeapSnapshotArtifact Snapshot() => new(
        Origin: HeapSnapshotOrigin.Live,
        ProcessId: 123,
        CapturedAt: DateTimeOffset.UtcNow,
        WalkDuration: TimeSpan.FromMilliseconds(50),
        Runtime: new DumpRuntimeInfo("CoreCLR", "10.0.0", "X64", IsServerGC: false, HeapCount: 1),
        Heap: new DumpHeapSummary(1024, 0, 0, 1024, 0, 0, 1024),
        TopTypesByBytes: [],
        TopTypesByInstances: []);

    private sealed class StubDumpInspector : IDumpInspector
    {
        public ulong? LastAddress { get; private set; }
        public HeapObjectInspection? ObjectInspection { get; init; }
        public HeapGcRootInspection? GcRootInspection { get; init; }
        public HeapObjectSizeInspection? ObjectSizeInspection { get; init; }

        public Task<HeapSnapshotArtifact> InspectAsync(string dumpFilePath, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapSnapshotArtifact> InspectLiveAsync(int processId, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapObjectInspection> InspectObjectAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default)
        {
            LastAddress = address;
            return Task.FromResult(ObjectInspection ?? throw new InvalidOperationException("ObjectInspection not configured."));
        }

        public Task<HeapGcRootInspection> InspectGcRootAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default)
        {
            LastAddress = address;
            return Task.FromResult(GcRootInspection ?? throw new InvalidOperationException("GcRootInspection not configured."));
        }

        public Task<HeapObjectSizeInspection> InspectObjectSizeAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default)
        {
            LastAddress = address;
            return Task.FromResult(ObjectSizeInspection ?? throw new InvalidOperationException("ObjectSizeInspection not configured."));
        }
    }
}
