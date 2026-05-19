using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.CpuSampling;

/// <summary>
/// Windows-only CPU sampler that wraps the NT Kernel Logger ETW provider's profile
/// sampling (PerfInfo + StackWalk events). Used as the fallback for NativeAOT processes
/// on Windows where the managed <c>Microsoft-DotNETCore-SampleProfiler</c> EventSource
/// is not implemented.
/// </summary>
/// <remarks>
/// <para>
/// The ETW kernel session captures system-wide CPU profile interrupts and resolves stacks
/// via the image load events recorded during the session. After the sampling window, the
/// ETL trace is converted to ETLX via <see cref="TraceLog"/> which handles symbol resolution
/// against PDB files and the PE export table — giving meaningful method names for NativeAOT
/// binaries published with <c>&lt;StripSymbols&gt;false&lt;/StripSymbols&gt;</c> (or
/// separate PDB output).
/// </para>
/// <para>
/// Requirements (validated by <see cref="IsAvailable"/>): Windows host and administrative
/// elevation (or <c>SeSystemProfilePrivilege</c>). Kernel-level profiling is inherently
/// system-wide, so concurrent sampling sessions are serialized via a static semaphore to
/// avoid resource contention.
/// </para>
/// </remarks>
public sealed class EtwNativeAotCpuSampler : ICpuSampler
{
    private static readonly SemaphoreSlim s_etwGate = new(1, 1);
    private readonly ILogger<EtwNativeAotCpuSampler> _logger;

    public EtwNativeAotCpuSampler(ILogger<EtwNativeAotCpuSampler>? logger = null)
    {
        _logger = logger ?? NullLogger<EtwNativeAotCpuSampler>.Instance;
    }

