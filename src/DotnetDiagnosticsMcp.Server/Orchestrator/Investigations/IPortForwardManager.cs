using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;

/// <summary>
/// Owns the per-handle data-plane between the orchestrator and the Pod-local diagnostics
/// MCP. Implementations open an in-process port-forward stream (no kubectl shell-out) and
/// expose it to the proxy endpoint as an <see cref="HttpClient"/> that can speak HTTP to
/// the ephemeral container.
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle ties to <see cref="InvestigationHandle.HandleId"/>: callers retrieve the
/// (lazily created) <see cref="HttpClient"/> on every proxied call via
/// <see cref="GetOrCreateClientAsync"/>, and tear the transport down via
/// <see cref="CloseAsync"/> on detach / TTL expiry / attach failure.
/// </para>
/// <para>
/// The returned <see cref="HttpClient"/>'s <c>BaseAddress</c> is a synthetic host
/// (<c>http://pod-local</c>) — the real transport is a custom <c>ConnectCallback</c> on
/// the underlying <see cref="SocketsHttpHandler"/> that opens a fresh port-forward stream
/// per connection. This keeps the proxy endpoint a thin HTTP-to-HTTP forwarder and lets
/// HttpClient's connection pool handle keep-alive across requests for the same handle.
/// </para>
/// </remarks>
public interface IPortForwardManager
{
    /// <summary>
    /// Returns the cached <see cref="HttpClient"/> for an investigation, creating it on
    /// first call. Idempotent — repeat calls for the same handle id return the same
    /// client instance. The handle's <see cref="InvestigationHandle.Namespace"/> and
    /// <see cref="InvestigationHandle.PodName"/> are used to wire the transport on first
    /// creation; subsequent calls ignore them.
    /// </summary>
    Task<HttpClient> GetOrCreateClientAsync(InvestigationHandle handle, CancellationToken cancellationToken);

    /// <summary>
    /// Releases the transport associated with a handle: cancels in-flight port-forward
    /// streams and disposes the <see cref="HttpClient"/>. Idempotent — closing an unknown
    /// or already-closed handle is a no-op. Always non-throwing.
    /// </summary>
    Task CloseAsync(string handleId);
}
