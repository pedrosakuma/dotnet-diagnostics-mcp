using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.OffCpu;
using DotnetDiagnosticsMcp.Core.Threads;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class PerfReplayThreadSnapshotInspectorTests
{
    [Fact]
    public void ParseLastSeenSwitchOut_KeepsLatestStackPerTid()
    {
        const string script = """
            target 200 [001]  100.000000: sched:sched_switch: prev_comm=worker prev_pid=200 prev_prio=120 prev_state=S ==> next_comm=other next_pid=1 next_prio=120
                    00000000 schedule+0x1 ([kernel.kallsyms])
                    00000000 futex_wait_queue+0x2 ([kernel.kallsyms])
                    00000000 pthread_cond_wait+0x3 (/lib/libc.so.6)

            target 200 [001]  100.100000: sched:sched_switch: prev_comm=worker prev_pid=200 prev_prio=120 prev_state=D ==> next_comm=other next_pid=2 next_prio=120
                    00000000 schedule+0x1 ([kernel.kallsyms])
                    00000000 io_schedule+0x2 ([kernel.kallsyms])
            """;

        var parsed = PerfSchedScriptParser.ParseLastSeenSwitchOut(script, new HashSet<int> { 200 });

        parsed.Should().ContainKey(200);
        parsed[200].PrevState.Should().Be("D");
        parsed[200].Stack.Should().HaveCount(2);
        parsed[200].Stack[1].Method.Should().Be("io_schedule");
    }

    [Fact]
    public void BuildApproximateThreads_MapsPrevStateAndFrames()
    {
        var lastByTid = new Dictionary<int, PerfSchedScriptParser.LastSeenSwitchOut>
        {
            [42] = new(
                Tid: 42,
                Comm: "worker",
                PrevState: "S",
                Stack:
                [
                    new OffCpuFrame("[kernel.kallsyms]", "schedule"),
                    new OffCpuFrame("[kernel.kallsyms]", "futex_wait_queue"),
                ],
                TimestampSeconds: 1.0),
        };

        var threads = PerfReplayThreadSnapshotInspector.BuildApproximateThreads(lastByTid, maxFramesPerThread: 64);

        threads.Should().ContainSingle();
        var t = threads[0];
        t.ManagedThreadId.Should().Be(42);
        t.OSThreadId.Should().Be(42);
        t.IsLikelyBlocked.Should().BeTrue();
        t.InferredWaitReason.Should().Be("BlockedSleeping");
        t.TopFrameMethod.Should().Be("schedule");
        t.Frames.Should().HaveCount(2);
    }

    [Fact]
    public void EvaluateThreadSnapshotSupport_WhenPtraceDeniedButPerfAvailable_ReportsPerfReplayFallback()
    {
        if (!OperatingSystem.IsLinux()) return;

        var support = CapabilityDetector.EvaluateThreadSnapshotSupport(
            runtime: RuntimeFlavor.NativeAot,
            ptrace: new PtraceProbeResult(CanAttach: false, Reason: "ptrace denied"),
            euStackAvailable: true,
            canSampleOffCpu: true);

        support.CanCollect.Should().BeTrue();
        support.Source.Should().Be("perf-replay-approx");
        support.Preconditions.Should().Contain("Approximate");
    }
}
