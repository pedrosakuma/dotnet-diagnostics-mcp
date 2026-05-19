using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.OffCpu;

/// <summary>
/// Platform router for <see cref="IOffCpuSampler"/>. Routes Linux to
/// <see cref="PerfSchedOffCpuSampler"/> (perf sched_switch + DWARF unwind) and Windows to
/// <see cref="EtwOffCpuSampler"/> (NT Kernel Logger ContextSwitch with stack walk). Both
/// backends emit the same <see cref="OffCpuSnapshotArtifact"/> shape via the shared
/// <see cref="OffCpuAggregator"/>, so downstream tools (<c>query_off_cpu_snapshot</c>) stay
/// platform-agnostic.
/// </summary>
public sealed class RoutingOffCpuSampler : IOffCpuSampler
{
    private readonly PerfSchedOffCpuSampler _linux;
    private readonly EtwOffCpuSampler _windows;
    private readonly ILogger<RoutingOffCpuSampler> _logger;

    public RoutingOffCpuSampler(
        PerfSchedOffCpuSampler linux,
        EtwOffCpuSampler windows,
        ILogger<RoutingOffCpuSampler>? logger = null)
    {
        _linux = linux;
        _windows = windows;
        _logger = logger ?? NullLogger<RoutingOffCpuSampler>.Instance;
    }

    public bool IsAvailable()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return _linux.IsAvailable();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return _windows.IsAvailable();
        return false;
    }

    public Task<OffCpuSampleResult> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN = 25,
        CancellationToken cancellationToken = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return _linux.SampleAsync(processId, duration, topN, cancellationToken);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!_windows.IsAvailable())
            {
                throw new InvalidOperationException(
                    "ETW kernel CSwitch profiling is not available on this Windows host. The " +
                    "diagnostics sidecar must run with administrative elevation (or hold " +
                    "SeSystemProfilePrivilege) to enable kernel context-switch tracing.");
            }
            return _windows.SampleAsync(processId, duration, topN, cancellationToken);
        }

        throw new NotSupportedException(
            "Off-CPU sampling is only supported on Linux (perf sched) and Windows (ETW CSwitch) in this release.");
    }
}
