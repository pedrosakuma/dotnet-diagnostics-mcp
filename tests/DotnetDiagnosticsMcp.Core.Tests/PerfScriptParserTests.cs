using DotnetDiagnosticsMcp.Core.CpuSampling;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnosticsMcp.Core.Tests;

/// <summary>
/// Unit tests for the NativeAOT CPU sampling fallback. The parser is fully unit-tested
/// against fixed <c>perf script</c> output; the live <c>perf record</c> path is exercised
/// only in environments with kernel permission (skipped here).
/// </summary>
public class PerfScriptParserTests
{
    private const string TwoSamplesFromPid1 = """
            sample-target  1 [001] 12345.678901: cpu-clock:
                            ffffabcd12340000 NativeAotSample::HotPath+0x42 (/app/NativeAotSample)
                            ffffabcd12340100 NativeAotSample::Main+0x10 (/app/NativeAotSample)
                            7f1234560000 __libc_start_main+0x80 (/lib/libc.so.6)

            sample-target  1 [002] 12345.679001: cpu-clock:
                            ffffabcd12340000 NativeAotSample::HotPath+0x42 (/app/NativeAotSample)
                            ffffabcd12340200 NativeAotSample::ColdPath+0x10 (/app/NativeAotSample)
                            7f1234560000 __libc_start_main+0x80 (/lib/libc.so.6)

            other-proc  2 [000] 12345.679500: cpu-clock:
                            ffffaaaa00000000 NoiseFunction+0x0 (/usr/lib/libfoo.so)

            """;

    [Fact]
    public void Parser_AcceptsStandardPerfScriptShape()
    {
        var samples = PerfScriptParser.Parse(TwoSamplesFromPid1);

        samples.Should().HaveCount(3);
        samples[0].ProcessId.Should().Be(1);
        samples[0].Frames.Should().HaveCount(3);
        samples[0].Frames[0].Module.Should().Be("/app/NativeAotSample");
        samples[0].Frames[0].Symbol.Should().Be("NativeAotSample::HotPath");
        samples[0].Frames[2].Module.Should().Be("/lib/libc.so.6");
        samples[0].Frames[2].Symbol.Should().Be("__libc_start_main");
    }

    [Fact]
    public void Parser_FiltersByProcessIdWhenRequested()
    {
        var samples = PerfScriptParser.Parse(TwoSamplesFromPid1, processId: 1);

        samples.Should().HaveCount(2);
        samples.Should().OnlyContain(s => s.ProcessId == 1);
    }

    [Fact]
    public void Aggregate_RanksHotspots_AndProducesCallTree()
    {
        var (total, hotspots, root) = PerfNativeAotCpuSampler.Aggregate(TwoSamplesFromPid1, processId: 1, topN: 5);

        total.Should().Be(2);
        hotspots.Should().NotBeEmpty();

        // HotPath appears in both samples → highest inclusive count, exclusive = 0
        // (always called from another frame). __libc_start_main is the root of both stacks.
        var hot = hotspots.Single(h => h.Frame.Method.Contains("HotPath", StringComparison.Ordinal));
        hot.InclusiveSamples.Should().Be(2);
        hot.Identity.Should().BeNull("native frames do not carry a managed (mvid, token) handoff");

        // Tree root is synthetic; first real frame is __libc_start_main (the deepest caller
        // in both stacks) because perf prints leaf→root and we reverse for tree traversal.
        root.Children.Should().NotBeEmpty();
        var firstRealFrame = root.Children[0];
        firstRealFrame.Frame.Method.Should().Be("__libc_start_main");
        firstRealFrame.InclusiveSamples.Should().Be(2);
    }

    [Fact]
    public void Parser_SkipsCommentLines_AndOrphanFrames()
    {
        const string output = """
            # ========
            # captured on: host
            # ========

                            ffff00000000 OrphanFrame+0x0 (/lib/orphan.so)

            sample-target  1 [001] 12345.0: cpu-clock:
                            ffff11110000 RealFrame+0x0 (/app/RealMod)

            """;

        var samples = PerfScriptParser.Parse(output);
        samples.Should().HaveCount(1);
        samples[0].Frames.Single().Symbol.Should().Be("RealFrame");
    }

    [Fact]
    public void Parser_HandlesFramesWithoutModule()
    {
        const string output = """
            sample-target  7 [000] 1.0: cpu-clock:
                            ffff11110000 [unknown]

            """;

        var samples = PerfScriptParser.Parse(output);
        samples.Should().HaveCount(1);
        samples[0].Frames[0].Symbol.Should().Be("[unknown]");
        samples[0].Frames[0].Module.Should().BeEmpty();
    }
}
