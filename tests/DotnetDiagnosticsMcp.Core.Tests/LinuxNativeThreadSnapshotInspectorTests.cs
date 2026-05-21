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

    [Fact]
    public void BuildManagedThread_UsesTidAsManagedThreadIdSoDrilldownCanAddressEachThread()
    {
        var parsed1 = new ParsedNativeThread(
            Tid: 4242,
            Frames: new[] { new ParsedNativeFrame(0xDEADBEEF, "Foo", "libc.so.6") });
        var parsed2 = new ParsedNativeThread(
            Tid: 4243,
            Frames: Array.Empty<ParsedNativeFrame>());

        var frames1 = parsed1.Frames
            .Select(f => new ManagedStackFrame("Native", f.DisplayName, null, f.ModuleName, f.InstructionPointer, 0))
            .ToArray();
        var frames2 = Array.Empty<ManagedStackFrame>();

        var t1 = LinuxNativeThreadSnapshotInspector.BuildManagedThread(parsed1, frames1, "S", true, true, "BlockedOnLock");
        var t2 = LinuxNativeThreadSnapshotInspector.BuildManagedThread(parsed2, frames2, "R", true, false, "Running");

        t1.ManagedThreadId.Should().Be(4242, "TID must back ManagedThreadId so query_thread_snapshot(view=\"stack\", threadId=tid) reaches each native thread");
        t2.ManagedThreadId.Should().Be(4243);
        t1.OSThreadId.Should().Be(4242U);
        t2.OSThreadId.Should().Be(4243U);
        new[] { t1.ManagedThreadId, t2.ManagedThreadId }.Should().OnlyHaveUniqueItems();
        t1.TopFrameMethod.Should().Be("Foo");
        t2.TopFrameMethod.Should().BeNull();
    }

    [Fact]
    public async Task InspectLiveAsync_TreatsExitNonZeroWithStdoutAsPartialSuccess()
    {
        // Regression for issue #105: on NativeAOT, eu-stack often emits valid frames for every
        // thread and then warns "dwfl_thread_getframes ... Callback returned failure" on the
        // AOT entrypoint of TID 1, exiting with code 1. We must keep the stdout frames and
        // surface the warning instead of throwing.
        if (!OperatingSystem.IsLinux()) return;

        var pid = Environment.ProcessId;
        var fakeEuStack = await CreateFakeEuStackAsync(
            stdout: $"PID {pid} - process\nTID 1:\n#0  0x401000 main+0x10 (/app/Foo)\nTID 7:\n#0  0x401200 worker+0x20 (/app/Foo)\n",
            stderr: "/usr/bin/eu-stack: dwfl_thread_getframes tid 1 at 0x401010 in /app/Foo: Callback returned failure",
            exitCode: 1);

        try
        {
            var inspector = new LinuxNativeThreadSnapshotInspector(euStackPath: fakeEuStack);
            var artifact = await inspector.InspectLiveAsync(pid);

            artifact.Threads.Should().HaveCountGreaterThan(0,
                "stdout had two TIDs and the partial-output handler should keep them");
            artifact.Warnings.Should().NotBeNull();
            artifact.Warnings!.Should().Contain(w =>
                w.Contains("eu-stack exited with code", StringComparison.Ordinal) &&
                w.Contains("Callback returned failure", StringComparison.Ordinal));
        }
        finally
        {
            try { File.Delete(fakeEuStack); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task InspectLiveAsync_StillThrowsWhenStdoutIsEmpty()
    {
        if (!OperatingSystem.IsLinux()) return;

        var pid = Environment.ProcessId;
        var fakeEuStack = await CreateFakeEuStackAsync(
            stdout: string.Empty,
            stderr: "eu-stack: total meltdown",
            exitCode: 2);

        try
        {
            var inspector = new LinuxNativeThreadSnapshotInspector(euStackPath: fakeEuStack);
            var act = () => inspector.InspectLiveAsync(pid);

            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            try { File.Delete(fakeEuStack); } catch { /* best effort */ }
        }
    }

    private static async Task<string> CreateFakeEuStackAsync(string stdout, string stderr, int exitCode)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fake-eu-stack-{Guid.NewGuid():N}.sh");
        var script =
            "#!/bin/sh\n" +
            "cat <<'__STDOUT__'\n" +
            stdout +
            "__STDOUT__\n" +
            "cat <<'__STDERR__' 1>&2\n" +
            stderr + "\n" +
            "__STDERR__\n" +
            $"exit {exitCode}\n";
        await File.WriteAllTextAsync(path, script);
        var chmod = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("chmod", $"+x {path}") { UseShellExecute = false });
        if (chmod is not null)
        {
            await chmod.WaitForExitAsync();
        }
        return path;
    }
}
