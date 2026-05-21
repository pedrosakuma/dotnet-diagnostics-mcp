using System.IO;
using DotnetDiagnosticsMcp.Core.Container;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Internal;
using DotnetDiagnosticsMcp.Core.OffCpu;
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
    private readonly EtwNativeAotCpuSampler? _etwSampler;
    private readonly IContainerSignalsCollector? _containerSignals;
    private readonly IOffCpuSampler? _offCpuSampler;

    public CapabilityDetector(
        ILogger<CapabilityDetector>? logger = null,
        PerfNativeAotCpuSampler? perfSampler = null,
        EtwNativeAotCpuSampler? etwSampler = null,
        IContainerSignalsCollector? containerSignals = null,
        IOffCpuSampler? offCpuSampler = null)
    {
        _logger = logger ?? NullLogger<CapabilityDetector>.Instance;
        _perfSampler = perfSampler;
        _etwSampler = etwSampler;
        _containerSignals = containerSignals;
        _offCpuSampler = offCpuSampler;
    }

    public async Task<DiagnosticCapabilities> DetectAsync(int processId, CancellationToken cancellationToken = default)
    {
        var client = new DiagnosticsClient(processId);
        var snapshot = ProcessInfoReflection.TryGet(client);
        var runtimeVersion = snapshot?.ClrProductVersionString ?? string.Empty;

        var probe = await ProbeSampleProfilerAsync(client, cancellationToken).ConfigureAwait(false);
        var loadedModules = LoadedModuleInspector.TryGetSignature(processId);
        var runtime = ClassifyRuntime(snapshot, probe, loadedModules);

        var perfAvailable = _perfSampler is not null && _perfSampler.IsAvailable();
        var etwAvailable = _etwSampler is not null && _etwSampler.IsAvailable();
        var canSampleOffCpu = _offCpuSampler is not null && _offCpuSampler.IsAvailable();
        var ptrace = PtraceProbe.Detect();
        var euStackAvailable = IsEuStackAvailable();
        var (canCollectThreadSnapshot, threadSnapshotSource, threadSnapshotPreconditions) =
            EvaluateThreadSnapshotSupport(runtime, ptrace, euStackAvailable, canSampleOffCpu);
        // CoreCLR always supports SampleProfiler; whether the 2-second probe happened to
        // catch a Thread/Sample event is a function of workload, not capability. As long
        // as we classified the runtime as CoreCLR (preferably from module inspection,
        // otherwise from the SampleProfiler probe itself) we can sample CPU. NativeAOT
        // relies on an out-of-process sampler (perf on Linux, ETW on Windows).
        var canSampleCpu = (runtime == RuntimeFlavor.CoreClr) ||
                           (runtime == RuntimeFlavor.NativeAot && (perfAvailable || etwAvailable));
        var canCollectGcDump = runtime == RuntimeFlavor.CoreClr;
        var canReadCounters = probe.SessionStarted;
        var canCollectExceptions = probe.SessionStarted;
        var canCollectHttp = probe.SessionStarted;
        var canCollectCustomEs = probe.SessionStarted;
        var canCollectDump = true;

        var notes = BuildNotes(
            runtime,
            probe,
            perfAvailable,
            etwAvailable,
            canSampleOffCpu,
            ptrace,
            canCollectThreadSnapshot,
            threadSnapshotSource,
            threadSnapshotPreconditions);

        var (inContainer, cgroupV2, canSeeThrottle) = await DetectContainerFlagsAsync(processId, cancellationToken).ConfigureAwait(false);

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
            Notes: notes)
        {
            InContainer = inContainer,
            CgroupV2 = cgroupV2,
            CanSeeThrottle = canSeeThrottle,
            CanSampleOffCpu = canSampleOffCpu,
            CanAttachClrMD = ptrace.CanAttach,
            AttachClrMdReason = ptrace.Reason,
            CanCollectThreadSnapshot = canCollectThreadSnapshot,
            ThreadSnapshotSource = threadSnapshotSource,
            ThreadSnapshotPreconditions = threadSnapshotPreconditions,
        };
    }

    private async Task<(bool InContainer, bool CgroupV2, bool CanSeeThrottle)> DetectContainerFlagsAsync(int processId, CancellationToken ct)
    {
        if (_containerSignals is null) return (false, false, false);
        try
        {
            var signals = await _containerSignals.CollectAsync(processId, ct).ConfigureAwait(false);
            return (
                InContainer: signals.InContainer,
                CgroupV2: signals.CgroupVersion == CgroupVersion.V2,
                CanSeeThrottle: signals.Cpu?.QuotaCores is > 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Container signal probe failed for pid {Pid} during capability detection.", processId);
            return (false, false, false);
        }
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
        long anyEvents = 0;
        long sampleEvents = 0;
        var counter = new System.Threading.SemaphoreSlim(0, 1);

        var sourceTask = Task.Run(() =>
        {
            using var source = new Microsoft.Diagnostics.Tracing.EventPipeEventSource(session.EventStream);
            // AllEvents covers manifests + metadata + every payload event, so it is reliable
            // for detecting "the session started and the runtime is streaming something". Used
            // only for the SessionStarted signal and to gate the wait.
            source.AllEvents += _ =>
            {
                if (System.Threading.Interlocked.Increment(ref anyEvents) == 1)
                {
                    counter.Release();
                }
            };

            // Real SampleProfiler samples are a strong CoreCLR-only signal. NativeAOT is
            // a no-op for this provider, but its EventPipe stream still emits manifests
            // and metadata events — counting those here previously caused a misclassification
            // regression when /proc inspection wasn't possible (e.g. foreign OS or restricted
            // containers). Track sample events explicitly so the classifier fallback can
            // distinguish "the runtime is alive on EventPipe" from "the runtime actually
            // emits SampleProfiler samples".
            source.Dynamic.AddCallbackForProviderEvent(
                "Microsoft-DotNETCore-SampleProfiler",
                "Thread/Sample",
                _ => System.Threading.Interlocked.Increment(ref sampleEvents));

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
        return new ProbeResult(SessionStarted: true, SampleEventsReceived: sampleEvents, FailureReason: null);
    }

    private static RuntimeFlavor ClassifyRuntime(ProcessInfoSnapshot? snapshot, ProbeResult probe, LoadedModuleSignature? modules)
    {
        if (!probe.SessionStarted)
        {
            return RuntimeFlavor.Unknown;
        }

        // Strongest signal: inspect loaded modules of the target process. CoreCLR-hosted apps
        // always load libcoreclr.so / coreclr.dll; a self-contained NativeAOT binary never does.
        // The previous heuristic ("any EventPipe event => CoreCLR") misclassified NativeAOT
        // because the runtime still emits provider manifests / metadata over EventPipe even when
        // SampleProfiler is a no-op.
        if (modules is { } sig)
        {
            if (sig.HasCoreClr)
            {
                return RuntimeFlavor.CoreClr;
            }

            if (sig.Inspected && !sig.HasCoreClr)
            {
                return RuntimeFlavor.NativeAot;
            }
        }

        // Fallbacks when we couldn't inspect loaded modules (permission, foreign OS, etc.):
        // SampleProfiler emitting samples is still a strong CoreCLR-only signal.
        if (probe.SampleEventsReceived > 0)
        {
            return RuntimeFlavor.CoreClr;
        }

        if (snapshot is null)
        {
            return RuntimeFlavor.Unknown;
        }

        return RuntimeFlavor.NativeAot;
    }

    private static string BuildNotes(
        RuntimeFlavor runtime,
        ProbeResult probe,
        bool perfAvailable,
        bool etwAvailable,
        bool canSampleOffCpu,
        PtraceProbeResult ptrace,
        bool canCollectThreadSnapshot,
        string? threadSnapshotSource,
        string? threadSnapshotPreconditions)
    {
        if (!probe.SessionStarted)
        {
            return $"Could not start EventPipe session ({probe.FailureReason}). " +
                   "Verify the process is .NET 6+ and that DOTNET_EnableDiagnostics is not 0.";
        }

        var primary = runtime switch
        {
            RuntimeFlavor.CoreClr => "CoreCLR runtime detected; all diagnostic tools available.",
            RuntimeFlavor.NativeAot when etwAvailable =>
                "NativeAOT detected (SampleProfiler emitted no events). CPU sampling is available via Windows ETW " +
                "kernel profiling (native symbols from PDB/export table). gcdump is not supported.",
            RuntimeFlavor.NativeAot when perfAvailable =>
                "NativeAOT detected (SampleProfiler emitted no events). CPU sampling is available via the Linux 'perf' fallback " +
                "(native symbols only, no managed IL handoff). gcdump is not supported.",
            RuntimeFlavor.NativeAot =>
                "NativeAOT detected (SampleProfiler emitted no events). " +
                "CPU sampling and gcdump are not available; counters, exceptions and EventSources still work. " +
                "On Windows, run as Administrator to enable ETW kernel profiling. " +
                "On Linux, install 'perf' with CAP_PERFMON to enable the native CPU sampling fallback.",
            _ => "Could not classify runtime flavor."
        };

        // Off-CPU availability is a property of the sidecar host (perf + CAP_PERFMON on Linux,
        // admin elevation on Windows), not of the target runtime — so we surface it as a
        // separate hint so the LLM can decide whether to call collect_off_cpu_sample before
        // committing to the (system-wide, privileged) capture.
        if (canSampleOffCpu)
        {
            primary += " collect_off_cpu_sample is available.";
        }
        else if (OperatingSystem.IsWindows())
        {
            primary += " collect_off_cpu_sample is NOT available: the sidecar lacks administrative " +
                       "elevation required for the NT Kernel Logger ContextSwitch provider. Run the " +
                       "diagnostics process as Administrator (or grant SeSystemProfilePrivilege).";
        }
        else if (OperatingSystem.IsLinux())
        {
            primary += " collect_off_cpu_sample is NOT available: 'perf' is missing from PATH or the " +
                       "sidecar lacks CAP_PERFMON (or perf_event_paranoid > -1). Install linux-tools-common " +
                       "/ linux-tools-generic and grant the capability.";
        }
        else
        {
            primary += " collect_off_cpu_sample is not supported on this OS in this release.";
        }

        // ClrMD live attach (collect_thread_snapshot, inspect_live_heap, inspect_dump on live
        // PID, collect_process_dump) is also a host-side capability. Tell the LLM up front
        // when the four tools will hard-fail with PermissionDenied so it can either route
        // around them (dump-based workflow) or relay a concrete mitigation to the user.
        if (!ptrace.CanAttach)
        {
            primary += $" ClrMD live attach tools (collect_thread_snapshot, inspect_live_heap, inspect_dump on live PID, collect_process_dump) are NOT available: {ptrace.Reason}";
        }

        if (canCollectThreadSnapshot && !string.IsNullOrWhiteSpace(threadSnapshotSource))
        {
            primary += $" collect_thread_snapshot is available via '{threadSnapshotSource}'.";
        }
        else if (!string.IsNullOrWhiteSpace(threadSnapshotPreconditions))
        {
            primary += $" collect_thread_snapshot is NOT available: {threadSnapshotPreconditions}";
        }

        return primary;
    }

    internal static (bool CanCollect, string? Source, string? Preconditions) EvaluateThreadSnapshotSupport(
        RuntimeFlavor runtime,
        PtraceProbeResult ptrace,
        bool euStackAvailable,
        bool canSampleOffCpu)
    {
        if (runtime == RuntimeFlavor.CoreClr)
        {
            return (
                CanCollect: ptrace.CanAttach,
                Source: ptrace.CanAttach ? "clrmd-thread-walk" : null,
                Preconditions: ptrace.CanAttach ? null : ptrace.Reason);
        }

        if (runtime == RuntimeFlavor.NativeAot && OperatingSystem.IsLinux())
        {
            if (euStackAvailable && ptrace.CanAttach)
            {
                return (
                    CanCollect: true,
                    Source: "linux-native-stack",
                    Preconditions: null);
            }

            if (canSampleOffCpu)
            {
                return (
                    CanCollect: true,
                    Source: "perf-replay-approx",
                    Preconditions: "Approximate last-seen stacks from a short perf sched_switch replay window (not point-in-time).");
            }

            return (
                CanCollect: false,
                Source: null,
                Preconditions: !euStackAvailable
                    ? "eu-stack (elfutils) is missing from PATH and perf replay is unavailable (perf/CAP_PERFMON missing)."
                    : ptrace.Reason);
        }

        if (runtime == RuntimeFlavor.NativeAot && OperatingSystem.IsWindows())
        {
            return (
                CanCollect: false,
                Source: null,
                Preconditions: "NativeAOT thread snapshot backend on Windows is tracked in issue #93.");
        }

        return (
            CanCollect: false,
            Source: null,
            Preconditions: "No thread snapshot backend is registered for this runtime/OS.");
    }

    private static bool IsEuStackAvailable()
    {
        if (!OperatingSystem.IsLinux()) return false;
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVar)) return false;

        foreach (var segment in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(segment, "eu-stack");
            if (File.Exists(candidate)) return true;
        }

        return false;
    }

    private sealed record ProbeResult(bool SessionStarted, long SampleEventsReceived, string? FailureReason);
}
