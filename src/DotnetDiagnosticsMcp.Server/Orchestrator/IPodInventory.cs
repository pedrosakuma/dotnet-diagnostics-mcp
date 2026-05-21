using System.Threading;
using System.Threading.Tasks;

namespace DotnetDiagnosticsMcp.Server.Orchestrator;

/// <summary>
/// Inventory of attachable Pods. The MCP <c>list_pods</c> tool delegates to this service
/// instead of talking to the Kubernetes API directly so policy (namespace allowlist,
/// selector validation, preparedness verdict) and transport are independently testable.
/// </summary>
public interface IPodInventory
{
    /// <summary>
    /// Lists Pods that match the supplied filters, applying the orchestrator's
    /// configured allowlists and preparedness policy.
    /// </summary>
    /// <param name="request">Caller-supplied filters; all members optional except as noted on <see cref="ListPodsRequest"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A page of <see cref="PodCandidate"/> rows plus an optional opaque continuation cursor.</returns>
    /// <exception cref="OrchestratorException">
    /// Thrown for policy violations (<c>NamespaceNotAllowed</c>, <c>SelectorRejected</c>,
    /// <c>InvalidArgument</c>, <c>TooManyResults</c>) or Kubernetes call failures
    /// (<c>KubeApiUnavailable</c>, <c>PermissionDenied</c>).
    /// </exception>
    Task<PodCandidatePage> ListPodsAsync(ListPodsRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Request shape for <see cref="IPodInventory.ListPodsAsync"/>. Mirrors the
/// <c>list_pods</c> MCP tool signature but typed for easier unit testing.
/// </summary>
/// <param name="Namespace">Namespace to scope the listing to. Null falls back to <c>OrchestratorOptions.DefaultNamespace</c>.</param>
/// <param name="LabelSelector">Kubernetes label selector (e.g. <c>app=api,env=prod</c>). Keys are validated against the orchestrator's label allowlist when configured.</param>
/// <param name="FieldSelector">Kubernetes field selector (e.g. <c>status.phase=Running</c>). Passed through to the API as-is.</param>
/// <param name="ContainerName">Optional container name to target. When null, the first container in each Pod's spec is used.</param>
/// <param name="PreparedOnly">When true (default), only Pods with the prepared verdict are returned.</param>
/// <param name="IncludeNotReady">When false (default), Pods that are not Ready are filtered out post-page.</param>
/// <param name="Limit">Page size. Clamped to <c>OrchestratorOptions.MaxListLimit</c>.</param>
/// <param name="Cursor">Opaque continuation token from a prior page, or null for the first page.</param>
public sealed record ListPodsRequest(
    string? Namespace,
    string? LabelSelector,
    string? FieldSelector,
    string? ContainerName,
    bool PreparedOnly = true,
    bool IncludeNotReady = false,
    int Limit = 100,
    string? Cursor = null);
