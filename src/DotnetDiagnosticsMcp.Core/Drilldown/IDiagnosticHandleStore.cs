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
    /// <param name="processId">PID the artifact was collected from. Used for invalidation when
    /// <paramref name="evictWhenProcessExits"/> is true.</param>
    /// <param name="kind">Short discriminator (e.g. "cpu-sample", "gc-dump").</param>
    /// <param name="artifact">Opaque payload retained in memory.</param>
    /// <param name="ttl">Maximum age before automatic eviction.</param>
    /// <param name="evictWhenProcessExits">When true (default), the handle is dropped as soon as
    /// the eviction background service notices the PID is no longer alive. Set to <c>false</c> for
    /// artifacts collected from an offline source (dump files, imported traces) whose <c>ProcessId</c>
    /// refers to a process on another host or a PID that may have been reused locally.</param>
    /// <returns>The newly-issued handle.</returns>
    DiagnosticHandle Register(int processId, string kind, object artifact, TimeSpan ttl, bool evictWhenProcessExits = true);

    /// <summary>
    /// Retrieves the artifact previously stored under <paramref name="handle"/>, casting it
    /// to <typeparamref name="T"/>. Returns <c>null</c> if the handle is unknown, expired, or
    /// holds an artifact of an incompatible type.
    /// </summary>
    T? TryGet<T>(string handle) where T : class;

    /// <summary>
    /// Retrieves the artifact previously stored under <paramref name="handle"/> together with the
    /// <c>kind</c> it was registered with — without forcing a generic type assertion. Returns
    /// <c>null</c> when the handle is unknown or expired. Used by the polymorphic
    /// <c>query_collection</c> dispatcher, which selects the artifact's concrete type based on
    /// <see cref="DiagnosticHandle.Kind"/>.
    /// </summary>
    HandleLookup? TryGetWithKind(string handle);

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

/// <summary>Bundle returned by <see cref="IDiagnosticHandleStore.TryGetWithKind"/>: the
/// metadata plus the untyped artifact, so polymorphic dispatchers can branch on
/// <see cref="DiagnosticHandle.Kind"/> without paying a generic type assertion first.</summary>
public readonly record struct HandleLookup(DiagnosticHandle Handle, object Artifact)
{
    /// <summary>Convenience accessor for <see cref="DiagnosticHandle.Kind"/>.</summary>
    public string Kind => Handle.Kind;
}
