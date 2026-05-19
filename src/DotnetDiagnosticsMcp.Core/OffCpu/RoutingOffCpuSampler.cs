using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.OffCpu;

/// <summary>
/// Platform router for <see cref="IOffCpuSampler"/>. Today Linux is the only implemented
/// backend (<see cref="PerfSchedOffCpuSampler"/>); the Windows ETW kernel-CSwitch path lands
/// in sub-slice 2b. Calling <see cref="SampleAsync"/> on an unsupported platform throws
/// <see cref="NotSupportedException"/> with the concrete capability gap so the LLM can
/// either re-route the investigation or stop early.
/// </summary>
public sealed class RoutingOffCpuSampler : IOffCpuSampler
{
    private readonly PerfSchedOffCpuSampler _linux;
    private readonly ILogger<RoutingOffCpuSampler> _logger;

    public RoutingOffCpuSampler(
        PerfSchedOffCpuSampler linux,
        ILogger<RoutingOffCpuSampler>? logger = null)
    {
        _linux = linux;
        _logger = logger ?? NullLogger<RoutingOffCpuSampler>.Instance;
    }

    public bool IsAvailable()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return _linux.IsAvailable();
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
            throw new NotSupportedException(
                "Off-CPU sampling on Windows requires the ETW kernel CSwitch provider, which is " +
                "not yet implemented (planned: dotnet-diagnostics-mcp issue #41 sub-slice 2b). " +
                "On Linux, ensure perf is available and the diagnostics container has CAP_PERFMON.");
        }

        throw new NotSupportedException(
            "Off-CPU sampling is only supported on Linux (perf sched) in this release.");
    }
}
