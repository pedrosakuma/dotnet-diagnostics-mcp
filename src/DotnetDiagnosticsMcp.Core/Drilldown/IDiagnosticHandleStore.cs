namespace DotnetDiagnosticsMcp.Core.Drilldown;

/// <summary>
/// In-process registry that stores a heavy diagnostic artifact (parsed trace, gcdump, etc.)
/// keyed by an opaque short-lived handle. Lets a tool return a small summary to the LLM and
/// expose a follow-up tool to drill down without re-running the collector.
/// </summary>
public interface IDiagnosticHandleStore
{
    /// <summary>
    /// Stores <paramref name="artifact"/> under a fresh handle. The artifact is evicted after
    /// <paramref name="ttl"/> elapses or after the store's capacity is reached.
    /// </summary>
    /// <param name="processId">PID the artifact was collected from. Used for invalidation.</param>
    /// <param name="kind">Short discriminator (e.g. "cpu-sample", "gc-dump").</param>
    /// <param name="artifact">Opaque payload retained in memory.</param>
    /// <param name="ttl">Maximum age before automatic eviction.</param>
    /// <returns>The newly-issued handle.</returns>
    DiagnosticHandle Register(int processId, string kind, object artifact, TimeSpan ttl);

    /// <summary>
    /// Retrieves the artifact previously stored under <paramref name="handle"/>, casting it
    /// to <typeparamref name="T"/>. Returns <c>null</c> if the handle is unknown, expired, or
    /// holds an artifact of an incompatible type.
    /// </summary>
    T? TryGet<T>(string handle) where T : class;

    /// <summary>
    /// Removes the artifact stored under <paramref name="handle"/> immediately. Safe to call
    /// when the handle is already expired or unknown.
    /// </summary>
    bool Invalidate(string handle);

    /// <summary>
    /// Removes every artifact previously registered for <paramref name="processId"/>. Use when
    /// the target process exits so consumers don't drill into a dead trace.
    /// </summary>
    int InvalidateForProcess(int processId);
}

/// <summary>Lightweight value-type description of a registered artifact.</summary>
public sealed record DiagnosticHandle(string Id, DateTimeOffset ExpiresAt, int ProcessId, string Kind);
