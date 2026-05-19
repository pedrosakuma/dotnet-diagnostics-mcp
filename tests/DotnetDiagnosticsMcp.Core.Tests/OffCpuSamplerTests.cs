using DotnetDiagnosticsMcp.Core.OffCpu;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class RoutingOffCpuSamplerTests
{
    [Fact]
    public async Task OnNonLinux_NonWindows_Throws_NotSupportedException()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) return; // exercised on macOS or other only
        var router = new RoutingOffCpuSampler(new PerfSchedOffCpuSampler());
        router.IsAvailable().Should().BeFalse();

        var act = async () => await router.SampleAsync(processId: 1, TimeSpan.FromSeconds(1));
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task OnWindows_Throws_NotSupportedException_WithIssueReference()
    {
        if (!OperatingSystem.IsWindows()) return;
        var router = new RoutingOffCpuSampler(new PerfSchedOffCpuSampler());
        router.IsAvailable().Should().BeFalse();

        var act = async () => await router.SampleAsync(processId: 1, TimeSpan.FromSeconds(1));
        var ex = await act.Should().ThrowAsync<NotSupportedException>();
        ex.Which.Message.Should().Contain("Windows");
        ex.Which.Message.Should().Contain("ETW");
        ex.Which.Message.Should().Contain("#41");
    }
}

public sealed class PerfSchedAggregateTests
{
    [Fact]
    public void GroupsByStackKeyAndRanksByTotalOffCpuMicros()
    {
        // Two spans on the same blocking stack and one on a different stack — the heavier stack
        // should win the top spot and the per-thread rollup should track per-TID totals.
        var futexStack = new List<OffCpuFrame>
        {
            // perf prints leaf→root: schedule() is the leaf (event fires in-kernel),
            // pthread_cond_wait() is the user-space root. Aggregate reverses internally.
            new("[kernel.kallsyms]", "schedule"),
            new("[kernel.kallsyms]", "futex_wait_queue"),
            new("libc.so.6", "pthread_cond_wait"),
        };
        var ioStack = new List<OffCpuFrame>
        {
            new("[kernel.kallsyms]", "schedule"),
            new("[kernel.kallsyms]", "io_schedule"),
        };

        var spans = new List<OffCpuSpan>
        {
            new(Tid: 1001, Comm: "worker-1", DurationMicros: 100_000, PrevState: "S", BlockingStack: futexStack),
            new(Tid: 1002, Comm: "worker-2", DurationMicros: 200_000, PrevState: "S", BlockingStack: futexStack),
            new(Tid: 1003, Comm: "worker-3", DurationMicros: 50_000,  PrevState: "D", BlockingStack: ioStack),
        };

        var result = PerfSchedOffCpuSampler.Aggregate(
            processId: 4242,
            startedAt: DateTimeOffset.UtcNow,
            duration: TimeSpan.FromSeconds(10),
            spans: spans,
            schedSwitches: 3,
            topN: 25);

        result.Summary.TotalOffCpuMicros.Should().Be(350_000);
        result.Summary.DistinctThreads.Should().Be(3);
        result.Summary.SchedSwitches.Should().Be(3);
        result.Summary.TopBlockingStacks.Should().HaveCount(2);
        result.Summary.TopBlockingStacks[0].OffCpuMicros.Should().Be(300_000, "futex stack aggregates 1001+1002");
        result.Summary.TopBlockingStacks[0].OccurrenceCount.Should().Be(2);
        result.Summary.TopBlockingStacks[0].DominantState.Should().Be("S");
        result.Summary.TopBlockingStacks[1].OffCpuMicros.Should().Be(50_000);
        result.Summary.TopBlockingStacks[1].DominantState.Should().Be("D");

        result.Artifact.Threads.Should().HaveCount(3);
        result.Artifact.Threads[0].Tid.Should().Be(1002, "worker-2 blocked the longest individually");
        result.Artifact.Threads[0].OffCpuMicros.Should().Be(200_000);
    }
}
