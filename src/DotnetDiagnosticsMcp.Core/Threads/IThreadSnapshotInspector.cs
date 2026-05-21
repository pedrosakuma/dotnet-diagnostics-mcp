using DotnetDiagnosticsMcp.Core.Memory;

namespace DotnetDiagnosticsMcp.Core.Threads;

/// <summary>
/// Inspects threads + lock state of a .NET process. Two collection modes mirror
/// <see cref="Dump.IDumpInspector"/>:
/// <list type="bullet">
///   <item><see cref="InspectLiveAsync"/> — attach to a live PID via ClrMD (suspends the target during the walk).</item>
///   <item><see cref="InspectDumpAsync"/> — read a previously-captured .dmp file (no impact on the target).</item>
/// </list>
/// Both paths produce the same <see cref="ThreadSnapshotArtifact"/>; downstream drilldown queries
/// (<c>query_thread_snapshot</c>, the <c>thread://snapshot/{handle}</c> Resource) don't need to
/// know how the snapshot was collected — same "split collector, unified drilldown" pattern as the
/// heap snapshot work.
/// </summary>
public interface IThreadSnapshotInspector
{
    Task<ThreadSnapshotArtifact> InspectLiveAsync(
        int processId,
        ThreadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<ThreadSnapshotArtifact> InspectDumpAsync(
        string dumpFilePath,
        ThreadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <param name="MaxFramesPerThread">Cap on captured stack frames per thread. Defaults to 64.
/// The full snapshot retains this many frames so drilldown queries can slice up to the cap.</param>
/// <param name="IncludeRuntimeFrames">When false (default), runtime/internal frames (CLR helpers,
/// transition stubs) are dropped so the LLM-facing stack stays focused on user code.</param>
/// <param name="IncludeNativeFrames">When true, native frames are retained. Defaults to false.</param>
/// <param name="SymbolPath">Optional NT_SYMBOL_PATH-style search path. Precedence: symbolPath > MCP_SYMBOL_PATH > _NT_SYMBOL_PATH > target MainModule directory.</param>
public sealed record ThreadSnapshotOptions(
    int MaxFramesPerThread = 64,
    bool IncludeRuntimeFrames = false,
    bool IncludeNativeFrames = false,
    string? SymbolPath = null);

/// <summary>Where a <see cref="ThreadSnapshotArtifact"/> came from.</summary>
public enum ThreadSnapshotOrigin
{
    /// <summary>Snapshot captured by attaching to a live process via ClrMD.</summary>
    Live,
    /// <summary>Snapshot captured by reading a previously-written dump file.</summary>
    Dump,
}

/// <summary>
/// Canonical thread snapshot produced once per walk and registered in the drilldown handle store.
/// Both <c>collect_thread_snapshot</c> (live and dump variants) emit this same shape.
/// </summary>
public sealed record ThreadSnapshotArtifact(
    ThreadSnapshotOrigin Origin,
    int ProcessId,
    DateTimeOffset CapturedAt,
    TimeSpan WalkDuration,
    string RuntimeName,
    string RuntimeVersion,
    IReadOnlyList<ManagedThread> Threads,
    IReadOnlyList<MonitorLockState> Locks)
{
    /// <summary>Collector backend that produced this snapshot (for example <c>clrmd-thread-walk</c> or <c>linux-native-stack</c>).</summary>
    public string? Source { get; init; }
    /// <summary>Path to the originating dump file when <see cref="Origin"/> is <see cref="ThreadSnapshotOrigin.Dump"/>; <c>null</c> for live captures.</summary>
    public string? DumpFilePath { get; init; }
    /// <summary>On-disk size of the originating dump file; <c>null</c> for live captures.</summary>
    public long? DumpFileSizeBytes { get; init; }
    /// <summary>Diagnostic warnings emitted during the walk (degraded data, ClrMD limitations, …).</summary>
    public IReadOnlyList<string>? Warnings { get; init; }
    /// <summary>Optional ThreadPool counters/queues captured during the walk when the backend can observe them.</summary>
    public ThreadPoolSnapshot? ThreadPool { get; init; }
    /// <summary>
    /// Precision marker for the snapshot shape. Defaults to <c>exact</c>; fallback collectors can
    /// stamp a degraded mode (for example <c>perf-replay-approx</c>).
    /// </summary>
    public string SnapshotKind { get; init; } = "exact";
    /// <summary>
    /// Sampling/replay window in seconds for approximate snapshots. <c>null</c> for point-in-time
    /// snapshots captured by direct runtime/ptrace walks.
    /// </summary>
    public int? WindowSeconds { get; init; }
}

/// <summary>ThreadPool counters/queue state captured at the same instant as a thread snapshot.</summary>
public sealed record ThreadPoolSnapshot(
    bool Initialized,
    bool UsingPortableThreadPool,
    bool UsingWindowsThreadPool,
    ThreadPoolWorkerState Workers,
    ThreadPoolIocpState Iocp,
    ThreadPoolQueueState Queues,
    int PendingWorkItems)
{
    /// <summary>CPU utilization reported by the runtime for ThreadPool hill-climbing decisions (0-100) when available.</summary>
    public int? CpuUtilization { get; init; }
    /// <summary>The last hill-climbing log entry plus interval metadata when the runtime exposes it.</summary>
    public ThreadPoolHillClimbingState? HillClimbing { get; init; }
    /// <summary>Capture notes for degraded/partial fields.</summary>
    public IReadOnlyList<string>? Notes { get; init; }
}

/// <summary>Worker-thread counters from ClrMD + portable ThreadPool internals.</summary>
public sealed record ThreadPoolWorkerState(
    int Current,
    int Active,
    int Idle,
    int Retired,
    int Min,
    int Max);

/// <summary>IOCP/completion-port counters when the runtime exposes them.</summary>
public sealed record ThreadPoolIocpState(
    int Current,
    int Idle,
    int Min,
    int Max)
{
    /// <summary>Dynamic current limit on completion ports, when reported by ClrMD.</summary>
    public int? CurrentLimit { get; init; }
    /// <summary>Maximum free completion ports observed by the runtime, when reported by ClrMD.</summary>
    public int? MaxIdle { get; init; }
    /// <summary>Current Windows ThreadPool worker-thread count when the runtime delegates to the OS ThreadPool.</summary>
    public int? WindowsThreadPoolThreadCount { get; init; }
}

/// <summary>Global + local queue lengths used to derive the pending-work-item count.</summary>
public sealed record ThreadPoolQueueState(
    int GlobalQueueLength,
    IReadOnlyList<ThreadPoolNamedQueueLength> GlobalQueues,
    IReadOnlyList<ThreadPoolLocalQueueLength> LocalQueues);

/// <summary>One named global queue length.</summary>
public sealed record ThreadPoolNamedQueueLength(
    string Name,
    int QueueLength)
{
    /// <summary>Optional queue index for sharded global queues.</summary>
    public int? QueueIndex { get; init; }
    /// <summary>Queue object address when surfaced from ClrMD heap objects.</summary>
    public ulong? QueueAddress { get; init; }
}

/// <summary>One work-stealing queue length, optionally mapped back to the owning worker thread.</summary>
public sealed record ThreadPoolLocalQueueLength(
    ulong QueueAddress,
    int QueueLength)
{
    /// <summary>Managed thread id of the owning worker when the queue could be mapped.</summary>
    public int? ManagedThreadId { get; init; }
    /// <summary>OS thread id of the owning worker when the queue could be mapped.</summary>
    public uint? OSThreadId { get; init; }
    /// <summary>Runtime queue index from ThreadPoolWorkQueueThreadLocals when available.</summary>
    public int? QueueIndex { get; init; }
}

/// <summary>Last hill-climbing sample emitted by the runtime.</summary>
public sealed record ThreadPoolHillClimbingState(
    int TickCount,
    int SampleCount,
    int NewThreadCount,
    float Throughput,
    string StateOrTransition)
{
    /// <summary>Current adjustment interval in milliseconds from PortableThreadPool internals, when available.</summary>
    public int? AdjustmentIntervalMs { get; init; }
}

/// <summary>One managed thread observed in the runtime.</summary>
public sealed record ManagedThread(
    int ManagedThreadId,
    uint OSThreadId,
    ulong Address,
    string State,
    bool IsAlive,
    bool IsBackground,
    bool IsFinalizer,
    bool IsGc,
    bool IsThreadpoolWorker,
    uint LockCount,
    string? CurrentExceptionType,
    string? TopFrameMethod,
    IReadOnlyList<ManagedStackFrame> Frames)
{
    /// <summary>True when the top frame indicates this thread is parked/waiting/sleeping — useful for ranking.</summary>
    public bool IsLikelyBlocked { get; init; }
    /// <summary>Coarse wait reason inferred from the top frame (Monitor.Wait/Sleep/Park/Join/Socket/etc.) when detectable.</summary>
    public string? InferredWaitReason { get; init; }
}

/// <summary>Single managed stack frame. Carries the handoff identity for <c>dotnet-assembly-mcp</c>.</summary>
public sealed record ManagedStackFrame(
    string Kind,
    string DisplayName,
    string? TypeFullName,
    string? ModuleName,
    ulong InstructionPointer,
    ulong StackPointer,
    MethodIdentity? Identity = null);

/// <summary>
/// One held monitor / sync block (the closest thing ClrMD 3.x exposes to "locks").
/// </summary>
public sealed record MonitorLockState(
    ulong ObjectAddress,
    string? ObjectTypeFullName,
    int OwnerManagedThreadId,
    uint OwnerOSThreadId,
    ulong OwnerThreadAddress,
    int RecursionCount,
    int WaitingThreadCount,
    bool IsContended,
    string Source)
{
    /// <summary>
    /// Managed thread ids inferred or observed as waiting on this lock. For ClrMD-backed monitor
    /// snapshots this is derived from stack-root inspection, so the list may be incomplete.
    /// </summary>
    public IReadOnlyList<int> WaitingManagedThreadIds { get; init; } = Array.Empty<int>();

    /// <summary>Lock kind when recoverable. SyncBlock-backed locks are monitors.</summary>
    public string LockKind { get; init; } = "Monitor";
}
