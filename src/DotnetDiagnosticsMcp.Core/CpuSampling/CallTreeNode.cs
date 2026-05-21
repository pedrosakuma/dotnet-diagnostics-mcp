using DotnetDiagnosticsMcp.Core.Memory;

namespace DotnetDiagnosticsMcp.Core.CpuSampling;

/// <summary>A node in a merged caller→callee tree built from CPU samples.</summary>
public sealed record CallTreeNode(
    SampledFrame Frame,
    long InclusiveSamples,
    long ExclusiveSamples,
    IReadOnlyList<CallTreeNode> Children,
    MethodIdentity? Identity = null);

/// <summary>Reprojects a call tree with per-frame <see cref="MethodIdentity"/> payloads.</summary>
public static class CallTreeIdentityProjector
{
    public static CallTreeNode Stamp(
        CallTreeNode root,
        IReadOnlyDictionary<SymbolRef, MethodIdentity>? identities)
    {
        if (identities is null || identities.Count == 0)
        {
            return root;
        }

        return Walk(root);

        CallTreeNode Walk(CallTreeNode node)
        {
            identities.TryGetValue(new SymbolRef(node.Frame.Module, node.Frame.Method), out var identity);
            if (node.Children.Count == 0)
            {
                return node with { Identity = identity };
            }

            var children = node.Children.Select(Walk).ToList();
            return node with { Identity = identity, Children = children };
        }
    }
}

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
    IReadOnlyDictionary<SymbolRef, SourceLocation>? ResolvedSources = null,
    IReadOnlyDictionary<SymbolRef, MethodIdentity>? MethodIdentities = null,
    NativeAotSymbolDemangler.SymbolSource SymbolSource = NativeAotSymbolDemangler.SymbolSource.Unknown)
{
    public IReadOnlyDictionary<SymbolRef, SourceLocation> ResolvedSources { get; init; }
        = ResolvedSources ?? EmptyResolved;

    public IReadOnlyDictionary<SymbolRef, MethodIdentity> MethodIdentities { get; init; }
        = MethodIdentities ?? EmptyIdentities;

    private static readonly IReadOnlyDictionary<SymbolRef, SourceLocation> EmptyResolved
        = new Dictionary<SymbolRef, SourceLocation>();

    private static readonly IReadOnlyDictionary<SymbolRef, MethodIdentity> EmptyIdentities
        = new Dictionary<SymbolRef, MethodIdentity>();
}
