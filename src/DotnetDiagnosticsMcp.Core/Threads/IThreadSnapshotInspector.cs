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
public sealed record ThreadSnapshotOptions(
    int MaxFramesPerThread = 64,
    bool IncludeRuntimeFrames = false,
    bool IncludeNativeFrames = false);

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
/// One held monitor / sync block (the closest thing ClrMD 3.x exposes to "locks"). Waiter
/// thread IDs are not directly available — only a count — so the drilldown view surfaces what's
/// known and flags ambiguity rather than guessing.
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
    string Source);
