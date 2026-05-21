using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DotnetDiagnosticsMcp.Core.Capabilities;

namespace DotnetDiagnosticsMcp.Core.CpuSampling;

/// <summary>
/// Selects an <see cref="ICpuSampler"/> implementation based on the detected runtime
/// flavour of the target process. CoreCLR uses the managed EventPipe SampleProfiler;
/// NativeAOT uses ETW kernel profiling on Windows or <c>perf</c> on Linux.
/// </summary>
public sealed class RoutingCpuSampler : ICpuSampler
{
    private readonly ICapabilityDetector _capabilities;
    private readonly EventPipeCpuSampler _managed;
    private readonly PerfNativeAotCpuSampler _perf;
    private readonly EtwNativeAotCpuSampler _etw;
    private readonly ILogger<RoutingCpuSampler> _logger;

    public RoutingCpuSampler(
        ICapabilityDetector capabilities,
        EventPipeCpuSampler managed,
        PerfNativeAotCpuSampler perf,
        EtwNativeAotCpuSampler etw,
        ILogger<RoutingCpuSampler>? logger = null)
    {
        _capabilities = capabilities;
        _managed = managed;
        _perf = perf;
        _etw = etw;
        _logger = logger ?? NullLogger<RoutingCpuSampler>.Instance;
    }

    public async Task<CpuSampleResult> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN = 25,
        SourceResolutionOptions? sourceResolution = null,
        MethodInstantiationResolutionOptions? methodInstantiationResolution = null,
        CancellationToken cancellationToken = default)
    {
        var caps = await _capabilities.DetectAsync(processId, cancellationToken).ConfigureAwait(false);
        if (caps.Runtime == RuntimeFlavor.NativeAot)
        {
            return await SampleNativeAotAsync(processId, duration, topN, sourceResolution, cancellationToken)
                .ConfigureAwait(false);
        }

        return await _managed.SampleAsync(processId, duration, topN, sourceResolution, methodInstantiationResolution, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CpuSampleResult> SampleNativeAotAsync(
        int processId,
        TimeSpan duration,
        int topN,
        SourceResolutionOptions? sourceResolution,
        CancellationToken cancellationToken)
    {
        // OS-explicit dispatch: ETW on Windows, perf on Linux.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (_etw.IsAvailable())
            {
                _logger.LogInformation("Routing CPU sample for pid {Pid} to ETW kernel profiling (NativeAOT on Windows).", processId);
                return await _etw.SampleAsync(processId, duration, topN, sourceResolution, methodInstantiationResolution: null, cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"Process {processId} is NativeAOT and the managed SampleProfiler is not implemented. " +
                "On Windows, the ETW kernel profiling fallback requires administrative elevation " +
                "(or SeSystemProfilePrivilege). Run the diagnostics process as Administrator to enable " +
                "native CPU sampling for NativeAOT processes.");
        }

        if (_perf.IsAvailable())
        {
            _logger.LogInformation("Routing CPU sample for pid {Pid} to perf fallback (NativeAOT on Linux).", processId);
            return await _perf.SampleAsync(processId, duration, topN, sourceResolution, methodInstantiationResolution: null, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"Process {processId} is NativeAOT and the managed SampleProfiler is not implemented. " +
            "On Linux, the perf-based fallback requires the 'perf' binary in PATH, CAP_PERFMON (or CAP_SYS_ADMIN), " +
            "and perf_event_paranoid <= 2 on the host. Install linux-perf in the diagnostics image and add " +
            "the capability to the container's securityContext to enable native CPU sampling.");
    }
}
