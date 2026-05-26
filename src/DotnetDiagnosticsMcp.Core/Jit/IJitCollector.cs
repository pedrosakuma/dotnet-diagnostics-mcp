namespace DotnetDiagnosticsMcp.Core.Jit;

/// <summary>
/// Collects CLR JIT / tiered-compilation activity from the runtime EventPipe stream.
/// </summary>
public interface IJitCollector
{
    Task<JitSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        CancellationToken cancellationToken = default);
}
