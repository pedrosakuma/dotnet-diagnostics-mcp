using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Threads;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

public sealed class QueryThreadSnapshotDeadlockTests
{
    private static readonly string[] ExpectedSosCommands = [
        "!threads",
        "!syncblk",
        "~~[65]s; !clrstack",
        "~~[66]s; !clrstack",
    ];

    [Fact]
    public void QueryThreadSnapshot_DeadlocksView_FindsSyntheticTwoThreadCycle()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(4242, "thread-snapshot", BuildTwoThreadDeadlockSnapshot(), TimeSpan.FromMinutes(5));

        var result = DiagnosticTools.QueryThreadSnapshot(store, handle.Id, view: "deadlocks");

        result.IsError.Should().BeFalse();
        result.Data.Should().NotBeNull();
        var data = result.Data!;
        data.View.Should().Be("deadlocks");
        data.Deadlocks.Should().NotBeNull();
        var deadlocks = data.Deadlocks!;
        deadlocks.Should().HaveCount(1);

        var cycle = deadlocks[0];
        cycle.CycleMembers.Select(member => member.ThreadId).Should().Equal(1, 2);
        cycle.CycleMembers.Select(member => member.TopFrameMethod).Should().Equal(
            "System.Threading.Monitor.Enter(System.Object)",
            "System.Threading.Monitor.Enter(System.Object)");
        cycle.LockChain.Should().HaveCount(2);
        cycle.LockChain[0].WaitingThreadId.Should().Be(1);
        cycle.LockChain[0].OwnerThreadId.Should().Be(2);
        cycle.LockChain[0].LockObjectAddress.Should().Be(0x2000);
        cycle.LockChain[1].WaitingThreadId.Should().Be(2);
        cycle.LockChain[1].OwnerThreadId.Should().Be(1);
        cycle.LockChain[1].LockObjectAddress.Should().Be(0x1000);
        cycle.RecommendedCommands.Select(command => command.Command).Should().Contain(ExpectedSosCommands);
    }

    [Fact]
    public void QueryThreadSnapshot_DeadlocksView_WithoutCycle_ReturnsEmptyArray()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(4242, "thread-snapshot", BuildOneWayContentionSnapshot(), TimeSpan.FromMinutes(5));

        var result = DiagnosticTools.QueryThreadSnapshot(store, handle.Id, view: "deadlocks");

        result.IsError.Should().BeFalse();
        result.Data.Should().NotBeNull();
        var data = result.Data!;
        data.Deadlocks.Should().NotBeNull();
        data.Deadlocks.Should().BeEmpty();
        result.Summary.Should().Contain("No deadlock cycles detected");
    }

    private static ThreadSnapshotArtifact BuildTwoThreadDeadlockSnapshot()
        => new(
            Origin: ThreadSnapshotOrigin.Live,
            ProcessId: 4242,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(25),
            RuntimeName: "CoreCLR",
            RuntimeVersion: "10.0.0",
            Threads:
            [
                BuildThread(managedThreadId: 1, osThreadId: 101),
                BuildThread(managedThreadId: 2, osThreadId: 102),
            ],
            Locks:
            [
                new MonitorLockState(
                    ObjectAddress: 0x1000,
                    ObjectTypeFullName: "System.Object",
                    OwnerManagedThreadId: 1,
                    OwnerOSThreadId: 101,
                    OwnerThreadAddress: 0x100,
                    RecursionCount: 1,
                    WaitingThreadCount: 1,
                    IsContended: true,
                    Source: "SyncBlock")
                {
                    WaitingManagedThreadIds = [2],
                },
                new MonitorLockState(
                    ObjectAddress: 0x2000,
                    ObjectTypeFullName: "System.Object",
                    OwnerManagedThreadId: 2,
                    OwnerOSThreadId: 102,
                    OwnerThreadAddress: 0x200,
                    RecursionCount: 1,
                    WaitingThreadCount: 1,
                    IsContended: true,
                    Source: "SyncBlock")
                {
                    WaitingManagedThreadIds = [1],
                },
            ]);

    private static ThreadSnapshotArtifact BuildOneWayContentionSnapshot()
        => new(
            Origin: ThreadSnapshotOrigin.Live,
            ProcessId: 4242,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(25),
            RuntimeName: "CoreCLR",
            RuntimeVersion: "10.0.0",
            Threads:
            [
                BuildThread(managedThreadId: 1, osThreadId: 101),
                BuildThread(managedThreadId: 2, osThreadId: 102),
            ],
            Locks:
            [
                new MonitorLockState(
                    ObjectAddress: 0x1000,
                    ObjectTypeFullName: "System.Object",
                    OwnerManagedThreadId: 1,
                    OwnerOSThreadId: 101,
                    OwnerThreadAddress: 0x100,
                    RecursionCount: 1,
                    WaitingThreadCount: 1,
                    IsContended: true,
                    Source: "SyncBlock")
                {
                    WaitingManagedThreadIds = [2],
                },
            ]);

    private static ManagedThread BuildThread(int managedThreadId, uint osThreadId)
        => new(
            ManagedThreadId: managedThreadId,
            OSThreadId: osThreadId,
            Address: osThreadId,
            State: "Background",
            IsAlive: true,
            IsBackground: true,
            IsFinalizer: false,
            IsGc: false,
            IsThreadpoolWorker: false,
            LockCount: 1,
            CurrentExceptionType: null,
            TopFrameMethod: "System.Threading.Monitor.Enter(System.Object)",
            Frames:
            [
                new ManagedStackFrame(
                    Kind: "ManagedMethod",
                    DisplayName: "System.Threading.Monitor.Enter(System.Object)",
                    TypeFullName: "System.Threading.Monitor",
                    ModuleName: "System.Private.CoreLib",
                    InstructionPointer: 0,
                    StackPointer: 0),
            ])
        {
            IsLikelyBlocked = true,
            InferredWaitReason = "Monitor.Enter (contended)",
        };
}
