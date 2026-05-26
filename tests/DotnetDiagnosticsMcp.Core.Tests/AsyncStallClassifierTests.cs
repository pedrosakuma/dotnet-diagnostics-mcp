using DotnetDiagnosticsMcp.Core.Threads;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class AsyncStallClassifierTests
{
    [Fact]
    public void AsyncStallClassifier_ClassifiesAllBuckets()
    {
        var snapshot = CreateSnapshot(
            CreateThread(1, true,
                CreateFrame("System.Threading.Tasks.Task`1[[System.Int32]].get_Result()")),
            CreateThread(2, true,
                CreateFrame("System.Threading.Channels.Channel`1+UnboundedChannelReader[[System.Int32]].WaitToReadAsync(System.Threading.CancellationToken)")),
            CreateThread(3, false,
                CreateFrame("System.Threading.Tasks.TaskCompletionSource`1[[System.Int32]].get_Task()")),
            CreateThread(4, true,
                CreateFrame("System.Threading.SemaphoreSlim.WaitAsync(System.Threading.CancellationToken)")),
            CreateThread(5, false,
                CreateFrame("System.Threading.Tasks.Task+DelayPromise.CompleteTimedOut()")),
            CreateThread(6, false,
                CreateFrame("Tests.AsyncFixture+<RunAsync>d__4.MoveNext()")),
            CreateThread(7, false,
                CreateFrame("System.Net.Sockets.Socket.Receive()")));

        var view = AsyncStallClassifier.Classify(snapshot, topN: 3);

        view.View.Should().Be("async-stalls");
        view.ClassifiedThreads.Should().Be(6);
        view.ByBucket.Should().BeEquivalentTo(
            new[]
            {
                new { Bucket = "SyncOverAsync", Count = 1 },
                new { Bucket = "ChannelAwait", Count = 1 },
                new { Bucket = "TcsPending", Count = 1 },
                new { Bucket = "SemaphoreAwait", Count = 1 },
                new { Bucket = "Delay", Count = 1 },
                new { Bucket = "Unknown", Count = 1 },
            },
            options => options.ExcludingMissingMembers());
        view.TopBlockedAsync.Should().HaveCount(3);
        view.TopBlockedAsync.Select(thread => thread.ThreadId).Should().Equal(1, 2, 4);
        view.TopBlockedAsync[0].TopFrames.Should().ContainSingle().Which.Should().Contain("get_Result");
    }

    [Fact]
    public void AsyncStallClassifier_TcsPending_RecognizesAwaitUnsafeOnCompletedWithHolder()
    {
        var snapshot = CreateSnapshot(
            CreateThread(17, true,
                CreateFrame("System.Runtime.CompilerServices.AsyncTaskMethodBuilder.AwaitUnsafeOnCompleted()"),
                CreateFrame("Tests.TcsHolder+<RunAsync>d__3.MoveNext()"),
                CreateFrame("Tests.TcsHolder.TaskCompletionSourceSlot")));

        var view = AsyncStallClassifier.Classify(snapshot, topN: 10);

        view.ClassifiedThreads.Should().Be(1);
        view.ByBucket.Should().ContainSingle(bucket => bucket.Bucket == "TcsPending" && bucket.Count == 1);
    }

    [Fact]
    public void AsyncStallClassifier_IgnoresThreadsWithoutAsyncSignals()
    {
        var snapshot = CreateSnapshot(
            CreateThread(33, false,
                CreateFrame("System.Net.Sockets.Socket.Receive()"),
                CreateFrame("System.IO.Stream.Read()")));

        var view = AsyncStallClassifier.Classify(snapshot, topN: 5);

        view.ClassifiedThreads.Should().Be(0);
        view.ByBucket.Should().BeEmpty();
        view.TopBlockedAsync.Should().BeEmpty();
    }

    private static ThreadSnapshotArtifact CreateSnapshot(params ManagedThread[] threads)
        => new(
            Origin: ThreadSnapshotOrigin.Live,
            ProcessId: 4242,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(12),
            RuntimeName: "CoreClr",
            RuntimeVersion: "10.0.0",
            Threads: threads,
            Locks: Array.Empty<MonitorLockState>());

    private static ManagedThread CreateThread(int managedThreadId, bool likelyBlocked, params ManagedStackFrame[] frames)
        => new(
            ManagedThreadId: managedThreadId,
            OSThreadId: (uint)(10_000 + managedThreadId),
            Address: (ulong)managedThreadId,
            State: "Wait",
            IsAlive: true,
            IsBackground: true,
            IsFinalizer: false,
            IsGc: false,
            IsThreadpoolWorker: true,
            LockCount: 0,
            CurrentExceptionType: null,
            TopFrameMethod: frames.FirstOrDefault()?.DisplayName,
            Frames: frames)
        {
            IsLikelyBlocked = likelyBlocked,
            InferredWaitReason = likelyBlocked ? "Task.Wait" : null,
        };

    private static ManagedStackFrame CreateFrame(string displayName)
        => new(
            Kind: "ManagedMethod",
            DisplayName: displayName,
            TypeFullName: displayName,
            ModuleName: "Sample.dll",
            InstructionPointer: 0,
            StackPointer: 0,
            Identity: null);
}
