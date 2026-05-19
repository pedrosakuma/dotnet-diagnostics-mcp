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
/// here is what <c>get_call_tree</c> walks on follow-up calls. <see cref="ResolvedSources"/>
/// holds optional source-level resolution (file:line, SourceLink) for top-N hotspots — keyed
/// by <c>(module, methodFullName)</c> so the exporter can attach the location without walking
/// the whole tree.
/// </summary>
public sealed record CpuSampleTraceArtifact(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    long TotalSamples,
    CallTreeNode Root,
    IReadOnlyDictionary<DotnetDiagnosticsMcp.Core.Memory.SymbolRef, DotnetDiagnosticsMcp.Core.Memory.SourceLocation>? ResolvedSources = null,
    IReadOnlyDictionary<DotnetDiagnosticsMcp.Core.Memory.SymbolRef, DotnetDiagnosticsMcp.Core.Memory.MethodIdentity>? MethodIdentities = null,
    NativeAotSymbolDemangler.SymbolSource SymbolSource = NativeAotSymbolDemangler.SymbolSource.Unknown)
{
    public IReadOnlyDictionary<DotnetDiagnosticsMcp.Core.Memory.SymbolRef, DotnetDiagnosticsMcp.Core.Memory.SourceLocation> ResolvedSources { get; init; }
        = ResolvedSources ?? EmptyResolved;

    public IReadOnlyDictionary<DotnetDiagnosticsMcp.Core.Memory.SymbolRef, DotnetDiagnosticsMcp.Core.Memory.MethodIdentity> MethodIdentities { get; init; }
        = MethodIdentities ?? EmptyIdentities;

    private static readonly IReadOnlyDictionary<DotnetDiagnosticsMcp.Core.Memory.SymbolRef, DotnetDiagnosticsMcp.Core.Memory.SourceLocation> EmptyResolved
        = new Dictionary<DotnetDiagnosticsMcp.Core.Memory.SymbolRef, DotnetDiagnosticsMcp.Core.Memory.SourceLocation>();

    private static readonly IReadOnlyDictionary<DotnetDiagnosticsMcp.Core.Memory.SymbolRef, DotnetDiagnosticsMcp.Core.Memory.MethodIdentity> EmptyIdentities
        = new Dictionary<DotnetDiagnosticsMcp.Core.Memory.SymbolRef, DotnetDiagnosticsMcp.Core.Memory.MethodIdentity>();
}
