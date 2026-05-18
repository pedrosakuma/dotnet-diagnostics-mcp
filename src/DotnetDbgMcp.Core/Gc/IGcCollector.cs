namespace DotnetDbgMcp.Core.Gc;

/// <summary>
/// Collects GC events from the target process over a fixed time window and returns a summary
/// (counts per generation, total/max pause, and per-event details).
/// </summary>
public interface IGcCollector
{
    Task<GcSummary> CollectAsync(
        int processId,
        TimeSpan duration,
        int maxEvents = 200,
        CancellationToken cancellationToken = default);
}
