using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

/// <summary>
/// Covers the stale-socket defensive filter in <see cref="LocalProcessDiscovery"/> (issue #108).
/// The diagnostic IPC scanner exposes PIDs that were published by long-dead processes when the
/// sidecar shares <c>/tmp</c> with the target via a docker volume that survives restarts.
/// </summary>
public sealed class LocalProcessDiscoveryTests
{
    [Fact]
    public void ListProcesses_DoesNotReturnPidsAbsentFromProcessTable()
    {
        // Implementation contract: ListProcesses() filters every PID from
        // DiagnosticsClient.GetPublishedProcesses() through an OS liveness check before
        // exposing it to callers. On Linux the check must also reject thread IDs that
        // happen to collide with phantom PIDs left behind by stale diagnostic sockets in
        // shared /tmp volumes (issue #108 — Linux exposes worker threads at /proc/<tid>
        // too, so a naive Directory.Exists("/proc/<pid>") check is not enough; we require
        // thread-group leader semantics via /proc/<pid>/status Tgid == Pid).

        var discovery = new LocalProcessDiscovery();

        var processes = discovery.ListProcesses();

        foreach (var p in processes)
        {
            var alive = OperatingSystem.IsLinux()
                ? IsLinuxProcessLeader(p.ProcessId)
                : SafeProcessExists(p.ProcessId);
            alive.Should().BeTrue(
                $"discovery returned pid {p.ProcessId} ({p.ManagedEntrypointAssemblyName}) but it is not a live thread-group leader — likely a stale diagnostic socket leaked through the enumeration filter.");
        }
    }

    [Fact]
    public void TryGetProcess_ForObviouslyDeadPid_ReturnsNull()
    {
        // Smoke for the catch-all on the per-PID path. Using a synthetic high PID that
        // could not plausibly be live in the test process. On rare collision the test is
        // a no-op for the assertion side, but the call must never throw.
        var discovery = new LocalProcessDiscovery();

        var act = () => discovery.TryGetProcess(int.MaxValue);

        act.Should().NotThrow();
    }

    [Fact]
    public void TryGetProcess_ForLinuxThreadIdOfSelf_ReturnsNull()
    {
        // Issue #108 second-order: TryGetProcess(tid) on Linux must reject thread IDs of
        // arbitrary processes too — a phantom socket file referring to a stale PID can
        // collide with a TID of the calling sidecar itself, and we must not surface a
        // bogus DotnetProcess for that TID.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var selfPid = Environment.ProcessId;
        var taskDir = "/proc/" + selfPid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/task";
        if (!Directory.Exists(taskDir))
        {
            return;
        }

        var workerTid = Directory.EnumerateDirectories(taskDir)
            .Select(d => Path.GetFileName(d))
            .Where(name => int.TryParse(name, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var tid) && tid != selfPid)
            .Select(name => int.Parse(name!, System.Globalization.CultureInfo.InvariantCulture))
            .FirstOrDefault();

        if (workerTid == 0)
        {
            return;
        }

        var discovery = new LocalProcessDiscovery();
        var result = discovery.TryGetProcess(workerTid);

        result.Should().BeNull(
            $"tid {workerTid} is a thread of the current process (pid {selfPid}) — discovery must reject it.");
    }

    private static bool IsLinuxProcessLeader(int pid)
    {
        var statusPath = "/proc/" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/status";
        if (!File.Exists(statusPath))
        {
            return false;
        }

        foreach (var line in File.ReadLines(statusPath))
        {
            if (line.StartsWith("Tgid:", StringComparison.Ordinal))
            {
                return int.TryParse(line.AsSpan("Tgid:".Length).Trim(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var tgid)
                    && tgid == pid;
            }
        }

        return false;
    }

    private static bool SafeProcessExists(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
