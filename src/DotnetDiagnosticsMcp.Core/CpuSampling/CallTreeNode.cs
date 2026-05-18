namespace DotnetDiagnosticsMcp.Core.CpuSampling;

/// <summary>A node in a merged caller→callee tree built from CPU samples.</summary>
public sealed record CallTreeNode(
    SampledFrame Frame,
    long InclusiveSamples,
    long ExclusiveSamples,
    IReadOnlyList<CallTreeNode> Children);

/// <summary>Bounded view of a <see cref="CallTreeNode"/> returned by the drill-down tool.</summary>
public sealed record CallTreeView(
    int ProcessId,
    long TotalSamples,
    int NodeCount,
    bool Truncated,
    CallTreeNode Root);

/// <summary>
/// In-memory artifact registered under a handle when the CPU sampler completes. The summary
/// (returned to the LLM by <c>collect_cpu_sample</c>) is intentionally compact; the full tree
/// here is what <c>get_call_tree</c> walks on follow-up calls.
/// </summary>
public sealed record CpuSampleTraceArtifact(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    long TotalSamples,
    CallTreeNode Root);
