using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Memory;
using DotnetDiagnosticsMcp.Core.Threads;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

public sealed class ThreadSnapshotQueryTests
{
    [Fact]
    public void QueryThreadSnapshot_UniqueStacks_GroupsAndOrdersThreads()
    {
        var store = new MemoryDiagnosticHandleStore();
        var snapshot = CreateSnapshot();
        var handle = store.Register(snapshot.ProcessId, "thread-snapshot", snapshot, TimeSpan.FromMinutes(10), evictWhenProcessExits: false);

        var result = DiagnosticTools.QueryThreadSnapshot(store, handle.Id, view: "unique-stacks", topN: 3, framesToHash: 2, minCount: 1);

        result.IsError.Should().BeFalse();
        var groups = result.Data!.UniqueStacks;
        groups.Should().NotBeNull();
        var uniqueStacks = groups!;
        uniqueStacks.Select(group => group.ThreadCount).Should().Equal(6, 4, 2);
        uniqueStacks[0].ThreadPercentage.Should().BeApproximately(0.5, 0.0001);
        uniqueStacks[0].SampleThreads.Should().HaveCount(5, "sample thread ids are capped for large groups");
        uniqueStacks[0].SampleThreads.Select(sample => sample.ManagedThreadId).Should().Equal(1, 2, 3, 4, 5);
        uniqueStacks[0].CanonicalFrames.Select(frame => frame.DisplayName).Should().Equal("GroupA.Mid", "GroupA.Leaf");
        uniqueStacks[0].InferredWaitReason.Should().Be("Monitor.Wait");
    }

    [Fact]
    public void QueryThreadSnapshot_UniqueStacks_HonorsMinCountAndTopN()
    {
        var store = new MemoryDiagnosticHandleStore();
        var snapshot = CreateSnapshot();
        var handle = store.Register(snapshot.ProcessId, "thread-snapshot", snapshot, TimeSpan.FromMinutes(10), evictWhenProcessExits: false);

        var result = DiagnosticTools.QueryThreadSnapshot(store, handle.Id, view: "unique-stacks", topN: 2, framesToHash: 2, minCount: 3);

        result.IsError.Should().BeFalse();
        var groups = result.Data!.UniqueStacks;
        groups.Should().NotBeNull();
        var uniqueStacks = groups!;
        uniqueStacks.Should().HaveCount(2);
        uniqueStacks.Select(group => group.ThreadCount).Should().Equal(6, 4);
        result.Summary.Should().Contain("Returning 2/2 unique stack group(s)");
    }

    [Fact]
    public void QueryThreadSnapshot_Stack_IgnoresUniqueStackParameters()
    {
        var store = new MemoryDiagnosticHandleStore();
        var snapshot = CreateSnapshot();
        var handle = store.Register(snapshot.ProcessId, "thread-snapshot", snapshot, TimeSpan.FromMinutes(10), evictWhenProcessExits: false);

        var result = DiagnosticTools.QueryThreadSnapshot(store, handle.Id, view: "stack", threadId: 1, framesToHash: 0, minCount: 0);

        result.IsError.Should().BeFalse();
        result.Data!.ThreadId.Should().Be(1);
        result.Data.Thread!.TopFrameMethod.Should().Be("GroupA.Leaf");
    }

    [Fact]
    public void QueryThreadSnapshot_AsyncStalls_ReturnsClassifierView()
    {
        var store = new MemoryDiagnosticHandleStore();
        var snapshot = CreateSnapshot();
        var handle = store.Register(snapshot.ProcessId, "thread-snapshot", snapshot, TimeSpan.FromMinutes(10), evictWhenProcessExits: false);

        var result = DiagnosticTools.QueryThreadSnapshot(store, handle.Id, view: "async-stalls", topN: 2);

        result.IsError.Should().BeFalse();
        result.Data!.View.Should().Be("async-stalls");
        result.Data.AsyncStalls.Should().NotBeNull();
        result.Data.AsyncStalls!.ClassifiedThreads.Should().BeGreaterThan(0);
    }

