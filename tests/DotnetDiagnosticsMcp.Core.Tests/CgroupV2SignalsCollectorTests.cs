using DotnetDiagnosticsMcp.Core.Container;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

/// <summary>
/// Drives the cgroup v2 collector against a synthetic filesystem laid out under a temp dir.
/// Verifies that throttle %, memory fraction, PSI parsing and "unlimited" sentinels are all
/// computed correctly without touching the real /sys/fs/cgroup.
/// </summary>
public sealed class CgroupV2SignalsCollectorTests : IDisposable
{
    private readonly string _root;
    private readonly string _cgroupRoot;
    private readonly string _procRoot;

    public CgroupV2SignalsCollectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cgroup-test-" + Guid.NewGuid().ToString("N"));
        _cgroupRoot = Path.Combine(_root, "cgroup");
        _procRoot = Path.Combine(_root, "proc");
        Directory.CreateDirectory(_cgroupRoot);
        Directory.CreateDirectory(_procRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private CgroupV2SignalsCollector NewCollector(string? rootFs = null) => new(
        logger: null,
        clock: TimeProvider.System,
        cgroupRoot: _cgroupRoot,
        procRoot: _procRoot,
        rootFs: rootFs ?? Path.Combine(_root, "rootfs-empty"));

    private void MarkAsCgroupV2() =>
        File.WriteAllText(Path.Combine(_cgroupRoot, "cgroup.controllers"), "cpu memory pids io\n");

    private string SetupPodCgroup(int pid, string podPath)
    {
        var procPid = Path.Combine(_procRoot, pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(procPid);
        File.WriteAllText(Path.Combine(procPid, "cgroup"), $"0::{podPath}\n");
        File.WriteAllText(Path.Combine(procPid, "oom_score"), "13\n");

        var podDir = _cgroupRoot + podPath;
        Directory.CreateDirectory(podDir);
        return podDir;
    }

    [Fact]
    public async Task ReportsThrottlePercentAndQuotaCores()
    {
        if (!OperatingSystem.IsLinux()) return; // Collector returns early on non-Linux; exercise on Linux only.
        MarkAsCgroupV2();
        var podDir = SetupPodCgroup(4242, "/kubepods/pod-xyz");

        File.WriteAllText(Path.Combine(podDir, "cpu.stat"),
            "usage_usec 12345678\nnr_periods 1000\nnr_throttled 150\nthrottled_usec 250000\n");
        File.WriteAllText(Path.Combine(podDir, "cpu.max"), "200000 100000\n"); // 2 cores
        File.WriteAllText(Path.Combine(podDir, "memory.current"), "536870912\n"); // 512 MiB
        File.WriteAllText(Path.Combine(podDir, "memory.max"), "1073741824\n"); // 1 GiB
        File.WriteAllText(Path.Combine(podDir, "memory.high"), "max\n");
        File.WriteAllText(Path.Combine(podDir, "memory.events"), "low 0\nhigh 0\nmax 4\noom 1\noom_kill 1\n");
        File.WriteAllText(Path.Combine(podDir, "pids.current"), "27\n");
        File.WriteAllText(Path.Combine(podDir, "pids.max"), "max\n");
        File.WriteAllText(Path.Combine(podDir, "cpu.pressure"),
            "some avg10=12.34 avg60=10.00 avg300=8.00 total=12345\n" +
            "full avg10=0.00 avg60=0.00 avg300=0.00 total=0\n");
        File.WriteAllText(Path.Combine(podDir, "memory.pressure"),
            "some avg10=1.00 avg60=0.50 avg300=0.10 total=999\n" +
            "full avg10=0.20 avg60=0.10 avg300=0.01 total=99\n");

        var signals = await NewCollector().CollectAsync(4242);

        signals.InContainer.Should().BeTrue();
        signals.CgroupVersion.Should().Be(CgroupVersion.V2);
        signals.CgroupPath.Should().Be("/kubepods/pod-xyz");
        signals.Cpu!.NrThrottled.Should().Be(150);
        signals.Cpu.ThrottlePercent.Should().BeApproximately(15.0, 0.001);
        signals.Cpu.QuotaCores.Should().BeApproximately(2.0, 0.001);
        signals.Memory!.MaxBytes.Should().Be(1_073_741_824);
        signals.Memory.UsageFraction.Should().BeApproximately(0.5, 0.001);
        signals.Memory.HighBytes.Should().BeNull(); // "max" => unlimited
        signals.Memory.OomKillCount.Should().Be(1);
        signals.Memory.MaxHitCount.Should().Be(4);
        signals.Pids!.Max.Should().BeNull(); // "max" => unlimited
        signals.Pressure!.CpuSomeAvg10.Should().BeApproximately(12.34, 0.01);
        signals.Pressure.MemFullAvg10.Should().BeApproximately(0.20, 0.01);
        signals.OomScore.Should().Be(13);
    }

    [Fact]
    public async Task UnlimitedQuotaClearsThrottlePercent()
    {
        if (!OperatingSystem.IsLinux()) return;
        MarkAsCgroupV2();
        var podDir = SetupPodCgroup(99, "/kubepods/pod-unlimited");
        File.WriteAllText(Path.Combine(podDir, "cpu.stat"),
            "usage_usec 100\nnr_periods 50\nnr_throttled 0\nthrottled_usec 0\n");
        File.WriteAllText(Path.Combine(podDir, "cpu.max"), "max 100000\n");
        File.WriteAllText(Path.Combine(podDir, "memory.current"), "1024\n");
        File.WriteAllText(Path.Combine(podDir, "memory.max"), "max\n");
        File.WriteAllText(Path.Combine(podDir, "pids.current"), "1\n");
        File.WriteAllText(Path.Combine(podDir, "pids.max"), "max\n");

        var signals = await NewCollector().CollectAsync(99);

        signals.Cpu!.QuotaCores.Should().BeNull();
        signals.Cpu.ThrottlePercent.Should().BeNull("no quota → throttling is impossible");
        signals.Memory!.MaxBytes.Should().BeNull();
        signals.Memory.UsageFraction.Should().BeNull();
    }

    [Fact]
    public async Task MissingPsiDegradesGracefully()
    {
        if (!OperatingSystem.IsLinux()) return;
        MarkAsCgroupV2();
        var podDir = SetupPodCgroup(7, "/kubepods/pod-no-psi");
        File.WriteAllText(Path.Combine(podDir, "cpu.stat"),
            "usage_usec 0\nnr_periods 0\nnr_throttled 0\nthrottled_usec 0\n");
        File.WriteAllText(Path.Combine(podDir, "cpu.max"), "max 100000\n");
        File.WriteAllText(Path.Combine(podDir, "memory.current"), "10\n");
        File.WriteAllText(Path.Combine(podDir, "memory.max"), "max\n");
        File.WriteAllText(Path.Combine(podDir, "pids.current"), "1\n");
        File.WriteAllText(Path.Combine(podDir, "pids.max"), "max\n");
        // No *.pressure files at all.

        var signals = await NewCollector().CollectAsync(7);

        signals.Pressure.Should().BeNull();
        signals.Cpu.Should().NotBeNull("missing PSI must not poison other signals");
        signals.Notes.Should().Contain(n => n.Contains("pressure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PrivateCgroupNamespace_DetectsContainerViaDockerEnvMarker()
    {
        if (!OperatingSystem.IsLinux()) return;
        MarkAsCgroupV2();
        // Process sees its own cgroup-namespace root ("0::/") — typical with Docker's
        // default --cgroupns=private on cgroup v2.
        var podDir = SetupPodCgroup(101, "/");
        File.WriteAllText(Path.Combine(podDir, "cpu.stat"),
            "usage_usec 0\nnr_periods 0\nnr_throttled 0\nthrottled_usec 0\n");
        File.WriteAllText(Path.Combine(podDir, "cpu.max"), "max 100000\n");
        File.WriteAllText(Path.Combine(podDir, "memory.current"), "1024\n");
        File.WriteAllText(Path.Combine(podDir, "memory.max"), "max\n");
        File.WriteAllText(Path.Combine(podDir, "pids.current"), "1\n");
        File.WriteAllText(Path.Combine(podDir, "pids.max"), "max\n");

        var fakeRoot = Path.Combine(_root, "rootfs-docker");
        Directory.CreateDirectory(fakeRoot);
        File.WriteAllText(Path.Combine(fakeRoot, ".dockerenv"), string.Empty);

        var signals = await NewCollector(rootFs: fakeRoot).CollectAsync(101);

        signals.InContainer.Should().BeTrue("private cgroup ns + .dockerenv marker = container");
        signals.CgroupPath.Should().Be("/");
        signals.Notes.Should().Contain(n => n.Contains("private cgroup namespace", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PrivateCgroupNamespace_DetectsContainerViaActiveLimits()
    {
        if (!OperatingSystem.IsLinux()) return;
        MarkAsCgroupV2();
        var podDir = SetupPodCgroup(202, "/");
        File.WriteAllText(Path.Combine(podDir, "cpu.stat"),
            "usage_usec 0\nnr_periods 100\nnr_throttled 0\nthrottled_usec 0\n");
        // Limits are set even though cgroup path looks like "/".
        File.WriteAllText(Path.Combine(podDir, "cpu.max"), "150000 100000\n");
        File.WriteAllText(Path.Combine(podDir, "memory.current"), "10\n");
        File.WriteAllText(Path.Combine(podDir, "memory.max"), "max\n");
        File.WriteAllText(Path.Combine(podDir, "pids.current"), "1\n");
        File.WriteAllText(Path.Combine(podDir, "pids.max"), "max\n");

        var signals = await NewCollector().CollectAsync(202);

        signals.InContainer.Should().BeTrue("cpu quota set on root cgroup ⇒ containerised");
        signals.Cpu!.QuotaCores.Should().BeApproximately(1.5, 0.001);
        signals.Notes.Should().Contain(n => n.Contains("active cpu/memory limits", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TrueHost_RootCgroupWithoutLimitsOrMarkers_IsNotContainer()
    {
        if (!OperatingSystem.IsLinux()) return;
        MarkAsCgroupV2();
        var hostDir = SetupPodCgroup(303, "/");
        File.WriteAllText(Path.Combine(hostDir, "cpu.stat"),
            "usage_usec 0\nnr_periods 0\nnr_throttled 0\nthrottled_usec 0\n");
        File.WriteAllText(Path.Combine(hostDir, "cpu.max"), "max 100000\n");
        File.WriteAllText(Path.Combine(hostDir, "memory.current"), "10\n");
        File.WriteAllText(Path.Combine(hostDir, "memory.max"), "max\n");
        File.WriteAllText(Path.Combine(hostDir, "pids.current"), "1\n");
        File.WriteAllText(Path.Combine(hostDir, "pids.max"), "max\n");

        var signals = await NewCollector().CollectAsync(303);

        signals.InContainer.Should().BeFalse();
        signals.CgroupPath.Should().Be("/");
    }

    [Fact]
    public async Task NoCgroupHierarchy_ReturnsCgroupVersionNoneAndNotInContainer()
    {
        // Brand-new temp roots without the cgroup.controllers marker file.
        var collector = new CgroupV2SignalsCollector(
            cgroupRoot: _cgroupRoot, procRoot: _procRoot);

        var signals = await collector.CollectAsync(Environment.ProcessId);

        if (!OperatingSystem.IsLinux())
        {
            signals.CgroupVersion.Should().Be(CgroupVersion.None);
            signals.InContainer.Should().BeFalse();
            signals.Notes.Should().Contain(n => n.Contains("Linux", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            signals.CgroupVersion.Should().Be(CgroupVersion.None);
            signals.InContainer.Should().BeFalse();
        }
    }
}
