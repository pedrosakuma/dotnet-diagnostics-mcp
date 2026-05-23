using System.Threading;
using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Security;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

public sealed class HeapSnapshotQueryTests
{
    [Fact]
    public async Task QueryHeapSnapshot_AsyncView_ReturnsPendingOperations()
    {
        var store = new MemoryDiagnosticHandleStore();
        var snapshot = new HeapSnapshotArtifact(
            Origin: HeapSnapshotOrigin.Live,
            ProcessId: 1234,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(150),
            Runtime: new DumpRuntimeInfo("CoreCLR", "10.0.0", "x64", IsServerGC: false, HeapCount: 1),
            Heap: new DumpHeapSummary(1024, 128, 128, 512, 0, 0, 1024),
            TopTypesByBytes: Array.Empty<TypeStat>(),
            TopTypesByInstances: Array.Empty<TypeStat>())
        {
            AsyncOperations =
            [
                new AsyncOperationStat("MyApp.AsyncFixture+<LeafAsync>d__3", 0, "System.Runtime.CompilerServices.TaskAwaiter", 192)
                {
                    StateMachineAddress = 0x1000,
                    TaskAddress = 0x2000,
                    TaskId = 77,
                    TaskTypeFullName = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[[MyApp.AsyncFixture+<LeafAsync>d__3]]",
                    ContinuationObjectTypeFullName = "System.Threading.Tasks.Task+ContinuationResultTaskFromTask`1",
                    ObservedOrder = 4,
                    Stack =
                    [
                        new AsyncChainFrame("MyApp.AsyncFixture+<LeafAsync>d__3", 0, "System.Runtime.CompilerServices.TaskAwaiter", 0x1000)
                        {
                            TaskAddress = 0x2000,
                            TaskId = 77,
                            ContinuationObjectTypeFullName = "System.Threading.Tasks.Task+ContinuationResultTaskFromTask`1",
                        },
                        new AsyncChainFrame("MyApp.AsyncFixture+<OuterAsync>d__1", 0, "System.Runtime.CompilerServices.TaskAwaiter", 0x3000)
                        {
                            TaskAddress = 0x4000,
                            TaskId = 78,
                        },
                    ],
                },
            ],
        };

        var handle = store.Register(snapshot.ProcessId, "heap-snapshot", snapshot, TimeSpan.FromMinutes(10));

        var result = await DiagnosticTools.QueryHeapSnapshot(store, new StubDumpInspector(), new SensitiveDataRedactor(null), new SensitiveValueGate(null), handle.Id, view: "async", topN: 10);

        result.IsError.Should().BeFalse();
        result.Data.Should().NotBeNull();
        result.Data!.View.Should().Be("async");
        result.Data.AsyncOperations.Should().ContainSingle();
        result.Data.SortedBy.Should().Be("heap-order");
        result.Summary.Should().Contain("First pending state machine in heap-walk order");
        result.Data.AsyncOperations![0].Stack.Should().HaveCount(2);
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
