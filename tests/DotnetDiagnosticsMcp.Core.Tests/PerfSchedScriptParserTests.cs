using DotnetDiagnosticsMcp.Core.OffCpu;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

/// <summary>
/// Drives <see cref="PerfSchedScriptParser"/> against representative <c>perf script</c> output
/// captured from sched_switch tracing. The fixtures live inline so the test is hermetic and
/// the format expectations are documented next to the parser.
/// </summary>
public sealed class PerfSchedScriptParserTests
{
    [Fact]
    public void PairsOutAndInEventsAcrossSwitches_LongFormat()
    {
        // Two sched_switch events for the target tid 1000:
        //   t=1.000000s — tid 1000 goes OUT (state=S) parked on futex_wait
        //   t=1.250000s — tid 1000 comes IN (next_pid=1000)
        // Expected: one OffCpuSpan of 250_000µs with the futex stack.
        const string script = """
                    target  1000 [001]   1.000000: sched:sched_switch: prev_comm=target prev_pid=1000 prev_prio=120 prev_state=S ==> next_comm=swapper/1 next_pid=0 next_prio=120
                            ffffffff81234567 schedule+0x0 ([kernel.kallsyms])
                            ffffffff81234890 futex_wait_queue+0x10 ([kernel.kallsyms])
                            7f1234abcdef pthread_cond_wait+0x80 (/usr/lib/libc.so.6)

                   swapper     0 [001]   1.250000: sched:sched_switch: prev_comm=swapper/1 prev_pid=0 prev_prio=120 prev_state=R ==> next_comm=target next_pid=1000 next_prio=120

            """;

        var (spans, switches) = PerfSchedScriptParser.Parse(script, new HashSet<int> { 1000 });

        switches.Should().Be(1);
        spans.Should().HaveCount(1);
        var span = spans[0];
        span.Tid.Should().Be(1000);
        span.DurationMicros.Should().Be(250_000);
        span.PrevState.Should().Be("S");
        span.BlockingStack.Should().HaveCount(3);
        span.BlockingStack[0].Method.Should().Be("schedule");
        span.BlockingStack[2].Method.Should().Be("pthread_cond_wait");
    }

    [Fact]
    public void PairsOutAndInEventsAcrossSwitches_CompactFormat()
    {
        // Same scenario but using the compact "X:N [P] S ==> Y:M [Q]" payload.
        const string script = """
                    target  1000 [001]   2.000000: sched:sched_switch: target:1000 [120] D ==> swapper/1:0 [120]
                            ffffffff80abcdef io_schedule+0x0 ([kernel.kallsyms])

                   swapper     0 [001]   2.010000: sched:sched_switch: swapper/1:0 [120] R ==> target:1000 [120]

            """;

        var (spans, switches) = PerfSchedScriptParser.Parse(script, new HashSet<int> { 1000 });

        switches.Should().Be(1);
        spans.Should().ContainSingle();
        spans[0].DurationMicros.Should().Be(10_000);
        spans[0].PrevState.Should().Be("D", "uninterruptible IO wait");
        spans[0].BlockingStack[0].Method.Should().Be("io_schedule");
    }

    [Fact]
    public void IgnoresEventsForNonTargetTids()
    {
        // Two events involve TID 999 (not in target set) — they must not produce any span and
        // must not contribute to the switch count, BUT they must still be consumed (i.e. not
        // crash the parser when interleaved with target events).
        const string script = """
                     noise   999 [000]   3.000000: sched:sched_switch: prev_comm=noise prev_pid=999 prev_prio=120 prev_state=S ==> next_comm=target next_pid=1000 next_prio=120
                            ffffffff81111111 schedule+0x0 ([kernel.kallsyms])

                    target  1000 [000]   3.100000: sched:sched_switch: prev_comm=target prev_pid=1000 prev_prio=120 prev_state=S ==> next_comm=noise next_pid=999 next_prio=120
                            ffffffff81234567 schedule+0x0 ([kernel.kallsyms])
                            ffffffff81234890 futex_wait_queue+0x10 ([kernel.kallsyms])

                     noise   999 [000]   3.150000: sched:sched_switch: prev_comm=noise prev_pid=999 prev_prio=120 prev_state=R ==> next_comm=target next_pid=1000 next_prio=120

            """;

        var (spans, switches) = PerfSchedScriptParser.Parse(script, new HashSet<int> { 1000 });

        switches.Should().Be(1, "only one switch had prev_pid=1000");
        spans.Should().ContainSingle("target went out at 3.100000 and back in at 3.150000");
        spans[0].DurationMicros.Should().Be(50_000);
    }

    [Fact]
    public void OrphanOutWithoutInIsDiscarded()
    {
        // Thread goes off-CPU but never returns within the capture window. Parser must not
        // crash and must not emit a malformed span.
        const string script = """
                    target  1000 [001]   1.000000: sched:sched_switch: prev_comm=target prev_pid=1000 prev_prio=120 prev_state=S ==> next_comm=swapper/1 next_pid=0 next_prio=120
                            ffffffff81234567 schedule+0x0 ([kernel.kallsyms])

            """;

        var (spans, switches) = PerfSchedScriptParser.Parse(script, new HashSet<int> { 1000 });

        switches.Should().Be(1);
        spans.Should().BeEmpty("no matching IN event closed the span");
    }

    [Fact]
    public void NormalisesStateSuffixes()
    {
        // perf prints "S+" / "D|" etc when extra flags are attached; we keep only the first letter.
        const string script = """
                    target  1000 [001]   1.000000: sched:sched_switch: prev_comm=target prev_pid=1000 prev_prio=120 prev_state=S+ ==> next_comm=swapper/1 next_pid=0 next_prio=120
                            ffffffff81234567 schedule+0x0 ([kernel.kallsyms])

                    target  1000 [001]   1.010000: sched:sched_switch: prev_comm=swapper/1 prev_pid=0 prev_prio=120 prev_state=R ==> next_comm=target next_pid=1000 next_prio=120

            """;

        var (spans, _) = PerfSchedScriptParser.Parse(script, new HashSet<int> { 1000 });
        spans.Should().ContainSingle();
        spans[0].PrevState.Should().Be("S");
    }
}
