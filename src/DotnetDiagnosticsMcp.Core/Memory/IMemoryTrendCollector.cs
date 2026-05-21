namespace DotnetDiagnosticsMcp.Core.Memory;

/// <summary>
/// Collects a time-windowed sequence of memory snapshots for a target process and
/// computes growth deltas. Works on any process (not limited to .NET) — reads OS-level
/// APIs only, no EventPipe session or runtime attach required.
/// </summary>
public interface IMemoryTrendCollector
{
    /// <summary>
    /// Samples the target process's memory metrics at <paramref name="sampleEverySeconds"/>
    /// intervals for <paramref name="durationSeconds"/> total, then computes per-second
    /// deltas and a growth verdict.
    /// </summary>
    /// <param name="processId">OS process id of the target process.</param>
    /// <param name="durationSeconds">Total observation window length in seconds (≥ 2).</param>
    /// <param name="sampleEverySeconds">Interval between consecutive samples in seconds (≥ 1).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MemoryTrend> CollectAsync(
        int processId,
        int durationSeconds,
        int sampleEverySeconds,
        CancellationToken cancellationToken = default);
}
