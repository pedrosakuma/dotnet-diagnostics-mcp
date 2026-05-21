namespace DotnetDiagnosticsMcp.Core.Activities;

/// <summary>
/// Collects ActivitySource start/stop events via the DiagnosticSource EventPipe provider and
/// reconstructs activity lifetimes (ids, parent linkage, trace/span ids, tags, duration).
/// </summary>
public interface IActivityCollector
{
    Task<ActivityCapture> CollectAsync(
        int processId,
        TimeSpan duration,
        IReadOnlyList<string>? sources = null,
        int maxActivities = 200,
        CancellationToken cancellationToken = default);
}
