namespace DotnetDiagnosticsMcp.Core.OffCpu;

/// <summary>
/// Captures off-CPU stacks for a target process: where threads block, for how long, and on
/// which kernel/user frame they parked. The companion to <c>ICpuSampler</c> — on-CPU sampling
/// only sees the threads that are running, off-CPU sampling sees the ones that are not.
/// </summary>
public interface IOffCpuSampler
{
    /// <summary>
    /// True when the implementation can run on the current host. Cheap probe — no diagnostic IPC.
    /// Linux implementations should verify <c>perf</c>/BPF availability; Windows should verify
    /// kernel ETW.
    /// </summary>
    bool IsAvailable();

    /// <summary>
    /// Records sched events for <paramref name="duration"/> and returns the aggregated off-CPU view.
    /// </summary>
    /// <param name="processId">Target pid; all child kernel TIDs are captured.</param>
    /// <param name="duration">Sampling window. Must be (0, 5 minutes].</param>
    /// <param name="topN">Max blocking stacks returned in the summary; the full set is retained in the artifact.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OffCpuSampleResult> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN = 25,
        CancellationToken cancellationToken = default);
}
