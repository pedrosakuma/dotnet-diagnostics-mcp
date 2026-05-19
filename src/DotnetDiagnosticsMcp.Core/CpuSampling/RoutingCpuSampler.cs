using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DotnetDiagnosticsMcp.Core.Capabilities;

namespace DotnetDiagnosticsMcp.Core.CpuSampling;

/// <summary>
/// Selects an <see cref="ICpuSampler"/> implementation based on the detected runtime
/// flavour of the target process. CoreCLR uses the managed EventPipe SampleProfiler;
/// NativeAOT falls back to <c>perf</c> on Linux when available.
/// </summary>
public sealed class RoutingCpuSampler : ICpuSampler
{
    private readonly ICapabilityDetector _capabilities;
    private readonly EventPipeCpuSampler _managed;
    private readonly PerfNativeAotCpuSampler _perf;
    private readonly ILogger<RoutingCpuSampler> _logger;

    public RoutingCpuSampler(
        ICapabilityDetector capabilities,
        EventPipeCpuSampler managed,
        PerfNativeAotCpuSampler perf,
        ILogger<RoutingCpuSampler>? logger = null)
    {
        _capabilities = capabilities;
        _managed = managed;
        _perf = perf;
        _logger = logger ?? NullLogger<RoutingCpuSampler>.Instance;
    }

    public async Task<CpuSampleResult> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN = 25,
        SourceResolutionOptions? sourceResolution = null,
        CancellationToken cancellationToken = default)
    {
        var caps = await _capabilities.DetectAsync(processId, cancellationToken).ConfigureAwait(false);
        if (caps.Runtime == RuntimeFlavor.NativeAot)
        {
            if (_perf.IsAvailable())
            {
                _logger.LogInformation("Routing CPU sample for pid {Pid} to perf fallback (NativeAOT detected).", processId);
                return await _perf.SampleAsync(processId, duration, topN, sourceResolution, cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"Process {processId} is NativeAOT and the managed SampleProfiler is not implemented. " +
                "The perf-based fallback requires the 'perf' binary in PATH, CAP_PERFMON (or CAP_SYS_ADMIN), " +
                "and perf_event_paranoid <= 2 on the host. Install linux-perf in the diagnostics image and add " +
                "the capability to the container's securityContext to enable native CPU sampling.");
        }

        return await _managed.SampleAsync(processId, duration, topN, sourceResolution, cancellationToken).ConfigureAwait(false);
    }
}
