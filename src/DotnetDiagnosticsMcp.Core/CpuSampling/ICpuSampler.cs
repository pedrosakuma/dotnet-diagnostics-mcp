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
    /// call tree.
    /// </summary>
    Task<CpuSampleResult> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN = 25,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Combined result of a CPU sampling pass: the compact <see cref="CpuSample"/> summary returned
/// to the LLM and a heavier <see cref="CpuSampleTraceArtifact"/> retained under a handle for
/// drill-down queries.
/// </summary>
public sealed record CpuSampleResult(CpuSample Summary, CpuSampleTraceArtifact Artifact);
