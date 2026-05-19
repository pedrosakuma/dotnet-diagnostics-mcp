namespace DotnetDiagnosticsMcp.Core.OffCpu;

/// <summary>
/// Compact summary of an off-CPU sampling window, safe to return inline to the MCP client.
/// The heavy per-stack and per-thread data lives in <see cref="OffCpuSnapshotArtifact"/>,
/// retrieved via the issued handle.
/// </summary>
/// <param name="ProcessId">Target pid.</param>
/// <param name="StartedAt">Wall-clock start of the sampling window.</param>
/// <param name="Duration">Configured (not measured) window length.</param>
/// <param name="TotalOffCpuMicros">Sum of off-CPU time across every thread of the target.</param>
/// <param name="DistinctThreads">Number of distinct kernel TIDs that went off-CPU at least once.</param>
/// <param name="TopBlockingStacks">Up to topN stacks ranked by inclusive off-CPU microseconds.</param>
/// <param name="SchedSwitches">Total <c>sched_switch</c> events attributed to the target (sanity check the LLM can use to confirm capture density).</param>
/// <param name="SymbolSource">Resolution quality across all frames (mirrors the on-CPU sampler's flag so the LLM can reason about kernel-vs-user symbol coverage).</param>
public sealed record OffCpuSnapshot(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    long TotalOffCpuMicros,
    int DistinctThreads,
    IReadOnlyList<OffCpuStackHotspot> TopBlockingStacks,
    long SchedSwitches,
    string SymbolSource);

/// <summary>A blocking stack ranked by the total micros spent off-CPU below it.</summary>
public sealed record OffCpuStackHotspot(
    string LeafFrame,
    long OffCpuMicros,
    long OccurrenceCount,
    string DominantState,
    IReadOnlyList<OffCpuFrame> Stack);

/// <summary>A single resolved stack frame (kernel or user, demangled when possible).</summary>
public sealed record OffCpuFrame(string Module, string Method);

/// <summary>
/// Full off-CPU data set retained behind a handle for drill-down queries. Keeps the per-thread
/// view (which the summary intentionally omits) and the raw stack-keyed aggregation so the LLM
/// can ask "which thread blocked the longest?" or "what does this specific stack look like?"
/// without re-running <c>perf record</c>.
/// </summary>
public sealed record OffCpuSnapshotArtifact(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    long TotalOffCpuMicros,
    long SchedSwitches,
    IReadOnlyList<OffCpuStackHotspot> Stacks,
    IReadOnlyList<OffCpuThreadView> Threads,
    string SymbolSource);

/// <summary>Per-thread off-CPU rollup ranked by total micros blocked.</summary>
public sealed record OffCpuThreadView(
    int Tid,
    string ThreadName,
    long OffCpuMicros,
    long SwitchCount,
    string TopBlockingLeaf);

/// <summary>Pair returned by <see cref="IOffCpuSampler"/>: lightweight summary plus the artifact for the handle store.</summary>
public sealed record OffCpuSampleResult(OffCpuSnapshot Summary, OffCpuSnapshotArtifact Artifact);

/// <summary>
/// Discriminated view returned by <c>query_off_cpu_snapshot</c>. Exactly one of
/// <see cref="Stacks"/>, <see cref="Threads"/>, <see cref="Stack"/> is non-null depending on the
/// requested <see cref="View"/> ("topStacks" | "byThread" | "stack").
/// </summary>
public sealed record OffCpuQueryView(
    string View,
    int ProcessId,
    long TotalOffCpuMicros,
    IReadOnlyList<OffCpuStackHotspot>? Stacks,
    IReadOnlyList<OffCpuThreadView>? Threads,
    OffCpuStackHotspot? Stack);
