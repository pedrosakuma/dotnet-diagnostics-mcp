namespace DotnetDiagnosticsMcp.Core.Threads;

/// <summary>
/// Typed payload returned by <c>query_thread_snapshot</c>. Carries the slice requested by the LLM
/// (threads list, one thread's stack, lock graph, deadlock analysis, top-blocked ranking, or
/// unique stack groups) plus provenance fields (origin, pid, captured-at, suspend duration) so
/// the model can reason about freshness without a second roundtrip.
/// </summary>
public sealed record ThreadSnapshotQueryResult(
    string Handle,
    string View,
    string Origin,
    int ProcessId,
    DateTimeOffset CapturedAt,
    TimeSpan WalkDuration)
{
    /// <summary>Populated for <c>threads-summary</c> and <c>top-blocked</c>.</summary>
    public IReadOnlyList<ManagedThread>? Threads { get; init; }
    /// <summary>Populated for <c>stack</c>.</summary>
    public ManagedThread? Thread { get; init; }
    /// <summary>Populated for <c>lock-graph</c>.</summary>
    public IReadOnlyList<MonitorLockState>? Locks { get; init; }
<<<<<<< HEAD
    /// <summary>Populated for <c>deadlocks</c>.</summary>
    public IReadOnlyList<ThreadDeadlockCycle>? Deadlocks { get; init; }
    /// <summary>Populated for <c>unique-stacks</c>.</summary>
    public IReadOnlyList<UniqueThreadStackGroup>? UniqueStacks { get; init; }
=======
    /// <summary>Populated for <c>threadpool</c>.</summary>
    public ThreadPoolSnapshot? ThreadPool { get; init; }
>>>>>>> 8637000 (feat: add threadpool thread snapshot view)
    /// <summary>Echoes the thread id used by the <c>stack</c> view.</summary>
    public int? ThreadId { get; init; }
}

public sealed record ThreadDeadlockCycle(
    IReadOnlyList<ThreadDeadlockMember> CycleMembers,
    IReadOnlyList<ThreadDeadlockLink> LockChain,
    IReadOnlyList<ThreadDeadlockCommand> RecommendedCommands);

public sealed record ThreadDeadlockMember(
    int ThreadId,
    uint OSThreadId,
    string State,
    string? TopFrameMethod,
    string? InferredWaitReason);

public sealed record ThreadDeadlockLink(
    int WaitingThreadId,
    int OwnerThreadId,
    ulong LockObjectAddress,
    string? LockObjectTypeFullName,
    string LockKind);

public sealed record ThreadDeadlockCommand(string Command, string Purpose);

/// <summary>Small thread-id sample surfaced for a unique stack group.</summary>
public sealed record ThreadSampleId(int ManagedThreadId, uint OSThreadId);

/// <summary>
/// Aggregate returned by <c>query_thread_snapshot(view="unique-stacks")</c>. The canonical stack
/// is returned root → leaf for readability, while the signature hash is computed from the top
/// frames selected by the caller.
/// </summary>
public sealed record UniqueThreadStackGroup(
    string SignatureHash,
    int ThreadCount,
    double ThreadPercentage,
    IReadOnlyList<ThreadSampleId> SampleThreads,
    IReadOnlyList<ManagedStackFrame> CanonicalFrames)
{
    /// <summary>Coarse wait reason inferred from the representative thread when available.</summary>
    public string? InferredWaitReason { get; init; }
}