    private static ThreadSnapshotArtifact CreateSnapshot()
    {
        var threads = new List<ManagedThread>();
        for (var managedThreadId = 1; managedThreadId <= 6; managedThreadId++)
        {
            threads.Add(CreateThread(
                managedThreadId,
                waitReason: "Monitor.Wait",
                CreateFrame("GroupA.Leaf", GroupAModuleVersionId, 0x06000001),
                CreateFrame("GroupA.Mid", GroupAModuleVersionId, 0x06000002),
                CreateFrame($"GroupA.Root{managedThreadId}", GroupAModuleVersionId, 0x06000100 + managedThreadId)));
        }

        for (var managedThreadId = 7; managedThreadId <= 10; managedThreadId++)
        {
            threads.Add(CreateThread(
                managedThreadId,
                waitReason: "Socket I/O",
                CreateFrame("GroupB.Leaf", GroupBModuleVersionId, 0x06000011),
                CreateFrame("GroupB.Mid", GroupBModuleVersionId, 0x06000012),
                CreateFrame($"GroupB.Root{managedThreadId}", GroupBModuleVersionId, 0x06000200 + managedThreadId)));
        }

        for (var managedThreadId = 11; managedThreadId <= 12; managedThreadId++)
        {
            threads.Add(CreateThread(
                managedThreadId,
                waitReason: "Thread.Sleep",
                CreateFrame("GroupC.Leaf", GroupCModuleVersionId, 0x06000021),
                CreateFrame("GroupC.Mid", GroupCModuleVersionId, 0x06000022),
                CreateFrame($"GroupC.Root{managedThreadId}", GroupCModuleVersionId, 0x06000300 + managedThreadId)));
        }

        threads.Add(CreateThread(
            13,
            waitReason: "Task.Wait",
            CreateFrame("System.Threading.Tasks.Task`1[[System.Int32]].get_Result()", GroupCModuleVersionId, 0x06000401),
            CreateFrame("Tests.AsyncFixture+<RunAsync>d__4.MoveNext()", GroupCModuleVersionId, 0x06000402)));

        return new ThreadSnapshotArtifact(
            Origin: ThreadSnapshotOrigin.Live,
            ProcessId: 4242,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(25),
            RuntimeName: "CoreClr",
            RuntimeVersion: "10.0.0",
            Threads: threads,
            Locks: Array.Empty<MonitorLockState>())
        {
            Source = "clrmd-thread-walk",
        };
    }

    private static ManagedThread CreateThread(int managedThreadId, string waitReason, params ManagedStackFrame[] frames)
        => new(
            ManagedThreadId: managedThreadId,
            OSThreadId: (uint)(10_000 + managedThreadId),
            Address: (ulong)managedThreadId,
            State: "Wait",
            IsAlive: true,
            IsBackground: false,
            IsFinalizer: false,
            IsGc: false,
            IsThreadpoolWorker: true,
            LockCount: 0,
            CurrentExceptionType: null,
            TopFrameMethod: frames[0].DisplayName,
            Frames: frames)
        {
            IsLikelyBlocked = true,
            InferredWaitReason = waitReason,
        };

    private static ManagedStackFrame CreateFrame(string displayName, Guid moduleVersionId, int metadataToken)
        => new(
            Kind: "ManagedMethod",
            DisplayName: displayName,
            TypeFullName: $"Tests.{displayName}",
            ModuleName: "Sample.dll",
            InstructionPointer: (ulong)metadataToken,
            StackPointer: (ulong)(metadataToken + 1),
            Identity: new MethodIdentity(
                MethodName: displayName,
                GenericArity: 0,
                ModuleName: "Sample.dll",
                ModulePath: "/worktree/tests/Sample.dll",
                ModuleVersionId: moduleVersionId,
                MetadataToken: metadataToken,
                TypeFullName: $"Tests.{displayName}"));

    private static readonly Guid GroupAModuleVersionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GroupBModuleVersionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid GroupCModuleVersionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
}
