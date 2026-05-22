using System.Collections.Generic;

namespace DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;

/// <summary>
/// In-memory registry of investigation handles minted by <c>attach_to_pod</c>. Lookup
/// is by opaque handle id; reuse lookup is by (namespace, pod, container) tuple.
/// </summary>
/// <remarks>
/// The orchestrator is stateless across restarts by design (see
/// docs/central-orchestrator-design.md §5.7) — no implementation persists handles.
/// A typed interface still exists so unit tests can swap behavior and so a future
/// distributed-orchestrator implementation could plug in without touching call sites.
/// </remarks>
public interface IInvestigationStore
{
    /// <summary>Adds a fresh handle. Throws if <see cref="InvestigationHandle.HandleId"/> already exists.</summary>
    void Add(InvestigationHandle handle);

    /// <summary>
    /// Atomically reserves a target tuple in <see cref="InvestigationState.Attaching"/>: if no
    /// reusable handle for the target exists and reuse is allowed, the supplied <paramref name="newHandle"/>
    /// is registered and returned via <paramref name="existing"/>=null; if a reusable handle already
    /// exists and <paramref name="allowReuse"/> is true, it is returned via <paramref name="existing"/>;
    /// otherwise (reuse disabled and no existing handle), the new handle is registered and
    /// <paramref name="existing"/>=null is returned.
    /// </summary>
    /// <returns>True when the supplied <paramref name="newHandle"/> was registered; false when an existing handle was reused.</returns>
    bool TryReserveTarget(InvestigationHandle newHandle, bool allowReuse, out InvestigationHandle? existing);

    /// <summary>Updates an existing handle (e.g. state transition). Throws if the id is unknown.</summary>
    void Update(InvestigationHandle handle);

    /// <summary>
    /// Atomically transitions a handle to a terminal state (Closed / Expired / Failed),
    /// under the store lock. Returns the outcome so the caller can distinguish
    /// "transitioned now", "already terminal" (lost the race or prior close), and
    /// "unknown handle".
    /// </summary>
    /// <param name="handleId">Target handle id.</param>
    /// <param name="targetState">Terminal state to transition into. Must be Closed, Expired or Failed.</param>
    /// <param name="failureReason">Optional reason; ignored for Closed (which preserves any existing reason).</param>
    /// <param name="previousState">Out: state observed before the (attempted) transition. Null when the handle is unknown.</param>
    InvestigationTerminalTransition TryTransitionToTerminal(
        string handleId,
        InvestigationState targetState,
        string? failureReason,
        out InvestigationState? previousState);

    /// <summary>Returns the handle with the given id, or null if unknown.</summary>
    InvestigationHandle? GetById(string handleId);

    /// <summary>
    /// Returns an existing <see cref="InvestigationState.Active"/> or
    /// <see cref="InvestigationState.Attaching"/> handle for the given target, or null
    /// if none. Used by <c>attach_to_pod</c> to honour the reuse policy from §5.5.
    /// </summary>
    InvestigationHandle? FindReusableTarget(string podNamespace, string podName, string containerName);

    /// <summary>Snapshot of every known handle. Order is unspecified.</summary>
    IReadOnlyCollection<InvestigationHandle> Snapshot();
}

/// <summary>
/// Result of <see cref="IInvestigationStore.TryTransitionToTerminal"/>.
/// </summary>
public enum InvestigationTerminalTransition
{
    /// <summary>The handle id is not (or no longer) registered.</summary>
    NotFound,

    /// <summary>The handle was non-terminal and was atomically transitioned to the requested terminal state.</summary>
    Transitioned,

    /// <summary>The handle existed but was already terminal — no state change applied.</summary>
    AlreadyTerminal,
}
