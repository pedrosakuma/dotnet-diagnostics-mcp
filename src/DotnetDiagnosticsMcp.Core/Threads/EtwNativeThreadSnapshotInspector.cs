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

namespace DotnetDiagnosticsMcp.Core.Threads;

/// <summary>
/// Windows-only NativeAOT thread snapshot backend backed by a short ETW kernel capture.
/// Collects one native stack per OS thread and maps each TID to both ManagedThreadId and OSThreadId
/// so drilldown by thread id works consistently for native targets.
/// </summary>
public sealed class EtwNativeThreadSnapshotInspector : IThreadSnapshotInspector
{
    private static readonly SemaphoreSlim s_etwGate = new(1, 1);
    private static readonly TimeSpan CaptureDuration = TimeSpan.FromMilliseconds(200);
    private readonly ILogger<EtwNativeThreadSnapshotInspector> _logger;

    public EtwNativeThreadSnapshotInspector(ILogger<EtwNativeThreadSnapshotInspector>? logger = null)
    {
        _logger = logger ?? NullLogger<EtwNativeThreadSnapshotInspector>.Instance;
    }

    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows")]
    public bool IsAvailable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _logger.LogTrace("ETW native thread snapshot inspector not available: not running on Windows.");
            return false;
        }

        var elevated = TraceEventSession.IsElevated() == true;
        if (!elevated)
        {
            _logger.LogTrace("ETW native thread snapshot inspector not available: process is not elevated.");
        }

        return elevated;
    }

    public async Task<ThreadSnapshotArtifact> InspectLiveAsync(
        int processId,
        ThreadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId), "Process id must be positive.");
        }

        if (!IsAvailable())
        {
            throw new UnauthorizedAccessException(
                "ETW native thread snapshots are not available. This requires Windows with administrative elevation " +
                "(or SeSystemProfilePrivilege). Run the diagnostics sidecar as Administrator.");
        }

        var opts = options ?? new ThreadSnapshotOptions();
        Validate(opts);

        await s_etwGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CaptureAndProcessAsync(processId, opts, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            s_etwGate.Release();
        }
    }

    public Task<ThreadSnapshotArtifact> InspectDumpAsync(
        string dumpFilePath,
        ThreadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "EtwNativeThreadSnapshotInspector only supports live-process snapshots. Dump snapshots use ClrMdThreadSnapshotInspector.");

    private static void Validate(ThreadSnapshotOptions opts)
    {
        if (opts.MaxFramesPerThread <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(opts), "MaxFramesPerThread must be positive.");
        }
        if (opts.MaxFramesPerThread > ClrMdThreadSnapshotInspector.MaxFramesPerThreadHardCap)
        {
            throw new ArgumentOutOfRangeException(nameof(opts),
                $"MaxFramesPerThread must be <= {ClrMdThreadSnapshotInspector.MaxFramesPerThreadHardCap}.");
        }
    }

    private async Task<ThreadSnapshotArtifact> CaptureAndProcessAsync(
        int processId,
        ThreadSnapshotOptions options,
        CancellationToken cancellationToken)
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var captureDir = Path.Combine(Path.GetTempPath(), $"diagmcp-etw-threads-{processId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(captureDir);

        var sessionName = $"dotnet-diag-mcp-threads-{processId}-{Guid.NewGuid():N}";
        var etlPath = Path.Combine(captureDir, "trace.etl");
        var warnings = new List<string>();

        try
        {
            // ETW kernel sessions start emitting thread/profile events immediately, unlike EventPipe
            // sessions that usually need a longer warmup window.
            await CaptureEtwAsync(sessionName, etlPath, CaptureDuration, cancellationToken).ConfigureAwait(false);
            var threads = ProcessEtl(etlPath, processId, options.MaxFramesPerThread, warnings);
            stopwatch.Stop();

            return new ThreadSnapshotArtifact(
                Origin: ThreadSnapshotOrigin.Live,
                ProcessId: processId,
                CapturedAt: capturedAt,
                WalkDuration: stopwatch.Elapsed,
                RuntimeName: "NativeAot",
                RuntimeVersion: string.Empty,
                Threads: threads,
                Locks: Array.Empty<MonitorLockState>())
            {
                Source = "etw-native-stack",
                Warnings = warnings.Count > 0 ? warnings : null,
            };
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

            var keywords = KernelTraceEventParser.Keywords.Thread |
                           KernelTraceEventParser.Keywords.ImageLoad |
                           KernelTraceEventParser.Keywords.Profile |
                           KernelTraceEventParser.Keywords.Process;
            var stackKeywords = KernelTraceEventParser.Keywords.Thread;

            session.EnableKernelProvider(keywords, stackKeywords);
            _logger.LogDebug(
                "ETW native thread snapshot session '{Session}' started, capturing for {DurationMs}ms.",
                sessionName,
                duration.TotalMilliseconds);

            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ETW native thread snapshot session '{Session}' failed.", sessionName);
            throw new InvalidOperationException(
                $"Failed to start or run ETW kernel thread snapshot session. Ensure administrative elevation " +
                $"and no conflicting kernel session. Details: {ex.Message}", ex);
        }
        finally
        {
            try { session?.Stop(); }
            catch (Exception ex) { _logger.LogDebug(ex, "ETW native thread snapshot session stop failed (best effort)."); }
            session?.Dispose();
        }
    }

    private static List<ManagedThread> ProcessEtl(
        string etlPath,
        int processId,
        int maxFramesPerThread,
        List<string> warnings)
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
            return BuildThreads(traceLog, processId, maxFramesPerThread, symbolPath, warnings);
        }
        finally
        {
            TryDelete(etlxPath);
        }
    }

    private static List<ManagedThread> BuildThreads(
        TraceLog traceLog,
        int processId,
        int maxFramesPerThread,
        string? symbolPath,
        List<string> warnings)
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
                        traceLog.CodeAddresses.LookupSymbolsForModule(symbolReader, module.ModuleFile);
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Symbol lookup failed: {ex.GetType().Name}.");
            }
        }

        var stackByTid = new Dictionary<int, List<ManagedStackFrame>>();
        foreach (var ev in traceLog.Events)
        {
            if (ev.ProcessID != processId || ev.ThreadID <= 0)
            {
                continue;
            }

            if (stackByTid.ContainsKey(ev.ThreadID))
            {
                continue;
            }

            var stack = ev.CallStack();
            if (stack is null)
            {
                continue;
            }

            var frames = ExtractFrames(stack, maxFramesPerThread);
            if (frames.Count == 0)
            {
                continue;
            }

            stackByTid.Add(ev.ThreadID, frames);
        }

        var threadStateByTid = ReadProcessThreadStates(processId, warnings);
        var allThreadIds = threadStateByTid.Keys
            .Concat(stackByTid.Keys)
            .Distinct()
            .OrderBy(t => t)
            .ToArray();

        var managedThreads = new List<ManagedThread>(allThreadIds.Length);
        foreach (var tid in allThreadIds)
        {
            var frames = stackByTid.GetValueOrDefault(tid) ?? new List<ManagedStackFrame>();
            if (frames.Count == 0)
            {
                warnings.Add($"No ETW stack captured for tid {tid} during the snapshot window.");
            }

            if (!threadStateByTid.ContainsKey(tid))
            {
                warnings.Add($"Could not read live thread state for tid {tid}; it may have exited.");
            }

            var state = threadStateByTid.GetValueOrDefault(tid) ?? new ProcessThreadStateInfo("Unknown", false, null);
            managedThreads.Add(new ManagedThread(
                ManagedThreadId: tid,
                OSThreadId: unchecked((uint)tid),
                Address: 0,
                State: state.State,
                IsAlive: state.IsAlive,
                IsBackground: false,
                IsFinalizer: false,
                IsGc: false,
                IsThreadpoolWorker: false,
                LockCount: 0,
                CurrentExceptionType: null,
                TopFrameMethod: frames.Count > 0 ? frames[0].DisplayName : null,
                Frames: frames)
            {
                IsLikelyBlocked = !string.IsNullOrWhiteSpace(state.WaitReason),
                InferredWaitReason = state.WaitReason,
            });
        }

        return managedThreads;
    }

    private static Dictionary<int, ProcessThreadStateInfo> ReadProcessThreadStates(int processId, List<string> warnings)
    {
        var result = new Dictionary<int, ProcessThreadStateInfo>();
        try
        {
            using var process = Process.GetProcessById(processId);
            foreach (ProcessThread thread in process.Threads)
            {
                string state = "Unknown";
                bool isAlive = true;
                string? waitReason = null;

                try
                {
                    state = thread.ThreadState.ToString();
                    if (thread.ThreadState == System.Diagnostics.ThreadState.Terminated)
                    {
                        isAlive = false;
                    }

                    if (thread.ThreadState == System.Diagnostics.ThreadState.Wait)
                    {
                        waitReason = thread.WaitReason.ToString();
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not read state for tid {thread.Id}: {ex.GetType().Name}.");
                }

                result[thread.Id] = new ProcessThreadStateInfo(state, isAlive, waitReason);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not enumerate process threads for pid {processId}: {ex.GetType().Name}.");
        }

        return result;
    }

    private static List<ManagedStackFrame> ExtractFrames(TraceCallStack stack, int maxFramesPerThread)
    {
        var frames = new List<ManagedStackFrame>(Math.Min(maxFramesPerThread, 64));
        var current = stack;
        while (current is not null && frames.Count < maxFramesPerThread)
        {
            var codeAddress = current.CodeAddress;
            if (codeAddress is not null)
            {
                var moduleName = codeAddress.ModuleFile?.Name;
                var display = ResolveMethodName(codeAddress);
                frames.Add(new ManagedStackFrame(
                    Kind: "Native",
                    DisplayName: display,
                    TypeFullName: null,
                    ModuleName: moduleName,
                    InstructionPointer: codeAddress.Address,
                    StackPointer: 0,
                    Identity: null));
            }

            current = current.Caller;
        }

        return frames;
    }

    private static string ResolveMethodName(TraceCodeAddress codeAddress)
    {
        var fullName = codeAddress.FullMethodName;
        if (!string.IsNullOrEmpty(fullName) && fullName != "?")
        {
            return fullName;
        }

        return $"0x{codeAddress.Address:X}";
    }

    private static string? BuildSymbolPath(int processId)
    {
        var parts = new List<string>();

        try
        {
            using var proc = Process.GetProcessById(processId);
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
            // best effort
        }

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

    private sealed record ProcessThreadStateInfo(string State, bool IsAlive, string? WaitReason);
}
