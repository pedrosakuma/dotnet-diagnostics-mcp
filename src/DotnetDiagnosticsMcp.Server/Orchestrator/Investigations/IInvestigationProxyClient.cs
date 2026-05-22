using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;

/// <summary>
/// Forwards a single <c>tools/call</c> from the orchestrator's MCP surface to the
/// in-Pod diagnostics MCP that backs an active <see cref="InvestigationHandle"/>.
/// </summary>
/// <remarks>
/// <para>
/// This seam exists so the server-side proxy intercept (P3b-4, issue #153) can be
/// unit-tested without spinning a real Kubernetes port-forward + in-Pod MCP. The
/// production implementation (<see cref="PodLocalInvestigationProxyClient"/>) wraps the
/// MCP SDK's <c>McpClient</c> over the <see cref="IPortForwardManager"/> HttpClient and
/// injects the per-attach Pod-local bearer token before each call.
/// </para>
/// <para>
/// Implementations MUST be safe to invoke concurrently and SHOULD cache per-handle
/// transport state — the filter is on the hot path of every diagnostic tool call.
/// </para>
/// </remarks>
public interface IInvestigationProxyClient
{
    /// <summary>
    /// Forwards <paramref name="request"/> to the in-Pod MCP for the given handle and
    /// returns the upstream <see cref="CallToolResult"/> unchanged.
    /// </summary>
    /// <param name="handle">Active investigation handle. Caller is responsible for
    /// asserting <see cref="InvestigationState.Active"/> before calling.</param>
    /// <param name="request">The original <c>tools/call</c> params; forwarded verbatim
    /// so the in-Pod MCP receives identical tool name + arguments.</param>
    /// <param name="cancellationToken">Cancellation tied to the orchestrator-side
    /// request.</param>
    Task<CallToolResult> CallToolAsync(InvestigationHandle handle, CallToolRequestParams request, CancellationToken cancellationToken);
}
