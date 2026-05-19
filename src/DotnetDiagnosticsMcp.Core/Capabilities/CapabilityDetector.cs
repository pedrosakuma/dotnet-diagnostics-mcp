using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Internal;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.Capabilities;

/// <summary>
/// Detects diagnostic capabilities by combining the process info (when available) with an active
/// EventPipe probe: it attempts a short-lived session with the SampleProfiler provider and checks
/// whether any sample events are emitted. SampleProfiler is implemented by CoreCLR and absent in
/// NativeAOT, so the absence of events is a strong NativeAOT indicator. When the target is
/// NativeAOT and Linux <c>perf</c> is available, CPU sampling is reported as supported via the
/// perf-based fallback (managed frames will be unresolved native symbols).
/// </summary>
public sealed class CapabilityDetector : ICapabilityDetector
{
    private static readonly TimeSpan ProbeWindow = TimeSpan.FromSeconds(2);
    private readonly ILogger<CapabilityDetector> _logger;
    private readonly PerfNativeAotCpuSampler? _perfSampler;

    public CapabilityDetector(ILogger<CapabilityDetector>? logger = null, PerfNativeAotCpuSampler? perfSampler = null)
    {
        _logger = logger ?? NullLogger<CapabilityDetector>.Instance;
        _perfSampler = perfSampler;
    }

    public async Task<DiagnosticCapabilities> DetectAsync(int processId, CancellationToken cancellationToken = default)
    {
        var client = new DiagnosticsClient(processId);
        var snapshot = ProcessInfoReflection.TryGet(client);
        var runtimeVersion = snapshot?.ClrProductVersionString ?? string.Empty;

        var probe = await ProbeSampleProfilerAsync(client, cancellationToken).ConfigureAwait(false);
        var runtime = ClassifyRuntime(snapshot, probe);

        var perfAvailable = _perfSampler is not null && _perfSampler.IsAvailable();
        var canSampleCpu = (runtime == RuntimeFlavor.CoreClr && probe.SampleEventsReceived > 0) ||
                           (runtime == RuntimeFlavor.NativeAot && perfAvailable);
        var canCollectGcDump = runtime == RuntimeFlavor.CoreClr;
        var canReadCounters = probe.SessionStarted;
        var canCollectExceptions = probe.SessionStarted;
        var canCollectHttp = probe.SessionStarted;
        var canCollectCustomEs = probe.SessionStarted;
        var canCollectDump = true;

        var notes = BuildNotes(runtime, probe, perfAvailable);

        return new DiagnosticCapabilities(
            ProcessId: processId,
            Runtime: runtime,
            RuntimeVersion: runtimeVersion,
            CanReadEventCounters: canReadCounters,
            CanSampleCpu: canSampleCpu,
            CanCollectGcDump: canCollectGcDump,
            CanCollectExceptions: canCollectExceptions,
            CanCollectHttpActivity: canCollectHttp,
            CanCollectCustomEventSource: canCollectCustomEs,
            CanCollectProcessDump: canCollectDump,
            Notes: notes);
    }

    private async Task<ProbeResult> ProbeSampleProfilerAsync(DiagnosticsClient client, CancellationToken ct)
    {
        var providers = new[]
        {
            new EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", System.Diagnostics.Tracing.EventLevel.Informational),
        };

        EventPipeSession? session = null;
        try
        {
            session = await client.StartEventPipeSessionAsync(providers, requestRundown: false, circularBufferMB: 64, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is ServerNotAvailableException ||
            ex is UnsupportedCommandException ||
            ex is UnsupportedProtocolException ||
            ex is ServerErrorException ||
            ex is TimeoutException)
        {
            _logger.LogDebug(ex, "EventPipe probe failed to start for pid {Pid}.", client);
            return new ProbeResult(SessionStarted: false, SampleEventsReceived: 0, FailureReason: ex.GetType().Name);
        }

        try
        {
            return await DrainProbeAsync(session, ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // best-effort stop
            }

            session.Dispose();
        }
    }

    private static async Task<ProbeResult> DrainProbeAsync(EventPipeSession session, CancellationToken ct)
    {
        long received = 0;
        var counter = new System.Threading.SemaphoreSlim(0, 1);

        var sourceTask = Task.Run(() =>
        {
            using var source = new Microsoft.Diagnostics.Tracing.EventPipeEventSource(session.EventStream);
            source.AllEvents += _ =>
            {
                if (System.Threading.Interlocked.Increment(ref received) == 1)
                {
                    counter.Release();
                }
            };

            try
            {
                source.Process();
            }
            catch (Exception)
            {
                // session stop produces stream-closed exceptions during shutdown
            }
        }, ct);

        try
        {
            await counter.WaitAsync(ProbeWindow, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }

        // give a touch of time for sample events to accumulate before stopping
        await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);

        _ = sourceTask;
        return new ProbeResult(SessionStarted: true, SampleEventsReceived: received, FailureReason: null);
    }

    private static RuntimeFlavor ClassifyRuntime(ProcessInfoSnapshot? snapshot, ProbeResult probe)
    {
        if (!probe.SessionStarted)
        {
            return RuntimeFlavor.Unknown;
        }

        // SampleProfiler is a strong CoreCLR-only signal.
        if (probe.SampleEventsReceived > 0)
        {
            return RuntimeFlavor.CoreClr;
        }

        // The PortableRuntimeIdentifier on NativeAOT often reflects the published RID;
        // and SampleProfiler does not emit events under AOT today.
        if (snapshot is null)
        {
            return RuntimeFlavor.Unknown;
        }

        return RuntimeFlavor.NativeAot;
    }

    private static string BuildNotes(RuntimeFlavor runtime, ProbeResult probe, bool perfAvailable)
    {
        if (!probe.SessionStarted)
        {
            return $"Could not start EventPipe session ({probe.FailureReason}). " +
                   "Verify the process is .NET 6+ and that DOTNET_EnableDiagnostics is not 0.";
        }

        return runtime switch
        {
            RuntimeFlavor.CoreClr => "CoreCLR runtime detected; all diagnostic tools available.",
            RuntimeFlavor.NativeAot when perfAvailable =>
                "NativeAOT detected (SampleProfiler emitted no events). CPU sampling is available via the Linux 'perf' fallback " +
                "(native symbols only, no managed IL handoff). gcdump is not supported.",
            RuntimeFlavor.NativeAot =>
                "NativeAOT detected (SampleProfiler emitted no events). " +
                "CPU sampling and gcdump are not available; counters, exceptions and EventSources still work. " +
                "Install Linux 'perf' to enable the native CPU sampling fallback.",
            _ => "Could not classify runtime flavor."
        };
    }

    private sealed record ProbeResult(bool SessionStarted, long SampleEventsReceived, string? FailureReason);
}
