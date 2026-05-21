namespace DotnetDiagnosticsMcp.Core.Dump;

/// <summary>
/// Inspects a .NET process heap and turns it into actionable JSON the LLM can drive
/// an investigation from. Two modes:
/// <list type="bullet">
///   <item><see cref="InspectAsync"/> — offline, reads a previously-captured dump file (cheap on the target).</item>
///   <item><see cref="InspectLiveAsync"/> — attaches to a live PID via ClrMD without writing a dump (no I/O, but suspends the target during the walk).</item>
/// </list>
/// Both paths share the same walker so the resulting top-N type lists and retention paths are directly comparable.
/// Both also produce a <see cref="HeapSnapshotArtifact"/> internally; tools register that aggregate in the
/// drilldown handle store so the LLM can ask follow-up questions (richer top-N, retention by type, …) without
/// paying the walk cost again.
/// </summary>
public interface IDumpInspector
{
    /// <summary>
    /// Walks the heap in <paramref name="dumpFilePath"/> and returns the full
    /// <see cref="HeapSnapshotArtifact"/>. Tools typically register the artifact in a handle store
    /// and project a bounded summary to the LLM.
    /// </summary>
    Task<HeapSnapshotArtifact> InspectAsync(
        string dumpFilePath,
        DumpInspectionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attaches to a live .NET process via ClrMD and walks its managed heap without writing
    /// a dump file. The same UID constraint as the diagnostic socket applies. The target is
    /// suspended for the duration of the heap walk (typically sub-second for ≤ ~200 MB, can
    /// reach a few seconds for multi-GB heaps).
    /// </summary>
    Task<HeapSnapshotArtifact> InspectLiveAsync(
        int processId,
        DumpInspectionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves one managed object by exact address against the process or dump behind
    /// <paramref name="snapshot"/> and returns a field/array/string view similar to SOS <c>!do</c>.
    /// </summary>
    Task<HeapObjectInspection> InspectObjectAsync(
        HeapSnapshotArtifact snapshot,
        ulong address,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the shortest GC-root chain currently found for the managed object at
    /// <paramref name="address"/> against the process or dump behind <paramref name="snapshot"/>.
    /// </summary>
    Task<HeapGcRootInspection> InspectGcRootAsync(
        HeapSnapshotArtifact snapshot,
        ulong address,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Walks the transitive closure rooted at <paramref name="address"/> and returns its retained
    /// bytes + object count, similar to SOS <c>!objsize</c>.
    /// </summary>
    Task<HeapObjectSizeInspection> InspectObjectSizeAsync(
        HeapSnapshotArtifact snapshot,
        ulong address,
        CancellationToken cancellationToken = default);
}

/// <summary>Caller-tunable knobs for <see cref="IDumpInspector.InspectAsync"/>.</summary>
/// <param name="TopTypes">Number of types to project into the inline summary. The snapshot
/// retains a richer set (<see cref="SnapshotTopTypes"/>) so drilldown queries can expand later.</param>
/// <param name="SnapshotTopTypes">Number of types retained inside the snapshot for later drilldown.
/// Should be ≥ <paramref name="TopTypes"/>. Defaults to 200.</param>
/// <param name="IncludeRetentionPaths">When true, walk a short GC root chain for each of the top-K types by bytes. Off by default (more expensive than the basic walk).</param>
/// <param name="RetentionPathLimit">When retention paths are enabled, cap the depth of each chain (defaults to 8 frames).</param>
/// <param name="SnapshotRetentionPathTargets">Number of distinct types for which to compute retention paths
/// when <paramref name="IncludeRetentionPaths"/> is set. Defaults to 10.</param>
/// <param name="SnapshotFinalizerQueueTopTypes">Number of types retained in the finalizer-queue drilldown view. Defaults to 50.</param>
/// <param name="IncludeStaticFields">When true, enumerate every loaded type's static reference fields and rank by the size of the directly-referenced object (cheap proxy for retained bytes). Off by default (visits every AppDomain × Module × Type).</param>
/// <param name="SnapshotStaticFieldTopN">Number of static-field entries retained for the static-fields drilldown view. Defaults to 100.</param>
/// <param name="IncludeDelegateTargets">When true, detect MulticastDelegate instances during the heap walk and group their invocation-list entries by target type + method. Cheap (folded into the existing single heap pass).</param>
/// <param name="SnapshotDelegateTargetTopN">Number of (target type, method) entries retained for the delegate-targets drilldown view. Defaults to 100.</param>
/// <param name="IncludeDuplicateStrings">When true, hash every System.String during the heap walk and rank by aggregate retained bytes (count × char-bytes). Cheap (folded into the existing single heap pass) but allocates one hash per unique string.</param>
/// <param name="SnapshotDuplicateStringTopN">Number of duplicate-string entries retained for the duplicate-strings drilldown view. Defaults to 100.</param>
/// <param name="DuplicateStringPreviewLength">Maximum characters of each string preview returned by the duplicate-strings view. Defaults to 80.</param>
public sealed record DumpInspectionOptions(
    int TopTypes = 20,
    int SnapshotTopTypes = 200,
    bool IncludeRetentionPaths = false,
    int RetentionPathLimit = 8,
    int SnapshotRetentionPathTargets = 10,
    int SnapshotFinalizerQueueTopTypes = 50,
    bool IncludeStaticFields = false,
    int SnapshotStaticFieldTopN = 100,
    bool IncludeDelegateTargets = false,
    int SnapshotDelegateTargetTopN = 100,
    bool IncludeDuplicateStrings = false,
    int SnapshotDuplicateStringTopN = 100,
    int DuplicateStringPreviewLength = 80);

/// <summary>Where a <see cref="HeapSnapshotArtifact"/> came from.</summary>
public enum HeapSnapshotOrigin
{
    /// <summary>Snapshot captured by reading a previously-written dump file.</summary>
    Dump,
    /// <summary>Snapshot captured by attaching to a live process via ClrMD.</summary>
    Live,
}

/// <summary>
/// Canonical heap snapshot produced once per walk and registered in the drilldown handle store.
/// Both <c>inspect_dump</c> and <c>inspect_live_heap</c> emit the same shape, so downstream
/// drilldown queries (<c>query_heap_snapshot</c>, <c>heap://snapshot/{handle}</c> Resource) do not
/// need to know how it was collected — that's the "split collector, unified drilldown" pattern.
/// </summary>
public sealed record HeapSnapshotArtifact(
    HeapSnapshotOrigin Origin,
    int ProcessId,
    DateTimeOffset CapturedAt,
    TimeSpan WalkDuration,
    DumpRuntimeInfo Runtime,
    DumpHeapSummary Heap,
    IReadOnlyList<TypeStat> TopTypesByBytes,
    IReadOnlyList<TypeStat> TopTypesByInstances)
{
    /// <summary>Path to the originating dump file when <see cref="Origin"/> is <see cref="HeapSnapshotOrigin.Dump"/>; <c>null</c> for live captures.</summary>
    public string? DumpFilePath { get; init; }
    /// <summary>On-disk size of the originating dump file; <c>null</c> for live captures.</summary>
    public long? DumpFileSizeBytes { get; init; }
    /// <summary>Retention paths walked for the top-N retained types (gated by <see cref="DumpInspectionOptions.IncludeRetentionPaths"/>).</summary>
    public IReadOnlyList<RetentionPath>? RetentionPaths { get; init; }
    /// <summary>GC roots aggregated by ClrRootKind. Populated unconditionally for every successful walk.</summary>
    public IReadOnlyList<RootKindStat>? RootsByKind { get; init; }
    /// <summary>Objects sitting on the finalizer queue grouped by managed type (top-N by retained bytes).
    /// A growing finalizer queue is a classic memory pressure smell.</summary>
    public IReadOnlyList<FinalizableTypeStat>? FinalizableObjectsByType { get; init; }
    /// <summary>Per-segment heap layout (gen, kind, length, committed, free bytes). Drives the fragmentation drilldown view.</summary>
    public IReadOnlyList<SegmentStat>? Segments { get; init; }
    /// <summary>Top static reference fields ranked by the directly-referenced object size. Gated by <see cref="DumpInspectionOptions.IncludeStaticFields"/>.</summary>
    public IReadOnlyList<StaticFieldStat>? StaticFields { get; init; }
    /// <summary>Delegate / event-handler subscribers grouped by (target type, method). Gated by <see cref="DumpInspectionOptions.IncludeDelegateTargets"/>.</summary>
    public IReadOnlyList<DelegateTargetStat>? DelegateTargets { get; init; }
    /// <summary>Top duplicate strings by aggregate retained bytes. Gated by <see cref="DumpInspectionOptions.IncludeDuplicateStrings"/>.</summary>
    public IReadOnlyList<DuplicateStringStat>? DuplicateStrings { get; init; }
    /// <summary>Diagnostic warnings emitted during the walk (degraded data, ClrMD limitations, …).</summary>
    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <summary>
/// Roll-up of GC roots for a single <c>ClrRootKind</c>. <see cref="DirectlyReferencedBytes"/> is the
/// sum of the sizes of the immediate target objects (not their reachable closures — that would
/// require a per-root retention walk and is too expensive for an always-on stat).
/// </summary>
public sealed record RootKindStat(
    string RootKind,
    long RootCount,
    long DistinctTargetObjects,
    long DirectlyReferencedBytes,
    long PinnedRootCount,
    long InteriorRootCount);

/// <summary>Type-aggregated entry from the finalizer queue.</summary>
public sealed record FinalizableTypeStat(
    string TypeFullName,
    string? ModuleName,
    long InstanceCount,
    long TotalBytes);

/// <summary>
/// Per-segment view used by the <c>fragmentation</c> drilldown. <see cref="FreeBytes"/> is the
/// sum of <c>obj.IsFree</c> sizes encountered during the walk — a high free-to-length ratio on
/// gen-2 or LOH is the classic fragmentation signal.
/// </summary>
public sealed record SegmentStat(
    int LogicalHeap,
    string Kind,
    string Generation,
    ulong Start,
    ulong End,
    long Length,
    long CommittedBytes,
    long ReservedBytes,
    long UsedBytes,
    long FreeBytes,
    long ObjectCount,
    long FreeObjectCount)
{
    /// <summary>Free bytes as a percentage of segment length, rounded to two decimals.</summary>
    public double FreePercent { get; init; }
}

/// <summary>
/// One static reference field detected on a loaded type. <see cref="DirectlyReferencedBytes"/>
/// is the size of the directly-referenced object (not the full retained graph — cheap proxy);
/// arrays/dictionaries that retain a lot will surface via large Length on the referenced object.
/// </summary>
public sealed record StaticFieldStat(
    string ContainingTypeFullName,
    string? ModuleName,
    string FieldName,
    int FieldToken,
    ulong ValueAddress,
    string? ValueTypeFullName,
    long DirectlyReferencedBytes,
    int AppDomainId)
{
    /// <summary>Type identity of the *containing* type (the one declaring the static field).</summary>
    public TypeIdentity? ContainingTypeIdentity { get; init; }
}

/// <summary>
/// Aggregated delegate / event subscriber. One row per (TargetTypeFullName, MethodFullName) pair
/// — e.g. <c>MyForm</c> → <c>MyForm.OnClick</c> appearing 1 000 times across leaked event handlers.
/// </summary>
public sealed record DelegateTargetStat(
    string? TargetTypeFullName,
    string DeclaringTypeFullName,
    string MethodName,
    string? MethodSignature,
    string? ModuleName,
    long SubscriberCount)
{
    /// <summary>Identity of the bound method (mvid + token) for handoff to dotnet-assembly-mcp.</summary>
    public Memory.MethodIdentity? Method { get; init; }
    /// <summary>True for static delegate targets (TargetObject is null). Closures over instances are false.</summary>
    public bool IsStaticTarget { get; init; }
}

/// <summary>One row in the duplicate-strings drilldown. <see cref="TotalBytes"/> is
/// <c>InstanceCount * (StringLength * 2 + headerBytes)</c> — i.e. what a string interner
/// would reclaim if the duplicates were collapsed.</summary>
public sealed record DuplicateStringStat(
    string Preview,
    int StringLength,
    long InstanceCount,
    long TotalBytes,
    bool PreviewTruncated);

/// <summary>Output of <see cref="IDumpInspector.InspectAsync"/> projected for inline tool consumption.</summary>
public sealed record DumpInspection(
    string FilePath,
    long FileSizeBytes,
    DumpRuntimeInfo Runtime,
    DumpHeapSummary Heap,
    IReadOnlyList<TypeStat> TopTypesByBytes,
    IReadOnlyList<TypeStat> TopTypesByInstances,
    IReadOnlyList<RetentionPath>? RetentionPaths = null,
    IReadOnlyList<string>? Warnings = null)
{
    /// <summary>Drilldown handle for follow-up queries; <c>null</c> when the inspector was invoked outside the MCP tool layer.</summary>
    public string? Handle { get; init; }
}

/// <summary>
/// Output of <see cref="IDumpInspector.InspectLiveAsync"/>. Mirrors <see cref="DumpInspection"/>
/// but reports process identity and the wall-clock time during which the target was suspended,
/// not a file path/size.
/// </summary>
public sealed record LiveHeapInspection(
    int ProcessId,
    TimeSpan SuspendDuration,
    DumpRuntimeInfo Runtime,
    DumpHeapSummary Heap,
    IReadOnlyList<TypeStat> TopTypesByBytes,
    IReadOnlyList<TypeStat> TopTypesByInstances,
    IReadOnlyList<RetentionPath>? RetentionPaths = null,
    IReadOnlyList<string>? Warnings = null)
{
    /// <summary>Drilldown handle for follow-up queries; <c>null</c> when the inspector was invoked outside the MCP tool layer.</summary>
    public string? Handle { get; init; }
}

public sealed record DumpRuntimeInfo(
    string Name,
    string Version,
    string Architecture,
    bool IsServerGC,
    int HeapCount);

public sealed record DumpHeapSummary(
    long TotalBytes,
    long Gen0Bytes,
    long Gen1Bytes,
    long Gen2Bytes,
    long LargeObjectHeapBytes,
    long PinnedObjectHeapBytes,
    long CommittedBytes);

/// <summary>
/// Aggregated heap statistic for a single managed type. <see cref="Identity"/> is the
/// cross-MCP handoff payload so the LLM can pivot from "what's retained" to
/// "what's the type definition" via <c>dotnet-assembly-mcp</c>.
/// </summary>
public sealed record TypeStat(
    string TypeFullName,
    string? ModuleName,
    long InstanceCount,
    long TotalBytes,
    double TotalBytesPercent,
    TypeIdentity? Identity = null);

/// <summary>
/// Canonical, machine-readable identity of a managed type observed in a dump
/// (issue #12 — pairs with <see cref="DotnetDiagnosticsMcp.Core.Memory.MethodIdentity"/>).
/// The <c>(ModuleVersionId, MetadataToken)</c> pair round-trips to a single
/// <c>TypeDefinition</c> (table 0x02) regardless of name mangling.
/// </summary>
public sealed record TypeIdentity(string TypeFullName)
{
    public string? ModuleName { get; init; }
    public string? ModulePath { get; init; }
    public Guid? ModuleVersionId { get; init; }
    public int? MetadataToken { get; init; }
}

/// <summary>
/// A short GC retention chain "root → … → instance" for one of the top retained types.
/// Useful for answering "why is this leak alive?" without manual <c>!gcroot</c> in WinDbg.
/// </summary>
public sealed record RetentionPath(
    string TargetTypeFullName,
    ulong TargetObjectAddress,
    IReadOnlyList<RetentionFrame> Chain,
    bool Truncated);

public sealed record RetentionFrame(
    string TypeFullName,
    ulong ObjectAddress)
{
    public string? RootKind { get; init; }
}