    /// <summary>
    /// Returns true when the host can run this sampler. Checks the OS (Windows) and
    /// that the current process has administrative elevation required for kernel ETW sessions.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows")]
    public bool IsAvailable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _logger.LogTrace("ETW sampler not available: not running on Windows.");
            return false;
        }

        var elevated = TraceEventSession.IsElevated() == true;
        if (!elevated)
        {
            _logger.LogTrace("ETW sampler not available: process is not elevated.");
        }

        return elevated;
    }

    public async Task<CpuSampleResult> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN = 25,
        SourceResolutionOptions? sourceResolution = null,
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
                "ETW kernel profiling is not available. This requires Windows with administrative elevation " +
                "(or SeSystemProfilePrivilege). Run the diagnostics sidecar as Administrator to enable " +
                "native CPU sampling for NativeAOT processes.");
        }

        // Serialize ETW sessions to avoid system-wide contention.
        await s_etwGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CaptureAndProcessAsync(processId, duration, topN, sourceResolution, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            s_etwGate.Release();
        }
    }

    private async Task<CpuSampleResult> CaptureAndProcessAsync(
        int processId,
        TimeSpan duration,
        int topN,
        SourceResolutionOptions? sourceResolution,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var captureDir = Path.Combine(Path.GetTempPath(), $"diagmcp-etw-{processId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(captureDir);

        var sessionName = $"dotnet-diag-mcp-{processId}-{Guid.NewGuid():N}";
        var etlPath = Path.Combine(captureDir, "trace.etl");

        try
        {
            await CaptureEtwAsync(sessionName, etlPath, duration, cancellationToken).ConfigureAwait(false);
            return ProcessEtl(etlPath, processId, startedAt, duration, topN, sourceResolution);
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
                // Stop the session automatically after the duration + buffer.
                StopOnDispose = true,
            };

            // Enable kernel profile sampling with stack walk.
            // On Windows 8+ this works on user-mode sessions (non-kernel-logger).
            var keywords = KernelTraceEventParser.Keywords.Profile |
                           KernelTraceEventParser.Keywords.ImageLoad |
                           KernelTraceEventParser.Keywords.Process |
                           KernelTraceEventParser.Keywords.Thread;
            var stackKeywords = KernelTraceEventParser.Keywords.Profile;

            session.EnableKernelProvider(keywords, stackKeywords);
            _logger.LogDebug("ETW session '{Session}' started for pid {Pid}, capturing for {Duration}s.",
                sessionName, 0, duration.TotalSeconds);

            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ETW session '{Session}' failed.", sessionName);
            throw new InvalidOperationException(
                $"Failed to start or run ETW kernel profiling session. " +
                $"Ensure administrative elevation and that no conflicting kernel session is active. " +
                $"Details: {ex.Message}", ex);
        }
        finally
        {
            try { session?.Stop(); }
            catch (Exception ex) { _logger.LogDebug(ex, "ETW session stop failed (best effort)."); }
            session?.Dispose();
        }
    }

    private static CpuSampleResult ProcessEtl(
        string etlPath,
        int processId,
        DateTimeOffset startedAt,
        TimeSpan duration,
        int topN,
        SourceResolutionOptions? sourceResolution)
    {
        if (!File.Exists(etlPath))
        {
            throw new InvalidOperationException("ETW capture produced no output file.");
        }

        // Determine symbol path: include the target executable directory + standard paths.
        var symbolPath = BuildSymbolPath(processId, sourceResolution);

        // Convert ETL → ETLX. Disable remote symbol resolution during conversion
        // to avoid network hangs. We resolve symbols locally afterward via LookupSymbolsForModule.
        var options = new TraceLogOptions
        {
            LocalSymbolsOnly = true,
            ShouldResolveSymbols = _ => false,
        };

        var etlxPath = TraceLog.CreateFromEventTraceLogFile(etlPath, null, options);

        try
        {
            using var traceLog = TraceLog.OpenOrConvert(etlxPath);
            return AggregateFromTraceLog(traceLog, processId, startedAt, duration, topN, symbolPath);
        }
        finally
        {
            TryDelete(etlxPath);
        }
    }

    private static CpuSampleResult AggregateFromTraceLog(
        TraceLog traceLog,
        int processId,
        DateTimeOffset startedAt,
        TimeSpan duration,
        int topN,
        string? symbolPath)
    {
        var inclusive = new Dictionary<string, long>(StringComparer.Ordinal);
        var exclusive = new Dictionary<string, long>(StringComparer.Ordinal);
        var modules = new Dictionary<string, string>(StringComparer.Ordinal);
        var builder = new CallTreeBuilder();
        long total = 0;

        // Resolve symbols for modules loaded in our target process.
        if (symbolPath is not null)
        {
            using var symbolReader = new SymbolReader(TextWriter.Null, symbolPath);
            foreach (var process in traceLog.Processes)
            {
                if (process.ProcessID != processId) continue;
                foreach (var module in process.LoadedModules)
                {
                    traceLog.CodeAddresses.LookupSymbolsForModule(symbolReader, module.ModuleFile);
                }
            }
        }

        var events = traceLog.Events
            .Where(e => e is SampledProfileTraceData && e.ProcessID == processId);

        foreach (var ev in events)
        {
            var stack = ev.CallStack();
            if (stack is null) continue;

            // Walk the stack: TraceLog stacks are leaf→root (callee→caller).
            var frames = new List<(string Key, string Module, string Display)>();
            var current = stack;
            while (current is not null)
            {
                var moduleName = current.CodeAddress?.ModuleFile?.Name ?? string.Empty;
                var methodName = ResolveMethodName(current);
                var key = string.IsNullOrEmpty(moduleName) ? methodName : moduleName + "!" + methodName;
                frames.Add((key, moduleName, methodName));
                modules.TryAdd(key, moduleName);
                current = current.Caller;
            }

            if (frames.Count == 0) continue;
            total++;

            // frames is leaf→root; reverse to root→leaf for CallTreeBuilder.
            frames.Reverse();

            var leafKey = frames[^1].Key;
            exclusive[leafKey] = exclusive.GetValueOrDefault(leafKey) + 1;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (k, _, _) in frames)
            {
                if (seen.Add(k))
                {
                    inclusive[k] = inclusive.GetValueOrDefault(k) + 1;
                }
            }

            builder.AddStack(frames, leafKey);
        }

        var ranked = inclusive
            .OrderByDescending(kv => kv.Value)
            .Take(topN)
            .ToArray();

        var hotspots = ranked
            .Select(kv =>
            {
                var module = modules.GetValueOrDefault(kv.Key, string.Empty);
                var display = !string.IsNullOrEmpty(module) && kv.Key.StartsWith(module + "!", StringComparison.Ordinal)
                    ? kv.Key[(module.Length + 1)..]
                    : kv.Key;
                return new Hotspot(
                    Frame: new SampledFrame(Module: module, Method: display),
                    InclusiveSamples: kv.Value,
                    ExclusiveSamples: exclusive.GetValueOrDefault(kv.Key),
                    Identity: null);
            })
            .ToList();

        var root = builder.Build();
        var symbolSource = total > 0
            ? NativeAotSymbolDemangler.SymbolSource.ElfDemangled // Closest equivalent for "PDB-resolved"
            : NativeAotSymbolDemangler.SymbolSource.Unknown;

        var summary = new CpuSample(processId, startedAt, duration, total, hotspots);
        var artifact = new CpuSampleTraceArtifact(processId, startedAt, duration, total, root, null, null, symbolSource);
        return new CpuSampleResult(summary, artifact);
    }

    private static string ResolveMethodName(TraceCallStack frame)
    {
        var codeAddress = frame.CodeAddress;
        if (codeAddress is null) return "[unknown]";

        // Try full symbol resolution first.
        var fullName = codeAddress.FullMethodName;
        if (!string.IsNullOrEmpty(fullName) && fullName != "?")
        {
            return fullName;
        }

        // Fall back to module+offset.
        var modFile = codeAddress.ModuleFile;
        if (modFile is not null)
        {
            return $"0x{codeAddress.Address:X}";
        }

        return $"[0x{codeAddress.Address:X}]";
    }

    private static string? BuildSymbolPath(int processId, SourceResolutionOptions? sourceResolution)
    {
        var parts = new List<string>();

        // Include the target process's module directory (where PDBs likely live).
        try
        {
            var proc = Process.GetProcessById(processId);
            var mainModule = proc.MainModule?.FileName;
            if (mainModule is not null)
            {
                var dir = Path.GetDirectoryName(mainModule);
                if (dir is not null)
                {
                    parts.Add(dir);
                }
            }
        }
        catch
        {
            // Process may have exited or access denied — best effort.
        }

        // Respect user-provided symbol path.
        if (sourceResolution?.SymbolPath is not null)
        {
            parts.Add(sourceResolution.SymbolPath);
        }

        // Respect _NT_SYMBOL_PATH environment variable.
        var ntSymPath = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        if (!string.IsNullOrEmpty(ntSymPath))
        {
            parts.Add(ntSymPath);
        }

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
