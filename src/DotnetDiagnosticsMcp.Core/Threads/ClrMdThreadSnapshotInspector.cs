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
        var (threads, locks, threadPool) = Capture(runtime, opts, warnings, isLiveCapture: true, ct);
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
            ThreadPool = threadPool,
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
        var (threads, locks, threadPool) = Capture(runtime, opts, warnings, isLiveCapture: false, ct);
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
            ThreadPool = threadPool,
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    private (IReadOnlyList<ManagedThread> Threads, IReadOnlyList<MonitorLockState> Locks, ThreadPoolSnapshot? ThreadPool) Capture(
        ClrRuntime runtime, ThreadSnapshotOptions opts, List<string> warnings, bool isLiveCapture, CancellationToken ct)
    {
        var threads = new List<ManagedThread>(runtime.Threads.Length);
        var clrThreads = new List<ClrThread>(runtime.Threads.Length);
        var threadByAddress = new Dictionary<ulong, ClrThread>();

        foreach (var t in runtime.Threads)
        {
            ct.ThrowIfCancellationRequested();
            clrThreads.Add(t);
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

        var managedThreadsById = threads
            .Where(thread => thread.ManagedThreadId > 0)
            .ToDictionary(thread => thread.ManagedThreadId);
        var locks = WalkSyncBlocks(runtime, clrThreads, threadByAddress, managedThreadsById, warnings, ct);
        var threadPool = CaptureThreadPool(
            runtime,
            threads,
            threads
                .Where(thread => thread.ManagedThreadId > 0)
                .ToDictionary(t => t.ManagedThreadId, t => t.OSThreadId),
            warnings,
            isLiveCapture,
            ct);
        return (threads, locks, threadPool);
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
        IReadOnlyList<ClrThread> clrThreads,
        Dictionary<ulong, ClrThread> threadByAddress,
        Dictionary<int, ManagedThread> managedThreadsById,
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

        var waitersByObjectAddress = InferWaitingThreads(clrThreads, managedThreadsById, locks, warnings, ct);
        return locks
            .Select(lockState => lockState with
            {
                WaitingManagedThreadIds = waitersByObjectAddress.TryGetValue(lockState.ObjectAddress, out var waiters)
                    ? waiters
                    : Array.Empty<int>(),
            })
            .OrderByDescending(l => l.WaitingThreadCount)
            .ThenByDescending(l => l.RecursionCount)
            .ToArray();
    }

    private static Dictionary<ulong, IReadOnlyList<int>> InferWaitingThreads(
        IReadOnlyList<ClrThread> clrThreads,
        Dictionary<int, ManagedThread> managedThreadsById,
        IReadOnlyList<MonitorLockState> locks,
        List<string> warnings,
        CancellationToken ct)
    {
        var contendedLocks = locks
            .Where(lockState => lockState.IsContended && lockState.ObjectAddress != 0 && lockState.OwnerManagedThreadId > 0)
            .ToDictionary(lockState => lockState.ObjectAddress);
        if (contendedLocks.Count == 0)
        {
            return new Dictionary<ulong, IReadOnlyList<int>>();
        }

        var waitersByObjectAddress = new Dictionary<ulong, List<int>>();
        foreach (var clrThread in clrThreads)
        {
            ct.ThrowIfCancellationRequested();
            if (!managedThreadsById.TryGetValue(clrThread.ManagedThreadId, out var managedThread) ||
                !string.Equals(managedThread.InferredWaitReason, "Monitor.Enter (contended)", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var candidateLocks = clrThread.EnumerateStackRoots()
                    .Select(root => root.Object.Address)
                    .Where(address => address != 0 && contendedLocks.ContainsKey(address))
                    .Distinct()
                    .Where(address => contendedLocks[address].OwnerManagedThreadId != managedThread.ManagedThreadId)
                    .Take(4)
                    .ToArray();

                if (candidateLocks.Length != 1)
                {
                    continue;
                }

                var waiters = waitersByObjectAddress.TryGetValue(candidateLocks[0], out var existing)
                    ? existing
                    : waitersByObjectAddress[candidateLocks[0]] = new List<int>();
                if (!waiters.Contains(managedThread.ManagedThreadId))
                {
                    waiters.Add(managedThread.ManagedThreadId);
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Waiter inference for managed thread {managedThread.ManagedThreadId} (OS {managedThread.OSThreadId}) aborted: {ex.GetType().Name}.");
            }
        }

        return waitersByObjectAddress.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<int>)pair.Value.OrderBy(id => id).ToArray());
    }


    private static ThreadPoolSnapshot? CaptureThreadPool(
        ClrRuntime runtime,
        IReadOnlyList<ManagedThread> threads,
        IReadOnlyDictionary<int, uint> osThreadIdsByManagedId,
        List<string> warnings,
        bool isLiveCapture,
        CancellationToken ct)
    {
        ClrThreadPool? threadPool;
        try
        {
            threadPool = runtime.ThreadPool;
        }
        catch (Exception ex)
        {
            warnings.Add($"ThreadPool capture failed: {ex.GetType().Name} ({ex.Message}).");
            return null;
        }

        if (threadPool is null)
        {
            return isLiveCapture
                ? CaptureThreadPoolFromLiveSnapshot(runtime, threads, osThreadIdsByManagedId, warnings, ct)
                : CaptureThreadPoolFromRuntimeInternals(runtime, osThreadIdsByManagedId, warnings, ct);
        }

        var notes = new List<string>();
        var globalQueues = CaptureGlobalQueues(runtime, notes, allowHeapFallback: !isLiveCapture, ct);
        var localQueues = CaptureLocalQueues(runtime, osThreadIdsByManagedId, notes, allowHeapFallback: !isLiveCapture, includeOwnerMapping: !isLiveCapture, ct);
        var globalQueueLength = globalQueues.Sum(q => q.QueueLength);
        var pendingWorkItems = globalQueueLength + localQueues.Sum(q => q.QueueLength);

        return new ThreadPoolSnapshot(
            Initialized: true,
            UsingPortableThreadPool: threadPool.UsingPortableThreadPool,
            UsingWindowsThreadPool: threadPool.UsingWindowsThreadPool,
            Workers: new ThreadPoolWorkerState(
                Current: Math.Max(0, threadPool.ActiveWorkerThreads + threadPool.IdleWorkerThreads + threadPool.RetiredWorkerThreads),
                Active: threadPool.ActiveWorkerThreads,
                Idle: threadPool.IdleWorkerThreads,
                Retired: threadPool.RetiredWorkerThreads,
                Min: threadPool.MinThreads,
                Max: threadPool.MaxThreads),
            Iocp: new ThreadPoolIocpState(
                Current: threadPool.TotalCompletionPorts,
                Idle: threadPool.FreeCompletionPorts,
                Min: threadPool.MinCompletionPorts,
                Max: threadPool.MaxCompletionPorts)
            {
                CurrentLimit = threadPool.CompletionPortCurrentLimit,
                MaxIdle = threadPool.MaxFreeCompletionPorts,
                WindowsThreadPoolThreadCount = threadPool.UsingWindowsThreadPool ? threadPool.WindowsThreadPoolThreadCount : null,
            },
            Queues: new ThreadPoolQueueState(globalQueueLength, globalQueues, localQueues),
            PendingWorkItems: pendingWorkItems)
        {
            CpuUtilization = threadPool.CpuUtilization >= 0 ? threadPool.CpuUtilization : null,
            HillClimbing = CaptureHillClimbing(runtime, threadPool, notes),
            Notes = notes.Count > 0 ? notes : null,
        };
    }

    private static ThreadPoolSnapshot CaptureThreadPoolFromLiveSnapshot(
        ClrRuntime runtime,
        IReadOnlyList<ManagedThread> threads,
        IReadOnlyDictionary<int, uint> osThreadIdsByManagedId,
        List<string> warnings,
        CancellationToken ct)
    {
        _ = warnings;
        var notes = new List<string>
        {
            "ClrMD runtime.ThreadPool was null during live attach; using thread snapshot + static ThreadPool roots only to avoid heap-wide walks while the target process is suspended.",
            "Configured ThreadPool worker/IOCP min/max counts are not observable from the live lightweight fallback and are reported as best-effort values.",
        };

        var globalQueues = CaptureGlobalQueues(runtime, notes, allowHeapFallback: false, ct);
        var localQueues = CaptureLocalQueues(runtime, osThreadIdsByManagedId, notes, allowHeapFallback: false, includeOwnerMapping: false, ct);
        var currentWorkers = threads.Count(t => t.IsThreadpoolWorker);
        var activeWorkers = threads.Count(t => t.IsThreadpoolWorker && !t.IsLikelyBlocked);
        var idleWorkers = Math.Max(0, currentWorkers - activeWorkers);
        var globalQueueLength = globalQueues.Sum(q => q.QueueLength);
        var pendingWorkItems = globalQueueLength + localQueues.Sum(q => q.QueueLength);

        return new ThreadPoolSnapshot(
            Initialized: true,
            UsingPortableThreadPool: true,
            UsingWindowsThreadPool: false,
            Workers: new ThreadPoolWorkerState(
                Current: currentWorkers,
                Active: activeWorkers,
                Idle: idleWorkers,
                Retired: 0,
                Min: 0,
                Max: currentWorkers),
            Iocp: new ThreadPoolIocpState(
                Current: 0,
                Idle: 0,
                Min: 0,
                Max: 0),
            Queues: new ThreadPoolQueueState(globalQueueLength, globalQueues, localQueues),
            PendingWorkItems: pendingWorkItems)
        {
            Notes = notes,
        };
    }

    private static ThreadPoolSnapshot? CaptureThreadPoolFromRuntimeInternals(
        ClrRuntime runtime,
        IReadOnlyDictionary<int, uint> osThreadIdsByManagedId,
        List<string> warnings,
        CancellationToken ct)
    {
        var notes = new List<string>
        {
            "ClrMD runtime.ThreadPool was null; using direct PortableThreadPool/ThreadPoolWorkQueue field inspection instead.",
        };

        var portableThreadPool = FindSingletonObjectByTypeName(runtime, "System.Threading.PortableThreadPool", ct);
        if (portableThreadPool.IsNull || !portableThreadPool.IsValid)
        {
            warnings.Add("ThreadPool capture failed: neither ClrMD runtime.ThreadPool nor a PortableThreadPool heap singleton were readable.");
            return null;
        }

        var minThreads = portableThreadPool.TryReadField<short>("_minThreads", out var min) ? min : (short)0;
        var maxThreads = portableThreadPool.TryReadField<short>("_maxThreads", out var max) ? max : (short)0;
        var minIocp = portableThreadPool.TryReadField<short>("_legacy_minIOCompletionThreads", out var minIo) ? minIo : (short)0;
        var maxIocp = portableThreadPool.TryReadField<short>("_legacy_maxIOCompletionThreads", out var maxIo) ? maxIo : (short)0;
        var cpuUtilization = portableThreadPool.TryReadField<int>("_cpuUtilization", out var cpu) ? cpu : (int?)null;
        var separated = TryReadValueTypeField(portableThreadPool, "_separated");
        var counts = TryReadValueTypeField(separated, "counts");
        ulong countsData = 0;
        if (counts.IsValid)
        {
            TryReadField(counts, "_data", out countsData);
        }
        else
        {
            notes.Add("PortableThreadPool._separated.counts was not readable; worker counts may be incomplete.");
        }

        var callbackCounts = TryReadValueTypeField(portableThreadPool, "_countsOfThreadsProcessingUserCallbacks");
        uint callbackCountsData = 0;
        if (callbackCounts.IsValid)
        {
            TryReadField(callbackCounts, "_data", out callbackCountsData);
        }

        var existingThreads = (short)((countsData >> 16) & 0xFFFF);
        var processingThreads = (short)(countsData & 0xFFFF);
        var activeCallbacks = (short)(callbackCountsData & 0xFFFF);
        var globalQueues = CaptureGlobalQueues(runtime, notes, allowHeapFallback: true, ct);
        var localQueues = CaptureLocalQueues(runtime, osThreadIdsByManagedId, notes, allowHeapFallback: true, includeOwnerMapping: true, ct);
        var globalQueueLength = globalQueues.Sum(q => q.QueueLength);
        var pendingWorkItems = globalQueueLength + localQueues.Sum(q => q.QueueLength);
        notes.Add("Current/idle IOCP counts are not observable from the PortableThreadPool fallback; only configured min/max are reported.");
        notes.Add("Retired worker threads are not observable from the PortableThreadPool fallback and are reported as 0.");

        return new ThreadPoolSnapshot(
            Initialized: true,
            UsingPortableThreadPool: true,
            UsingWindowsThreadPool: false,
            Workers: new ThreadPoolWorkerState(
                Current: Math.Max(0, (int)existingThreads),
                Active: Math.Max(Math.Max(0, (int)processingThreads), Math.Max(0, (int)activeCallbacks)),
                Idle: Math.Max(0, existingThreads - Math.Max(processingThreads, activeCallbacks)),
                Retired: 0,
                Min: Math.Max(0, (int)minThreads),
                Max: Math.Max(0, (int)maxThreads)),
            Iocp: new ThreadPoolIocpState(
                Current: 0,
                Idle: 0,
                Min: Math.Max(0, (int)minIocp),
                Max: Math.Max(0, (int)maxIocp)),
            Queues: new ThreadPoolQueueState(globalQueueLength, globalQueues, localQueues),
            PendingWorkItems: pendingWorkItems)
        {
            CpuUtilization = cpuUtilization,
            HillClimbing = null,
            Notes = notes,
        };
    }

    private static ThreadPoolHillClimbingState? CaptureHillClimbing(
        ClrRuntime runtime,
        ClrThreadPool threadPool,
        List<string> notes)
    {
        try
        {
            HillClimbingLogEntry? last = null;
            foreach (var entry in threadPool.EnumerateHillClimbingLog())
            {
                last = entry;
            }

            if (last is null)
            {
                return null;
            }

            return new ThreadPoolHillClimbingState(
                TickCount: last.TickCount,
                SampleCount: last.SampleCount,
                NewThreadCount: last.NewThreadCount,
                Throughput: last.Throughput,
                StateOrTransition: last.StateOrTransition.ToString())
            {
                AdjustmentIntervalMs = TryReadPortableThreadPoolAdjustmentInterval(runtime),
            };
        }
        catch (Exception ex)
        {
            notes.Add($"Hill-climbing log unavailable: {ex.GetType().Name} ({ex.Message}).");
            return null;
        }
    }

    private static int? TryReadPortableThreadPoolAdjustmentInterval(ClrRuntime runtime)
    {
        try
        {
            var portableThreadPool = TryReadStaticObject(runtime, "System.Threading.PortableThreadPool", "ThreadPoolInstance");
            if (portableThreadPool.IsNull || !portableThreadPool.IsValid)
            {
                return null;
            }

            return portableThreadPool.TryReadField<int>("_threadAdjustmentIntervalMs", out var intervalMs)
                ? intervalMs
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static ThreadPoolNamedQueueLength[] CaptureGlobalQueues(ClrRuntime runtime, List<string> notes, bool allowHeapFallback, CancellationToken ct)
    {
        var workQueue = TryReadStaticObject(runtime, "System.Threading.ThreadPool", "s_workQueue");
        if ((workQueue.IsNull || !workQueue.IsValid) && allowHeapFallback)
        {
            workQueue = FindSingletonObjectByTypeName(runtime, "System.Threading.ThreadPoolWorkQueue", ct);
        }
        if (workQueue.IsNull || !workQueue.IsValid)
        {
            notes.Add(allowHeapFallback
                ? "ThreadPool global queue object was not readable from either ThreadPool.s_workQueue or the heap singleton."
                : "ThreadPool global queue object was not readable from ThreadPool.s_workQueue without a heap walk.");
            return Array.Empty<ThreadPoolNamedQueueLength>();
        }

        var queues = new List<ThreadPoolNamedQueueLength>(capacity: 8);

        AddGlobalQueue(queues, notes, "workItems", TryReadObjectField(workQueue, "workItems"), ct);
        AddGlobalQueue(queues, notes, "highPriorityWorkItems", TryReadObjectField(workQueue, "highPriorityWorkItems"), ct);
        AddGlobalQueue(queues, notes, "lowPriorityWorkItems", TryReadObjectField(workQueue, "lowPriorityWorkItems"), ct);

        var assignableQueues = TryReadObjectField(workQueue, "_assignableWorkItemQueues");
        if (!assignableQueues.IsNull && assignableQueues.IsValid)
        {
            try
            {
                var array = assignableQueues.AsArray();
                for (var i = 0; i < array.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var queue = array.GetObjectValue(i);
                    if (queue.IsNull || !queue.IsValid)
                    {
                        continue;
                    }

                    queues.Add(new ThreadPoolNamedQueueLength(
                        Name: "assignableWorkItemQueue",
                        QueueLength: CountConcurrentQueue(queue, notes, $"assignableWorkItemQueues[{i}]", ct))
                    {
                        QueueIndex = i,
                        QueueAddress = queue.Address,
                    });
                }
            }
            catch (Exception ex)
            {
                notes.Add($"Assignable ThreadPool queues were only partially readable: {ex.GetType().Name} ({ex.Message}).");
            }
        }

        return queues
            .OrderByDescending(q => q.QueueLength)
            .ThenBy(q => q.Name, StringComparer.Ordinal)
            .ThenBy(q => q.QueueIndex ?? int.MinValue)
            .ToArray();
    }

    private static void AddGlobalQueue(
        List<ThreadPoolNamedQueueLength> queues,
        List<string> notes,
        string name,
        ClrObject queue,
        CancellationToken ct)
    {
        if (queue.IsNull || !queue.IsValid)
        {
            return;
        }

        queues.Add(new ThreadPoolNamedQueueLength(
            Name: name,
            QueueLength: CountConcurrentQueue(queue, notes, name, ct))
        {
            QueueAddress = queue.Address,
        });
    }

    private static ThreadPoolLocalQueueLength[] CaptureLocalQueues(
        ClrRuntime runtime,
        IReadOnlyDictionary<int, uint> osThreadIdsByManagedId,
        List<string> notes,
        bool allowHeapFallback,
        bool includeOwnerMapping,
        CancellationToken ct)
    {
        var queuesObject = TryReadStaticObject(runtime, "System.Threading.ThreadPoolWorkQueue+WorkStealingQueueList", "s_queues");
        if (queuesObject.IsNull || !queuesObject.IsValid)
        {
            if (!allowHeapFallback)
            {
                notes.Add("ThreadPool local queue list was not readable from WorkStealingQueueList.s_queues without a heap walk.");
                return Array.Empty<ThreadPoolLocalQueueLength>();
            }

            return CaptureLocalQueuesFromThreadLocals(runtime, osThreadIdsByManagedId, notes, ct);
        }

        var queueLengths = new List<ThreadPoolLocalQueueLength>();
        try
        {
            var array = queuesObject.AsArray();
            for (var i = 0; i < array.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var queue = array.GetObjectValue(i);
                if (queue.IsNull || !queue.IsValid)
                {
                    continue;
                }

                queueLengths.Add(new ThreadPoolLocalQueueLength(
                    QueueAddress: queue.Address,
                    QueueLength: CountWorkStealingQueue(queue, notes, i))
                {
                    QueueIndex = i,
                });
            }
        }
        catch (Exception ex)
        {
            notes.Add($"Work-stealing queue list was only partially readable: {ex.GetType().Name} ({ex.Message}).");
        }

        if (queueLengths.Count == 0)
        {
            return Array.Empty<ThreadPoolLocalQueueLength>();
        }

        if (!includeOwnerMapping)
        {
            notes.Add("Local queue owner thread ids were skipped to avoid heap-wide scans during live suspended capture.");
            return queueLengths
                .OrderByDescending(q => q.QueueLength)
                .ThenBy(q => q.ManagedThreadId ?? int.MaxValue)
                .ThenBy(q => q.QueueAddress)
                .ToArray();
        }

        var metadataByQueue = MapLocalQueueOwners(runtime, queueLengths.Select(q => q.QueueAddress).ToHashSet(), osThreadIdsByManagedId, notes, ct);
        return queueLengths
            .Select(q => metadataByQueue.TryGetValue(q.QueueAddress, out var owner)
                ? q with { ManagedThreadId = owner.ManagedThreadId, OSThreadId = owner.OSThreadId, QueueIndex = owner.QueueIndex ?? q.QueueIndex }
                : q)
            .OrderByDescending(q => q.QueueLength)
            .ThenBy(q => q.ManagedThreadId ?? int.MaxValue)
            .ThenBy(q => q.QueueAddress)
            .ToArray();
    }

    private static ThreadPoolLocalQueueLength[] CaptureLocalQueuesFromThreadLocals(
        ClrRuntime runtime,
        IReadOnlyDictionary<int, uint> osThreadIdsByManagedId,
        List<string> notes,
        CancellationToken ct)
    {
        var localsType = FindTypeByName(runtime, "System.Threading.ThreadPoolWorkQueueThreadLocals");
        if (localsType is null)
        {
            return Array.Empty<ThreadPoolLocalQueueLength>();
        }

        var queueLengths = new Dictionary<ulong, ThreadPoolLocalQueueLength>();
        try
        {
            foreach (var obj in runtime.Heap.EnumerateObjects())
            {
                ct.ThrowIfCancellationRequested();
                if (obj.Type?.MethodTable != localsType.MethodTable)
                {
                    continue;
                }

                var queue = TryReadObjectField(obj, "workStealingQueue");
                if (queue.IsNull || !queue.IsValid || queueLengths.ContainsKey(queue.Address))
                {
                    continue;
                }

                int? managedThreadId = null;
                uint? osThreadId = null;
                var currentThread = TryReadObjectField(obj, "currentThread");
                if (!currentThread.IsNull && currentThread.IsValid && currentThread.TryReadField<int>("_managedThreadId", out var mid) && mid > 0)
                {
                    managedThreadId = mid;
                    if (osThreadIdsByManagedId.TryGetValue(mid, out var osThread))
                    {
                        osThreadId = osThread;
                    }
                }

                queueLengths[queue.Address] = new ThreadPoolLocalQueueLength(
                    QueueAddress: queue.Address,
                    QueueLength: CountWorkStealingQueue(queue, notes, queueLengths.Count))
                {
                    ManagedThreadId = managedThreadId,
                    OSThreadId = osThreadId,
                    QueueIndex = obj.TryReadField<int>("queueIndex", out var queueIndex) ? queueIndex : null,
                };
            }
        }
        catch (Exception ex)
        {
            notes.Add($"ThreadPool local queues were not fully readable from thread locals: {ex.GetType().Name} ({ex.Message}).");
        }

        return queueLengths.Values
            .OrderByDescending(q => q.QueueLength)
            .ThenBy(q => q.ManagedThreadId ?? int.MaxValue)
            .ThenBy(q => q.QueueAddress)
            .ToArray();
    }

    private static Dictionary<ulong, LocalQueueOwner> MapLocalQueueOwners(
        ClrRuntime runtime,
        HashSet<ulong> queueAddresses,
        IReadOnlyDictionary<int, uint> osThreadIdsByManagedId,
        List<string> notes,
        CancellationToken ct)
    {
        var owners = new Dictionary<ulong, LocalQueueOwner>();
        if (queueAddresses.Count == 0)
        {
            return owners;
        }

        var localsType = FindTypeByName(runtime, "System.Threading.ThreadPoolWorkQueueThreadLocals");
        if (localsType is null)
        {
            notes.Add("Could not resolve ThreadPoolWorkQueueThreadLocals, so local queues are reported without owner thread ids.");
            return owners;
        }

        try
        {
            foreach (var obj in runtime.Heap.EnumerateObjects())
            {
                ct.ThrowIfCancellationRequested();
                if (obj.Type?.MethodTable != localsType.MethodTable)
                {
                    continue;
                }

                var queue = TryReadObjectField(obj, "workStealingQueue");
                if (queue.IsNull || !queue.IsValid || !queueAddresses.Contains(queue.Address) || owners.ContainsKey(queue.Address))
                {
                    continue;
                }

                int? managedThreadId = null;
                uint? osThreadId = null;
                var currentThread = TryReadObjectField(obj, "currentThread");
                if (!currentThread.IsNull && currentThread.IsValid && currentThread.TryReadField<int>("_managedThreadId", out var mid) && mid > 0)
                {
                    managedThreadId = mid;
                    if (osThreadIdsByManagedId.TryGetValue(mid, out var osThread))
                    {
                        osThreadId = osThread;
                    }
                }

                owners[queue.Address] = new LocalQueueOwner(
                    managedThreadId,
                    osThreadId,
                    obj.TryReadField<int>("queueIndex", out var queueIndex) ? queueIndex : null);

                if (owners.Count == queueAddresses.Count)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            notes.Add($"ThreadPool local queue owners were not fully mapped: {ex.GetType().Name} ({ex.Message}).");
            return owners;
        }

        if (owners.Count < queueAddresses.Count)
        {
            notes.Add($"Mapped {owners.Count}/{queueAddresses.Count} work-stealing queues back to owning threads; remaining queues expose only queue addresses.");
        }

        return owners;
    }

    private static int CountWorkStealingQueue(ClrObject queue, List<string> notes, int queueIndex)
    {
        try
        {
            var head = queue.TryReadField<int>("m_headIndex", out var headIndex) ? headIndex : 0;
            var tail = queue.TryReadField<int>("m_tailIndex", out var tailIndex) ? tailIndex : 0;
            return Math.Max(0, tail - head);
        }
        catch (Exception ex)
        {
            notes.Add($"Local ThreadPool queue {queueIndex} length was not readable: {ex.GetType().Name} ({ex.Message}).");
            return 0;
        }
    }

    private static int CountConcurrentQueue(ClrObject queue, List<string> notes, string queueName, CancellationToken ct)
    {
        if (queue.IsNull || !queue.IsValid)
        {
            return 0;
        }

        try
        {
            var count = 0;
            var visitedSegments = new HashSet<ulong>();
            for (var segment = TryReadObjectField(queue, "_head"); !segment.IsNull && segment.IsValid && visitedSegments.Add(segment.Address); segment = TryReadObjectField(segment, "_nextSegment"))
            {
                ct.ThrowIfCancellationRequested();
                var slots = TryReadObjectField(segment, "_slots");
                if (slots.IsNull || !slots.IsValid)
                {
                    continue;
                }

                var array = slots.AsArray();
                for (var i = 0; i < array.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var slot = array.GetStructValue(i);
                    var item = TryReadObjectField(slot, "Item");
                    if (!item.IsNull && item.IsValid)
                    {
                        count++;
                    }
                }
            }

            return count;
        }
        catch (Exception ex)
        {
            notes.Add($"Global ThreadPool queue '{queueName}' length was not readable: {ex.GetType().Name} ({ex.Message}).");
            return 0;
        }
    }

    private static ClrObject TryReadStaticObject(ClrRuntime runtime, string typeName, string fieldName)
    {
        var type = FindTypeByName(runtime, typeName);
        if (type is null)
        {
            return default;
        }

        var field = type.GetStaticFieldByName(fieldName);
        if (field is null || !field.IsObjectReference)
        {
            return default;
        }

        foreach (var domain in runtime.AppDomains)
        {
            try
            {
                if (!field.IsInitialized(domain))
                {
                    continue;
                }

                var value = field.ReadObject(domain);
                if (!value.IsNull && value.IsValid)
                {
                    return value;
                }
            }
            catch
            {
                // best effort: try the next AppDomain
            }
        }

        return default;
    }

    private static ClrType? FindTypeByName(ClrRuntime runtime, string fullName)
    {
        var seen = new HashSet<ulong>();
        foreach (var module in runtime.EnumerateModules())
        {
            foreach (var (methodTable, _) in module.EnumerateTypeDefToMethodTableMap())
            {
                if (!seen.Add(methodTable))
                {
                    continue;
                }

                ClrType? type;
                try
                {
                    type = runtime.GetTypeByMethodTable(methodTable);
                }
                catch
                {
                    continue;
                }

                if (type?.Name == fullName)
                {
                    return type;
                }
            }
        }

        return null;
    }

    private static ClrObject FindSingletonObjectByTypeName(ClrRuntime runtime, string fullName, CancellationToken ct)
    {
        foreach (var obj in runtime.Heap.EnumerateObjects())
        {
            ct.ThrowIfCancellationRequested();
            if (obj.Type?.Name == fullName)
            {
                return obj;
            }
        }

        return default;
    }

    private static ClrObject TryReadObjectField(ClrObject obj, string fieldName)
    {
        try
        {
            return obj.ReadObjectField(fieldName);
        }
        catch
        {
            return default;
        }
    }

    private static ClrObject TryReadObjectField(ClrValueType value, string fieldName)
    {
        try
        {
            return value.ReadObjectField(fieldName);
        }
        catch
        {
            return default;
        }
    }

    private static ClrValueType TryReadValueTypeField(ClrObject obj, string fieldName)
    {
        try
        {
            return obj.ReadValueTypeField(fieldName);
        }
        catch
        {
            return default;
        }
    }

    private static ClrValueType TryReadValueTypeField(ClrValueType value, string fieldName)
    {
        try
        {
            return value.ReadValueTypeField(fieldName);
        }
        catch
        {
            return default;
        }
    }

    private static bool TryReadField<T>(ClrValueType value, string fieldName, out T result) where T : unmanaged
    {
        try
        {
            result = value.ReadField<T>(fieldName);
            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }

    private sealed record LocalQueueOwner(int? ManagedThreadId, uint? OSThreadId, int? QueueIndex);

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
