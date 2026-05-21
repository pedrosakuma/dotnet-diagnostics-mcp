using DotnetDiagnosticsMcp.Core.Threads;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class LinuxNativeThreadSnapshotInspectorTests
{
    [Fact]
    public void ParseEuStackOutput_ParsesThreadsAndDemanglesManagedSymbols()
    {
        const string output = """
            PID 777 - process
            TID 777:
            #0  0x0000000000401000 S_P_CoreLib_System_Threading_Thread__StartHelper+0x10 (/app/NativeAotSample)
            #1  0x0000000000400ff0 __libc_start_main+0x80 (/lib/x86_64-linux-gnu/libc.so.6)

            TID 812:
            #0  0x0000000000410000 pthread_cond_wait+0x52 (/lib/x86_64-linux-gnu/libpthread.so.0)
            """;

        var parsed = LinuxNativeThreadSnapshotInspector.ParseEuStackOutput(output, maxFramesPerThread: 64);

        parsed.Should().HaveCount(2);
        parsed[0].Tid.Should().Be(777);
        parsed[0].Frames.Should().HaveCount(2);
        parsed[0].Frames[0].DisplayName.Should().Contain("System.Private.CoreLib.System.Threading.Thread.StartHelper");
        parsed[0].Frames[0].DisplayName.Should().Contain("+0x10");
        parsed[0].Frames[1].DisplayName.Should().Contain("__libc_start_main");
    }

    [Fact]
    public void ParseEuStackOutput_RespectsFrameCap()
    {
        const string output = """
            TID 100:
            #0  0x1 a+0x1 (/a)
            #1  0x2 b+0x2 (/b)
            #2  0x3 c+0x3 (/c)
            """;

        var parsed = LinuxNativeThreadSnapshotInspector.ParseEuStackOutput(output, maxFramesPerThread: 2);

        parsed.Should().ContainSingle();
        parsed[0].Frames.Should().HaveCount(2);
        parsed[0].Frames.Select(f => f.DisplayName).Should().ContainInOrder("a+0x1", "b+0x2");
    }
}
