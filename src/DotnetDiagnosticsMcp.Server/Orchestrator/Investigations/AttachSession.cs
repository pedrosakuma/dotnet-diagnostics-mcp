using System;

namespace DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;

/// <summary>
/// Client-safe projection of an <see cref="InvestigationHandle"/>, returned by the
/// <c>attach_to_pod</c> MCP tool. Deliberately omits <see cref="InvestigationHandle.PodLocalBearerToken"/>
/// — that secret is generated per-attach, embedded in the ephemeral container's environment,
/// and held only in the orchestrator-side <see cref="IInvestigationStore"/>. The proxy
/// (P3b-2) injects it on the server side of the boundary so the external LLM client never
/// sees it. See docs/central-orchestrator-design.md §6.4.
/// </summary>
public sealed record AttachSession(
    string HandleId,
    string Namespace,
    string PodName,
    string TargetContainerName,
    string EphemeralContainerName,
    InvestigationState State,
    DateTimeOffset AttachedAt,
    DateTimeOffset ExpiresAt,
    string? FailureReason = null,
    string? ProxyBaseUrl = null)
{
    /// <summary>
    /// Projects an internal handle into the client-safe shape, dropping the bearer token.
    /// When <paramref name="proxyBaseUrl"/> is supplied it is attached so the client knows
    /// the URL prefix subsequent diagnostic tool calls should target. The orchestrator's
    /// reverse proxy strips the prefix, injects the per-attach bearer token, and forwards
    /// to the Pod-local diagnostics MCP.
    /// </summary>
    public static AttachSession FromHandle(InvestigationHandle handle, string? proxyBaseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return new AttachSession(
            HandleId: handle.HandleId,
            Namespace: handle.Namespace,
            PodName: handle.PodName,
            TargetContainerName: handle.TargetContainerName,
            EphemeralContainerName: handle.EphemeralContainerName,
            State: handle.State,
            AttachedAt: handle.AttachedAt,
            ExpiresAt: handle.ExpiresAt,
            FailureReason: handle.FailureReason,
            ProxyBaseUrl: proxyBaseUrl);
    }
}
