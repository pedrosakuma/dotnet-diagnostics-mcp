using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Threads;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

public sealed class ThreadSnapshotToolTests
{
    [Fact]
    public void QueryThreadSnapshot_ThreadpoolView_ReturnsCapturedThreadPool()
    {
        var handles = new MemoryDiagnosticHandleStore();
        var snapshot = new ThreadSnapshotArtifact(
            Origin: ThreadSnapshotOrigin.Live,
            ProcessId: 42,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(12),
            RuntimeName: "CoreClr",
            RuntimeVersion: "10.0.0",
            Threads: Array.Empty<ManagedThread>(),
            Locks: Array.Empty<MonitorLockState>())
        {
            Source = "clrmd-thread-walk",
            ThreadPool = new ThreadPoolSnapshot(
                Initialized: true,
                UsingPortableThreadPool: true,
                UsingWindowsThreadPool: false,
                Workers: new ThreadPoolWorkerState(Current: 7, Active: 3, Idle: 4, Retired: 0, Min: 1, Max: 32767),
                Iocp: new ThreadPoolIocpState(Current: 0, Idle: 0, Min: 1, Max: 1000),
                Queues: new ThreadPoolQueueState(
                    GlobalQueueLength: 5,
                    GlobalQueues: new[]
                    {
                        new ThreadPoolNamedQueueLength("workItems", 5) { QueueAddress = 0x1000 },
                    },
                    LocalQueues: new[]
                    {
                        new ThreadPoolLocalQueueLength(0x2000, 2) { ManagedThreadId = 11, OSThreadId = 22, QueueIndex = 0 },
                    }),
                PendingWorkItems: 7)
            {
                CpuUtilization = 42,
                HillClimbing = new ThreadPoolHillClimbingState(123, 4, 1, 12.5f, "Warmup") { AdjustmentIntervalMs = 10 },
            },
        };
        var handle = handles.Register(snapshot.ProcessId, "thread-snapshot", snapshot, TimeSpan.FromMinutes(10));

        var result = DiagnosticTools.QueryThreadSnapshot(handles, handle.Id, view: "threadpool");

        result.IsError.Should().BeFalse();
        result.Data.Should().NotBeNull();
        result.Data!.View.Should().Be("threadpool");
        result.Data.ThreadPool.Should().NotBeNull();
        result.Data.ThreadPool!.PendingWorkItems.Should().Be(7);
        result.Data.ThreadPool.Queues.LocalQueues.Should().ContainSingle();
        result.Summary.Should().Contain("pending work items 7");
    }
}
