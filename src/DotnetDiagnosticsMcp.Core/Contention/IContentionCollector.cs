namespace DotnetDiagnosticsMcp.Core.Contention;

/// <summary>
/// Collects CLR monitor-lock contention activity from a target process over a fixed EventPipe window.
/// </summary>
public interface IContentionCollector
{
    Task<ContentionSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        CancellationToken cancellationToken = default);
}
