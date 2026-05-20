using DotnetDiagnosticsMcp.Core.Capabilities;
using Xunit;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class PtraceProbeTests
{
    private const string StatusWithCap = "Name:\tdotnet\nCapEff:\t00000000a80c25fb\n";
    private const string StatusWithoutCap = "Name:\tdotnet\nCapEff:\t0000000000000000\n";
    private const string StatusMalformed = "Name:\tdotnet\nCapEff:\tnot-a-hex\n";

    private static Func<string, string> ReadAllText(IDictionary<string, string> files)
        => path => files.TryGetValue(path, out var v) ? v : throw new FileNotFoundException(path);

    private static Func<string, bool> FileExists(IDictionary<string, string> files)
        => path => files.ContainsKey(path);

    [Fact]
    public void CapSysPtrace_held_overrides_any_scope()
    {
        var files = new Dictionary<string, string>
        {
            [PtraceProbe.ProcSelfStatusPath] = StatusWithCap,
            [PtraceProbe.YamaPtraceScopePath] = "1\n",
        };

        var result = PtraceProbe.DetectLinux(ReadAllText(files), FileExists(files));

        Assert.True(result.CanAttach);
        Assert.Contains("CAP_SYS_PTRACE held", result.Reason);
    }

    [Fact]
    public void Scope0_alone_allows_attach()
    {
        var files = new Dictionary<string, string>
        {
            [PtraceProbe.ProcSelfStatusPath] = StatusWithoutCap,
            [PtraceProbe.YamaPtraceScopePath] = "0\n",
        };

        var result = PtraceProbe.DetectLinux(ReadAllText(files), FileExists(files));

        Assert.True(result.CanAttach);
        Assert.Contains("ptrace_scope=0", result.Reason);
    }

    [Fact]
    public void Scope1_without_cap_blocks_attach_and_lists_mitigations()
    {
        var files = new Dictionary<string, string>
        {
            [PtraceProbe.ProcSelfStatusPath] = StatusWithoutCap,
            [PtraceProbe.YamaPtraceScopePath] = "1\n",
        };

        var result = PtraceProbe.DetectLinux(ReadAllText(files), FileExists(files));

        Assert.False(result.CanAttach);
        Assert.Contains("ptrace_scope=1", result.Reason);
        Assert.Contains("--cap-add SYS_PTRACE", result.Reason);
        Assert.Contains("ptrace_scope=0", result.Reason);
    }

    [Fact]
    public void Scope2_without_cap_blocks_attach()
    {
        var files = new Dictionary<string, string>
        {
            [PtraceProbe.ProcSelfStatusPath] = StatusWithoutCap,
            [PtraceProbe.YamaPtraceScopePath] = "2",
        };

        var result = PtraceProbe.DetectLinux(ReadAllText(files), FileExists(files));

        Assert.False(result.CanAttach);
        Assert.Contains("ptrace_scope=2", result.Reason);
    }

    [Fact]
    public void Scope3_blocks_even_when_cap_is_documented_as_irrelevant()
    {
        var files = new Dictionary<string, string>
        {
            [PtraceProbe.ProcSelfStatusPath] = StatusWithoutCap,
            [PtraceProbe.YamaPtraceScopePath] = "3",
        };

        var result = PtraceProbe.DetectLinux(ReadAllText(files), FileExists(files));

        Assert.False(result.CanAttach);
        Assert.Contains("ptrace_scope=3", result.Reason);
        Assert.Contains("cannot override", result.Reason);
    }

    [Fact]
    public void Yama_missing_means_classic_same_uid_attach_allowed()
    {
        var files = new Dictionary<string, string>
        {
            [PtraceProbe.ProcSelfStatusPath] = StatusWithoutCap,
        };

        var result = PtraceProbe.DetectLinux(ReadAllText(files), FileExists(files));

        Assert.True(result.CanAttach);
        Assert.Contains("Yama LSM not enabled", result.Reason);
    }

    [Fact]
    public void Malformed_CapEff_does_not_throw_and_falls_through_to_scope()
    {
        var files = new Dictionary<string, string>
        {
            [PtraceProbe.ProcSelfStatusPath] = StatusMalformed,
            [PtraceProbe.YamaPtraceScopePath] = "0",
        };

        var result = PtraceProbe.DetectLinux(ReadAllText(files), FileExists(files));

        Assert.True(result.CanAttach);
        Assert.Contains("ptrace_scope=0", result.Reason);
    }

    [Fact]
    public void Unknown_scope_value_is_treated_as_blocking()
    {
        var files = new Dictionary<string, string>
        {
            [PtraceProbe.ProcSelfStatusPath] = StatusWithoutCap,
            [PtraceProbe.YamaPtraceScopePath] = "42",
        };

        var result = PtraceProbe.DetectLinux(ReadAllText(files), FileExists(files));

        Assert.False(result.CanAttach);
        Assert.Contains("unknown value", result.Reason);
    }
}
