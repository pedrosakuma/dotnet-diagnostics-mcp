using System.Runtime.InteropServices;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Memory;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.OffCpu;

/// <summary>
/// Windows off-CPU sampler driven by the NT Kernel Logger's <c>ContextSwitch</c> +
/// <c>DispatcherReadyThread</c> tracepoints with stack walks enabled. For every CSwitch we observe
/// two attributable events: an OUT for the outgoing thread (with its blocking stack — captured by
/// the ETW kernel-mode stackwalker on the switch-out path) and an IN for the incoming thread.
/// Per-thread pending-out tracking closes each off-CPU span exactly the way
/// <see cref="PerfSchedOffCpuSampler"/> closes pairs on Linux, so the resulting
/// <see cref="OffCpuSnapshotArtifact"/> is platform-agnostic and the
/// <c>query_off_cpu_snapshot</c> drilldown does not need a Windows branch.
/// </summary>
/// <remarks>
/// <para>
/// Requirements (validated by <see cref="IsAvailable"/>): Windows host with administrative elevation
/// (or <c>SeSystemProfilePrivilege</c>). Kernel ETW sessions are inherently system-wide so concurrent
/// captures are serialized through a static gate to keep buffer pressure predictable.
/// </para>
/// <para>
/// The stack we attribute to an off-CPU span is the stack captured at the CSwitch OUT event — i.e.
/// the call chain at the point the scheduler put the thread to sleep. The <c>WaitReason</c>
/// reported by the kernel (e.g. <c>UserRequest</c>, <c>WrLpcReceive</c>, <c>WrQueue</c>) is
/// propagated as the per-span <c>PrevState</c>, mirroring the perf <c>S/D/I</c> state characters.
/// Pending OUTs that never paired with an IN before the capture window ended are emitted as
/// <see cref="OffCpuSpan.IsCensored"/> spans with a lower-bound duration, matching the Linux
/// backend's flush behaviour so the LLM sees uniform "long blocker" attribution on both OSes.
/// </para>
/// <para>
/// Symbol resolution uses <see cref="SymbolReader"/> with the target process's main module
/// directory plus <c>_NT_SYMBOL_PATH</c>; managed↔kernel stack merging lands in slice 2c together
/// with the <c>depth</c> parameter, so for now we report user-mode frames as <c>module!method</c>
/// (or <c>module!0xADDR</c> when PDBs are missing) and kernel frames as <c>ntoskrnl!Function</c>
/// when symbols are available.
/// </para>
/// </remarks>
public sealed class EtwOffCpuSampler : IOffCpuSampler
{
    // Serialize concurrent kernel ETW sessions — they share the global NT Kernel Logger slot
    // and overlapping sessions cause buffer starvation / start failures.
    private static readonly SemaphoreSlim s_etwGate = new(1, 1);
    private readonly ILogger<EtwOffCpuSampler> _logger;
    private readonly MvidReader _mvidReader;

