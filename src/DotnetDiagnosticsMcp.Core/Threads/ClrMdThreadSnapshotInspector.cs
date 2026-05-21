using System.Collections.Concurrent;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DotnetDiagnosticsMcp.Core.Memory;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.Threads;

/// <summary>
/// ClrMD-backed implementation of <see cref="IThreadSnapshotInspector"/>. Walks managed threads
/// + sync blocks of a process (live or from a dump) and produces a single
/// <see cref="ThreadSnapshotArtifact"/> suitable for registration in the drilldown handle store.
/// </summary>
public sealed class ClrMdThreadSnapshotInspector : IThreadSnapshotInspector
{
    private readonly ILogger<ClrMdThreadSnapshotInspector> _logger;
    private readonly ConcurrentDictionary<string, Guid?> _mvidCache = new(StringComparer.Ordinal);

    /// <summary>Hard upper bound on captured frames per thread regardless of the caller's request.
    /// Bounds the live-attach suspend window + allocation footprint (review wave-2 finding).</summary>
    public const int MaxFramesPerThreadHardCap = 512;

    public ClrMdThreadSnapshotInspector(ILogger<ClrMdThreadSnapshotInspector>? logger = null)
    {
        _logger = logger ?? NullLogger<ClrMdThreadSnapshotInspector>.Instance;
    }

