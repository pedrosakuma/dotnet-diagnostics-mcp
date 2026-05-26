namespace DotnetDiagnosticsMcp.Core.ProcessDiscovery;

/// <summary>
/// Reads a process's effective runtime configuration (GC, ThreadPool, tiered compilation,
/// filtered runtime environment variables and curated AppContext switches).
/// </summary>
public interface IRuntimeConfigInspector
{
    Task<RuntimeConfigView> InspectAsync(int processId, CancellationToken cancellationToken = default);
}
