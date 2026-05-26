namespace DotnetDiagnosticsMcp.Core.ProcessDiscovery;

/// <summary>
/// Collects OS-level file-descriptor / handle / socket state for a target process.
/// Works without EventPipe or ptrace.
/// </summary>
public interface IProcessResourcesCollector
{
    /// <summary>
    /// Captures a single resource snapshot (<paramref name="durationSeconds"/> = 0) or a
    /// short trend window (>= 2 seconds) for the target process.
    /// </summary>
    Task<ProcessResources> CollectAsync(
        int processId,
        int durationSeconds,
        int sampleEverySeconds,
        CancellationToken cancellationToken = default);
}