    public Task<ThreadSnapshotArtifact> InspectLiveAsync(
        int processId,
        ThreadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId), "Process id must be positive.");
        }
        var opts = options ?? new ThreadSnapshotOptions();
        Validate(opts);
        return Task.Run(() => CaptureLive(processId, opts, cancellationToken), cancellationToken);
    }

    public Task<ThreadSnapshotArtifact> InspectDumpAsync(
        string dumpFilePath,
        ThreadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(dumpFilePath);
        if (!File.Exists(dumpFilePath)) throw new FileNotFoundException("Dump file not found.", dumpFilePath);
        var opts = options ?? new ThreadSnapshotOptions();
        Validate(opts);
        return Task.Run(() => CaptureDump(dumpFilePath, opts, cancellationToken), cancellationToken);
    }

    private static void Validate(ThreadSnapshotOptions opts)
    {
        if (opts.MaxFramesPerThread <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(opts), "MaxFramesPerThread must be positive.");
        }
        if (opts.MaxFramesPerThread > MaxFramesPerThreadHardCap)
        {
            throw new ArgumentOutOfRangeException(nameof(opts), $"MaxFramesPerThread must be <= {MaxFramesPerThreadHardCap} (bounds the live-attach suspend window).");
        }
    }

    private ThreadSnapshotArtifact CaptureLive(int processId, ThreadSnapshotOptions opts, CancellationToken ct)
    {
        var warnings = new List<string>();
        var capturedAt = DateTimeOffset.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var target = DataTarget.AttachToProcess(processId, suspend: true);
        var clrInfo = target.ClrVersions.FirstOrDefault()
            ?? throw new InvalidOperationException($"Process {processId} does not expose a CLR runtime (NativeAOT or non-managed).");
        using var runtime = clrInfo.CreateRuntime();
        var (threads, locks) = Capture(runtime, opts, warnings, ct);
        sw.Stop();
        return new ThreadSnapshotArtifact(
            Origin: ThreadSnapshotOrigin.Live,
            ProcessId: processId,
            CapturedAt: capturedAt,
            WalkDuration: sw.Elapsed,
            RuntimeName: clrInfo.Flavor.ToString(),
            RuntimeVersion: clrInfo.Version.ToString(),
            Threads: threads,
            Locks: locks)
        {
            Source = "clrmd-thread-walk",
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    private ThreadSnapshotArtifact CaptureDump(string dumpFilePath, ThreadSnapshotOptions opts, CancellationToken ct)
    {
        var fileInfo = new FileInfo(dumpFilePath);
        var warnings = new List<string>();
        var capturedAt = DateTimeOffset.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var target = DataTarget.LoadDump(dumpFilePath);
        var clrInfo = target.ClrVersions.FirstOrDefault()
            ?? throw new InvalidOperationException("Dump does not contain a CLR runtime.");
        using var runtime = clrInfo.CreateRuntime();
        var pidFromDump = unchecked((int)target.DataReader.ProcessId);
        var (threads, locks) = Capture(runtime, opts, warnings, ct);
        sw.Stop();
        return new ThreadSnapshotArtifact(
            Origin: ThreadSnapshotOrigin.Dump,
            ProcessId: pidFromDump,
            CapturedAt: capturedAt,
            WalkDuration: sw.Elapsed,
            RuntimeName: clrInfo.Flavor.ToString(),
            RuntimeVersion: clrInfo.Version.ToString(),
            Threads: threads,
            Locks: locks)
        {
            Source = "clrmd-thread-walk",
            DumpFilePath = dumpFilePath,
            DumpFileSizeBytes = fileInfo.Length,
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    private (IReadOnlyList<ManagedThread> Threads, IReadOnlyList<MonitorLockState> Locks) Capture(
        ClrRuntime runtime, ThreadSnapshotOptions opts, List<string> warnings, CancellationToken ct)
    {
        var threads = new List<ManagedThread>(runtime.Threads.Length);
        var threadByAddress = new Dictionary<ulong, ClrThread>();

        foreach (var t in runtime.Threads)
        {
            ct.ThrowIfCancellationRequested();
            threadByAddress[t.Address] = t;

            List<ManagedStackFrame> frames;
            try
            {
                frames = WalkStack(t, opts);
            }
            catch (Exception ex)
            {
                warnings.Add($"Stack walk for managed thread {t.ManagedThreadId} (OS {t.OSThreadId}) aborted: {ex.GetType().Name}.");
                frames = new List<ManagedStackFrame>();
            }

            var topFrame = frames.Count > 0 ? frames[0] : null;
            var (likelyBlocked, waitReason) = ClassifyTopFrame(topFrame);

            threads.Add(new ManagedThread(
                ManagedThreadId: t.ManagedThreadId,
                OSThreadId: t.OSThreadId,
                Address: t.Address,
                State: t.State.ToString(),
                IsAlive: t.IsAlive,
                IsBackground: HasFlag(t.State, "Background"),
                IsFinalizer: t.IsFinalizer,
                IsGc: t.IsGc,
                IsThreadpoolWorker: HasFlag(t.State, "ThreadpoolWorker") || HasFlag(t.State, "TPWorker"),
                LockCount: t.LockCount,
                CurrentExceptionType: t.CurrentException?.Type?.Name,
                TopFrameMethod: topFrame?.DisplayName,
                Frames: frames)
            {
                IsLikelyBlocked = likelyBlocked,
                InferredWaitReason = waitReason,
            });
        }

        var locks = WalkSyncBlocks(runtime, threadByAddress, warnings, ct);
        return (threads, locks);
    }

    private List<ManagedStackFrame> WalkStack(ClrThread t, ThreadSnapshotOptions opts)
    {
        // Capacity bounded independently of the (already-validated) request to keep the live-attach
        // allocation footprint small even when callers ask for the hard cap and most threads have
        // shallow stacks.
        var frames = new List<ManagedStackFrame>(Math.Min(opts.MaxFramesPerThread, 64));
        foreach (var f in t.EnumerateStackTrace())
        {
            var kind = f.Kind.ToString();
            // ClrStackFrameKind in 3.x: ManagedMethod / Runtime / Unknown.
            // - ManagedMethod: always kept.
            // - Runtime (CLR helpers, transition stubs): gated by IncludeRuntimeFrames.
            // - Unknown / unresolved native trampolines (Method is null and kind != ManagedMethod):
            //   gated by IncludeNativeFrames (independent of IncludeRuntimeFrames).
            if (f.Kind == ClrStackFrameKind.Runtime && !opts.IncludeRuntimeFrames) continue;
            if (f.Kind != ClrStackFrameKind.ManagedMethod && f.Method is null && !opts.IncludeNativeFrames) continue;

            var method = f.Method;
            var display = method?.Signature ?? method?.Name ?? f.FrameName ?? "<unknown>";
            var typeFqn = method?.Type?.Name;
            var modulePath = method?.Type?.Module?.Name;
            var moduleName = !string.IsNullOrEmpty(modulePath) ? Path.GetFileName(modulePath) : null;

            MethodIdentity? identity = null;
            if (method is not null && method.MetadataToken != 0)
            {
                identity = new MethodIdentity(
                    ModuleName: moduleName,
                    ModulePath: modulePath,
                    ModuleVersionId: TryReadMvid(modulePath),
                    MetadataToken: method.MetadataToken,
                    TypeFullName: typeFqn,
                    MethodName: method.Name ?? "<unknown>",
                    GenericArity: 0);
            }

            frames.Add(new ManagedStackFrame(
                Kind: kind,
                DisplayName: display,
                TypeFullName: typeFqn,
                ModuleName: moduleName,
                InstructionPointer: f.InstructionPointer,
                StackPointer: f.StackPointer,
                Identity: identity));

            if (frames.Count >= opts.MaxFramesPerThread) break;
        }
        return frames;
    }

    private static (bool IsLikelyBlocked, string? Reason) ClassifyTopFrame(ManagedStackFrame? top)
    {
        if (top is null) return (false, null);
        var name = top.DisplayName ?? string.Empty;
        // Cheap heuristics — same set used by perf engineers eyeballing stacks.
        if (Contains(name, "Monitor.Wait")) return (true, "Monitor.Wait");
        if (Contains(name, "Monitor.ReliableEnter") || Contains(name, "Monitor.Enter")) return (true, "Monitor.Enter (contended)");
        if (Contains(name, "Thread.Sleep")) return (true, "Thread.Sleep");
        if (Contains(name, "Thread.Join")) return (true, "Thread.Join");
        if (Contains(name, "ManualResetEvent") || Contains(name, "AutoResetEvent") || Contains(name, "ManualResetEventSlim")) return (true, "ResetEvent.Wait");
        if (Contains(name, "Semaphore") && Contains(name, "Wait")) return (true, "Semaphore.Wait");
        if (Contains(name, "WaitHandle.Wait")) return (true, "WaitHandle.Wait");
        if (Contains(name, "Task.Wait") || Contains(name, "Task.WaitAll") || Contains(name, "Task.WaitAny")) return (true, "Task.Wait (blocking)");
        if (Contains(name, "SpinWait") || Contains(name, "SpinLock")) return (true, "SpinWait/SpinLock");
        if (Contains(name, "Park") || Contains(name, "WaitOne")) return (true, "WaitOne/Park");
        if (Contains(name, "Socket") && (Contains(name, "Receive") || Contains(name, "Accept") || Contains(name, "Poll"))) return (true, "Socket I/O");
        if (Contains(name, "Read") && Contains(name, "Stream")) return (true, "Stream.Read");
        return (false, null);
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.Ordinal);

    private static bool HasFlag(ClrThreadState state, string flagName) =>
        state.ToString().Contains(flagName, StringComparison.OrdinalIgnoreCase);

    private static MonitorLockState[] WalkSyncBlocks(
        ClrRuntime runtime,
        Dictionary<ulong, ClrThread> threadByAddress,
        List<string> warnings,
        CancellationToken ct)
    {
        var locks = new List<MonitorLockState>();
        try
        {
            foreach (var sb in runtime.Heap.EnumerateSyncBlocks())
            {
                ct.ThrowIfCancellationRequested();
                if (!sb.IsMonitorHeld && sb.WaitingThreadCount == 0) continue;

                var ownerAddr = sb.HoldingThreadAddress;
                int ownerMid = -1; uint ownerOs = 0;
                if (ownerAddr != 0 && threadByAddress.TryGetValue(ownerAddr, out var ownerThread))
                {
                    ownerMid = ownerThread.ManagedThreadId;
                    ownerOs = ownerThread.OSThreadId;
                }

                ClrObject obj = default;
                try { if (sb.Object != 0) obj = runtime.Heap.GetObject(sb.Object); } catch { /* skip */ }

                locks.Add(new MonitorLockState(
                    ObjectAddress: sb.Object,
                    ObjectTypeFullName: obj.Type?.Name,
                    OwnerManagedThreadId: ownerMid,
                    OwnerOSThreadId: ownerOs,
                    OwnerThreadAddress: ownerAddr,
                    RecursionCount: sb.RecursionCount,
                    WaitingThreadCount: sb.WaitingThreadCount,
                    IsContended: sb.WaitingThreadCount > 0,
                    Source: "SyncBlock"));
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"SyncBlock enumeration aborted partway through: {ex.GetType().Name} ({ex.Message}).");
        }
        return locks.OrderByDescending(l => l.WaitingThreadCount).ThenByDescending(l => l.RecursionCount).ToArray();
    }

    private Guid? TryReadMvid(string? assemblyPath)
    {
        if (string.IsNullOrEmpty(assemblyPath)) return null;
        if (!File.Exists(assemblyPath)) return null;
        if (_mvidCache.TryGetValue(assemblyPath, out var cached)) return cached;
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
            {
                _mvidCache[assemblyPath] = null;
                return null;
            }
            var metadata = peReader.GetMetadataReader();
            var mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
            _mvidCache[assemblyPath] = mvid;
            return mvid;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MVID read failed for {Path}", assemblyPath);
            _mvidCache[assemblyPath] = null;
            return null;
        }
    }
}
