using System.Collections.Generic;

namespace DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;

/// <summary>
/// Per-MCP-session storage of "which investigation handle is bound to this session".
/// </summary>
/// <remarks>
/// <para>
/// Phase 3b-3 of the central-orchestrator design (issue #20) introduces this binder so that
/// <c>attach_to_pod</c> can remember the investigation it just opened on behalf of the
/// caller's MCP session. Subsequent server-side machinery (proxy intercept, lands in
/// P3b-4 or the protocol-level intercept slice) can read the binding to decide whether
/// to forward a tool call through the <c>/proxy/{handleId}</c> endpoint instead of
/// executing it locally. <c>detach</c> (P4) clears the binding.
/// </para>
/// <para>
/// The binder is intentionally a separate seam from
/// <see cref="DotnetDiagnosticsMcp.Core.ProcessDiscovery.ISessionTargetBindingStore"/>:
/// the Core store maps a session to a <em>local</em> pid, while this binder maps a
/// session to a remote <em>investigation handle</em>. They never bind the same key.
/// </para>
/// <para>
/// Implementations MUST be thread-safe: MCP sessions can issue concurrent tool calls
/// and the binder is read on every tool dispatch.
/// </para>
/// </remarks>
public interface IInvestigationSessionBinder
{
    /// <summary>
    /// Returns the handle id bound to <paramref name="sessionId"/>, or <c>null</c>
    /// when no binding is registered. Implementations MUST return <c>null</c> for
    /// null/empty <paramref name="sessionId"/>.
    /// </summary>
    string? TryGetHandleId(string? sessionId);

    /// <summary>
    /// Registers (or replaces) the binding for <paramref name="sessionId"/>.
    /// </summary>
    /// <param name="sessionId">MCP session id. MUST be non-null and non-empty.</param>
    /// <param name="handleId">Investigation handle id. MUST be non-null and non-empty.</param>
    void Bind(string sessionId, string handleId);

    /// <summary>
    /// Removes the binding for <paramref name="sessionId"/> if one exists.
    /// </summary>
    /// <returns>The handle id that was previously bound, or <c>null</c> when nothing was bound.</returns>
    string? Unbind(string? sessionId);

    /// <summary>
    /// Removes every binding that points at <paramref name="handleId"/>. Used by the
    /// future <c>detach</c> tool and the TTL reaper so an expired handle does not
    /// leave dangling session bindings behind.
    /// </summary>
    /// <returns>The session ids whose bindings were removed. Empty when nothing matched.</returns>
    IReadOnlyCollection<string> UnbindAllForHandle(string handleId);

    /// <summary>Returns a snapshot of all current (sessionId, handleId) pairs. Order is unspecified.</summary>
    IReadOnlyCollection<KeyValuePair<string, string>> Snapshot();
}
