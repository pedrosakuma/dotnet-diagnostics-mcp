namespace DotnetDiagnosticsMcp.Core.Dump;

/// <summary>
/// Typed payload returned by the <c>query_heap_snapshot</c> tool. Carries the slice requested
/// by the LLM (top-N types OR retention paths matching a filter) along with provenance fields
/// (origin, pid, captured-at) so the model can reason about freshness without a second roundtrip.
/// </summary>
public sealed record HeapSnapshotQueryResult(
    string Handle,
    string View,
    string Origin,
    int ProcessId,
    DateTimeOffset CapturedAt)
{
    /// <summary>Echoes the exact object address targeted by address-based heap queries.</summary>
    public ulong? Address { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"top-types"</c>.</summary>
    public IReadOnlyList<TypeStat>? TopTypes { get; init; }
    /// <summary>Echoes the ranking used for <c>top-types</c> queries: <c>"bytes"</c> or <c>"instances"</c>.</summary>
    public string? RankBy { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"retention-paths"</c>.</summary>
    public IReadOnlyList<RetentionPath>? RetentionPaths { get; init; }
    /// <summary>Echoes the substring filter applied to retention-path queries, if any.</summary>
    public string? FilterTypeFullName { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"roots-by-kind"</c>.</summary>
    public IReadOnlyList<RootKindStat>? RootsByKind { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"finalizer-queue"</c>.</summary>
    public IReadOnlyList<FinalizableTypeStat>? FinalizableObjects { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"fragmentation"</c>.</summary>
    public IReadOnlyList<SegmentStat>? Segments { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"static-fields"</c>.</summary>
    public IReadOnlyList<StaticFieldStat>? StaticFields { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"delegate-targets"</c>.</summary>
    public IReadOnlyList<DelegateTargetStat>? DelegateTargets { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"duplicate-strings"</c>.</summary>
    public IReadOnlyList<DuplicateStringStat>? DuplicateStrings { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"object"</c>.</summary>
    public HeapObjectInspection? ObjectDetails { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"gcroot"</c>.</summary>
    public HeapGcRootInspection? GcRoot { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"objsize"</c>.</summary>
    public HeapObjectSizeInspection? ObjectSize { get; init; }
}