    public EtwOffCpuSampler(
        ILogger<EtwOffCpuSampler>? logger = null,
        MvidReader? mvidReader = null)
    {
        _logger = logger ?? NullLogger<EtwOffCpuSampler>.Instance;
        _mvidReader = mvidReader ?? new MvidReader();
    }

    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows")]
    public bool IsAvailable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _logger.LogTrace("ETW off-CPU sampler not available: not running on Windows.");
            return false;
        }

        var elevated = TraceEventSession.IsElevated() == true;
        if (!elevated)
        {
            _logger.LogTrace("ETW off-CPU sampler not available: process is not elevated.");
        }
        return elevated;
    }

    public async Task<OffCpuSampleResult> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN = 25,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be (0, 5min].");
        }
        if (topN <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(topN), "topN must be positive.");
        }
        if (!IsAvailable())
        {
            throw new InvalidOperationException(
                "ETW kernel CSwitch profiling is not available. This requires Windows with administrative " +
                "elevation (or SeSystemProfilePrivilege). Run the diagnostics sidecar as Administrator to " +
                "enable off-CPU sampling via context-switch tracing.");
        }

        await s_etwGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CaptureAndProcessAsync(processId, duration, topN, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            s_etwGate.Release();
        }
    }

    private async Task<OffCpuSampleResult> CaptureAndProcessAsync(
        int processId,
        TimeSpan duration,
        int topN,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var captureDir = Path.Combine(Path.GetTempPath(), $"diagmcp-etw-offcpu-{processId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(captureDir);

        var sessionName = $"dotnet-diag-mcp-offcpu-{processId}-{Guid.NewGuid():N}";
        var etlPath = Path.Combine(captureDir, "trace.etl");
        var notes = new List<string>();

        try
        {
            await CaptureEtwAsync(sessionName, etlPath, duration, cancellationToken).ConfigureAwait(false);
            return ProcessEtl(etlPath, processId, startedAt, duration, topN, notes, _mvidReader);
        }
        finally
        {
            TryDeleteDirectory(captureDir);
        }
    }

    private async Task CaptureEtwAsync(
        string sessionName,
        string etlPath,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        TraceEventSession? session = null;
        try
        {
            session = new TraceEventSession(sessionName, etlPath)
            {
                StopOnDispose = true,
            };

            // ContextSwitch: the primary tracepoint — fires once per OS scheduler switch with both
            //   OldThread/NewThread identities plus the OS-level wait reason on the OUT side.
            // Dispatcher: surfaces ReadyThread events (who woke the blocked thread); we already
            //   turn it on now so the ETL is forward-compatible for slice 2c without re-capture.
            // ImageLoad/Process/Thread: required for module → symbol resolution and
            //   thread-name population by the TraceLog conversion.
            var keywords = KernelTraceEventParser.Keywords.ContextSwitch |
                           KernelTraceEventParser.Keywords.Dispatcher |
                           KernelTraceEventParser.Keywords.ImageLoad |
                           KernelTraceEventParser.Keywords.Process |
                           KernelTraceEventParser.Keywords.Thread;
            // Walk stacks specifically on ContextSwitch: stack captured at switch-out time IS the
            // blocking call chain we want to surface.
            var stackKeywords = KernelTraceEventParser.Keywords.ContextSwitch;

            session.EnableKernelProvider(keywords, stackKeywords);
            _logger.LogDebug("ETW off-CPU session '{Session}' started, capturing for {Duration}s.",
                sessionName, duration.TotalSeconds);

            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ETW off-CPU session '{Session}' failed.", sessionName);
            throw new InvalidOperationException(
                $"Failed to start or run ETW kernel CSwitch session. Ensure admin elevation and that " +
                $"no conflicting kernel session is active. Details: {ex.Message}", ex);
        }
        finally
        {
            try { session?.Stop(); }
            catch (Exception ex) { _logger.LogDebug(ex, "ETW off-CPU session stop failed (best effort)."); }
            session?.Dispose();
        }
    }

    private static OffCpuSampleResult ProcessEtl(
        string etlPath,
        int processId,
        DateTimeOffset startedAt,
        TimeSpan duration,
        int topN,
        List<string> notes,
        MvidReader mvidReader)
    {
        if (!File.Exists(etlPath))
        {
            throw new InvalidOperationException("ETW capture produced no output file.");
        }

        var symbolPath = BuildSymbolPath(processId);
        var options = new TraceLogOptions
        {
            LocalSymbolsOnly = true,
            ShouldResolveSymbols = _ => false,
        };
        var etlxPath = TraceLog.CreateFromEventTraceLogFile(etlPath, null, options);

        try
        {
            using var traceLog = TraceLog.OpenOrConvert(etlxPath);
            return AggregateFromTraceLog(traceLog, processId, startedAt, duration, topN, symbolPath, notes, mvidReader);
        }
        finally
        {
            TryDelete(etlxPath);
        }
    }

    private static OffCpuSampleResult AggregateFromTraceLog(
        TraceLog traceLog,
        int processId,
        DateTimeOffset startedAt,
        TimeSpan duration,
        int topN,
        string? symbolPath,
        List<string> notes,
        MvidReader mvidReader)
    {
        if (symbolPath is not null)
        {
            try
            {
                using var symbolReader = new SymbolReader(TextWriter.Null, symbolPath);
                foreach (var process in traceLog.Processes)
                {
                    if (process.ProcessID != processId) continue;
                    foreach (var module in process.LoadedModules)
                    {
                        try { traceLog.CodeAddresses.LookupSymbolsForModule(symbolReader, module.ModuleFile); }
                        catch { /* best effort per module */ }
                    }
                }
            }
            catch { /* best effort */ }
        }

        // Per-TID pending OUT awaiting a matching IN. Tracking by kernel TID (not process) so
        // a thread that briefly migrates between cores is still attributed to one off-CPU span.
        var pending = new Dictionary<int, (double Ts, string State, List<OffCpuFrame> Stack, string Comm)>();
        var spans = new List<OffCpuSpan>();
        long switches = 0;
        double maxTs = double.MinValue;

        foreach (var ev in traceLog.Events)
        {
            if (ev is not CSwitchTraceData cs) continue;
            var ts = cs.TimeStampRelativeMSec / 1000.0;
            if (ts > maxTs) maxTs = ts;

            // OUT side: the thread leaving the CPU belongs to the target.
            if (cs.OldProcessID == processId)
            {
                switches++;
                var stack = ExtractStack(cs.CallStack(), mvidReader);
                pending[cs.OldThreadID] = (
                    Ts: ts,
                    State: cs.OldThreadWaitReason.ToString(),
                    Stack: stack,
                    Comm: SafeProcessName(cs.OldProcessName));
            }

            // IN side: the thread coming on CPU belongs to the target — close the matching OUT.
            if (cs.NewProcessID == processId)
            {
                if (pending.Remove(cs.NewThreadID, out var p))
                {
                    var micros = (long)Math.Round((ts - p.Ts) * 1_000_000.0);
                    if (micros > 0)
                    {
                        spans.Add(new OffCpuSpan(
                            Tid: cs.NewThreadID,
                            Comm: p.Comm,
                            DurationMicros: micros,
                            PrevState: p.State,
                            BlockingStack: p.Stack));
                    }
                }
            }
        }

        // Flush any still-pending OUTs as censored spans (long blockers that outlived the window).
        if (maxTs > double.MinValue)
        {
            foreach (var kv in pending)
            {
                var micros = (long)Math.Round((maxTs - kv.Value.Ts) * 1_000_000.0);
                if (micros > 0)
                {
                    spans.Add(new OffCpuSpan(
                        Tid: kv.Key,
                        Comm: kv.Value.Comm,
                        DurationMicros: micros,
                        PrevState: kv.Value.State,
                        BlockingStack: kv.Value.Stack,
                        IsCensored: true));
                }
            }
        }

        return OffCpuAggregator.Aggregate(
            processId,
            startedAt,
            duration,
            spans,
            switches,
            topN,
            symbolSource: "etw-cswitch-pdb",
            notes: notes.Count > 0 ? notes : null);
    }

    private static List<OffCpuFrame> ExtractStack(TraceCallStack? stack, MvidReader mvidReader)
    {
        // TraceLog stacks are leaf→root (Caller chains to parent). The aggregator reverses to
        // root→leaf so we keep TraceLog's order here to match perf's leaf-first convention.
        var frames = new List<OffCpuFrame>();
        var current = stack;
        var depth = 0;
        while (current is not null && depth < 256)
        {
            var ca = current.CodeAddress;
            var module = ca?.ModuleFile?.Name ?? string.Empty;
            var method = ResolveMethodName(ca);
            var identity = TryBuildIdentity(ca, module, mvidReader);
            frames.Add(new OffCpuFrame(module, method, identity));
            current = current.Caller;
            depth++;
        }
        return frames;
    }

    private static string ResolveMethodName(TraceCodeAddress? ca)
    {
        if (ca is null) return "[unknown]";
        var name = ca.FullMethodName;
        if (!string.IsNullOrEmpty(name) && name != "?") return name;
        return $"0x{ca.Address:X}";
    }

    /// <summary>
    /// Builds a <see cref="MethodIdentity"/> handoff payload from a TraceLog
    /// <see cref="TraceCodeAddress"/> when it points to a managed method. Mirrors
    /// <see cref="EventPipeCpuSampler"/>'s extraction so on-CPU and off-CPU hotspots
    /// hand off identical shapes to <c>dotnet-assembly-mcp</c>. Returns <c>null</c> for
    /// native / kernel frames and for managed frames missing both an MVID-readable
    /// module path and a metadata token (nothing useful to hand off).
    /// </summary>
    private static MethodIdentity? TryBuildIdentity(TraceCodeAddress? ca, string moduleNameFallback, MvidReader mvidReader)
    {
        if (ca is null) return null;
        var method = ca.Method;
        if (method is null) return null;

        var moduleFile = method.MethodModuleFile;
        var modulePath = moduleFile?.FilePath;
        var moduleName = !string.IsNullOrEmpty(modulePath)
            ? Path.GetFileName(modulePath)
            : (moduleFile?.Name is { Length: > 0 } n ? n : moduleNameFallback);

        var token = method.MethodToken;
        var mvid = mvidReader.TryRead(modulePath);

        // Skip frames where we have nothing useful for the handoff (native / unresolved JIT).
        if (mvid is null && token == 0 && string.IsNullOrEmpty(modulePath) && string.IsNullOrEmpty(moduleName))
        {
            return null;
        }

        var parsed = EventPipeCpuSampler.ParseFullMethodName(method.FullMethodName);
        return new MethodIdentity(
            ModuleName: moduleName,
            ModulePath: modulePath,
            ModuleVersionId: mvid,
            MetadataToken: token > 0 ? token : null,
            TypeFullName: parsed.TypeFullName,
            MethodName: parsed.MethodName,
            GenericArity: parsed.GenericArity)
        {
            GenericTypeArguments = parsed.GenericTypeArguments,
        };
    }

    private static string SafeProcessName(string? name)
        => string.IsNullOrEmpty(name) ? string.Empty : name!;

    private static string? BuildSymbolPath(int processId)
    {
        var parts = new List<string>();
        try
        {
            var proc = System.Diagnostics.Process.GetProcessById(processId);
            var mainModule = proc.MainModule?.FileName;
            if (mainModule is not null)
            {
                var dir = Path.GetDirectoryName(mainModule);
                if (dir is not null) parts.Add(dir);
            }
        }
        catch { /* best effort */ }

        var ntSym = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        if (!string.IsNullOrEmpty(ntSym)) parts.Add(ntSym);
        return parts.Count > 0 ? string.Join(";", parts) : null;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}
