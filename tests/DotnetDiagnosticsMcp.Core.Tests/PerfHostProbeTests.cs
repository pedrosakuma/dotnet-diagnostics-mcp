using DotnetDiagnosticsMcp.Core.Capabilities;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class PerfHostProbeTests
{
    private const string StatusWithPerfmon = "Name:\tdotnet\nCapEff:\t0000004000000000\n";
    private const string StatusWithSysAdmin = "Name:\tdotnet\nCapEff:\t0000000000200000\n";
    private const string StatusWithoutCaps = "Name:\tdotnet\nCapEff:\t0000000000000000\n";

    private static Func<string, string> ReadAllText(IDictionary<string, string> files)
        => path => files.TryGetValue(path, out var v) ? v : throw new FileNotFoundException(path);

    private static Func<string, bool> FileExists(IDictionary<string, string> files)
        => path => files.ContainsKey(path);

    [Fact]
    public void PerfInstalled_WithCapPerfmon_CanTraceSchedSwitch()
    {
        var files = new Dictionary<string, string>
        {
            [PerfHostProbe.ProcSelfStatusPath] = StatusWithPerfmon,
            [PerfHostProbe.PerfEventParanoidPath] = "2\n",
        };

        var result = PerfHostProbe.DetectLinux(ReadAllText(files), FileExists(files), () => "/usr/bin/perf");

        result.PerfInstalled.Should().BeTrue();
        result.HasCapPerfmon.Should().BeTrue();
        result.HasCapSysAdmin.Should().BeFalse();
        result.PerfEventParanoid.Should().Be(2);
        result.CanTraceSchedSwitch.Should().BeTrue();
    }

    [Fact]
    public void PerfInstalled_WithSysAdmin_CanTraceSchedSwitch_WithoutCapPerfmon()
    {
        var files = new Dictionary<string, string>
        {
            [PerfHostProbe.ProcSelfStatusPath] = StatusWithSysAdmin,
            [PerfHostProbe.PerfEventParanoidPath] = "3\n",
        };

        var result = PerfHostProbe.DetectLinux(ReadAllText(files), FileExists(files), () => "/usr/bin/perf");

        result.PerfInstalled.Should().BeTrue();
        result.HasCapPerfmon.Should().BeFalse();
        result.HasCapSysAdmin.Should().BeTrue();
        result.PerfEventParanoid.Should().Be(3);
        result.CanTraceSchedSwitch.Should().BeTrue();
    }

    [Fact]
    public void PerfInstalled_WithoutCaps_NeedsParanoidMinusOne_ForSchedSwitch()
    {
        var files = new Dictionary<string, string>
        {
            [PerfHostProbe.ProcSelfStatusPath] = StatusWithoutCaps,
            [PerfHostProbe.PerfEventParanoidPath] = "-1\n",
        };

        var result = PerfHostProbe.DetectLinux(ReadAllText(files), FileExists(files), () => "/usr/bin/perf");

        result.PerfInstalled.Should().BeTrue();
        result.HasCapPerfmon.Should().BeFalse();
        result.HasCapSysAdmin.Should().BeFalse();
        result.PerfEventParanoid.Should().Be(-1);
        result.CanTraceSchedSwitch.Should().BeTrue();
    }

    [Fact]
    public void MissingPerfBinary_BlocksSchedSwitch_EvenWithRelaxedParanoid()
    {
        var files = new Dictionary<string, string>
        {
            [PerfHostProbe.ProcSelfStatusPath] = StatusWithoutCaps,
            [PerfHostProbe.PerfEventParanoidPath] = "-1\n",
        };

        var result = PerfHostProbe.DetectLinux(ReadAllText(files), FileExists(files), () => null);

        result.PerfInstalled.Should().BeFalse();
        result.HasCapPerfmon.Should().BeFalse();
        result.PerfEventParanoid.Should().Be(-1);
        result.CanTraceSchedSwitch.Should().BeFalse();
    }
}
