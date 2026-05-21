namespace DotnetDiagnosticsMcp.Core.CpuSampling;

/// <summary>
/// Collects CPU samples from a target process via EventPipe and returns the top-N hotspots
/// aggregated by frame plus an in-memory call-tree artifact suitable for follow-up drill-down.
/// </summary>
public interface ICpuSampler
{
    /// <summary>
    /// Samples the CPU stacks of the target process for <paramref name="duration"/> and
    /// aggregates the captured stacks into a top-N hotspot summary and a full caller→callee
    /// call tree. When <paramref name="sourceResolution"/> is enabled the top-N hotspots
    /// (only — not the whole stack) are resolved to <c>file:line</c> via PDB / SourceLink.
    /// </summary>
    Task<CpuSampleResult> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN = 25,
        SourceResolutionOptions? sourceResolution = null,
        MethodInstantiationResolutionOptions? methodInstantiationResolution = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional source-level resolution. <see cref="MaxResolved"/> caps how many top hotspots are
/// resolved (lazy resolution — PDB load + GetSourceLine is not free). <see cref="SymbolPath"/>
/// is forwarded to the underlying SymbolReader; a <c>null</c> value falls back to the standard
/// search (NT_SYMBOL_PATH env var + sidecar working directory).
/// </summary>
public sealed record SourceResolutionOptions(bool Enabled, string? SymbolPath = null, int MaxResolved = 10);

/// <summary>
/// Optional post-sample ClrMD enrichment that resolves the hottest managed frames to their closed
/// generic instantiations. <see cref="MaxResolved"/> bounds the attach-time work and defaults to
/// the same N as the hotspot list at the tool layer.
/// </summary>
public sealed record MethodInstantiationResolutionOptions(bool Enabled, int MaxResolved = 10);

/// <summary>
/// Combined result of a CPU sampling pass: the compact <see cref="CpuSample"/> summary returned
/// to the LLM and a heavier <see cref="CpuSampleTraceArtifact"/> retained under a handle for
/// drill-down queries.
/// </summary>
public sealed record CpuSampleResult(CpuSample Summary, CpuSampleTraceArtifact Artifact);
