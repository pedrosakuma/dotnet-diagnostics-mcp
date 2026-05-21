using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using k8s.Models;

namespace DotnetDiagnosticsMcp.Server.Orchestrator;

/// <summary>
/// Narrow abstraction over the subset of the Kubernetes API the orchestrator actually
/// uses. Exists so unit tests can swap in a stub without mocking the full
/// <c>k8s.IKubernetes</c> surface.
/// </summary>
/// <remarks>
/// Each method delegates to the underlying client with no extra business logic — the
/// orchestrator's policy (allowlists, selector validation, preparedness verdict) lives
/// one layer up in <see cref="IPodInventory"/>.
/// </remarks>
public interface IKubernetesPodsApi
{
    /// <summary>
    /// Lists Pods in a namespace. Pass null/empty <paramref name="namespaceName"/> to
    /// list across all namespaces (requires cluster-scoped RBAC).
    /// </summary>
    /// <param name="namespaceName">Namespace to scope the list to, or null for all namespaces.</param>
    /// <param name="labelSelector">Optional Kubernetes label selector forwarded as-is.</param>
    /// <param name="fieldSelector">Optional Kubernetes field selector forwarded as-is.</param>
    /// <param name="limit">Server-side page size. Null lets the API server choose.</param>
    /// <param name="continueToken">Opaque continuation token from a prior page, or null for the first page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The raw Pod list, including any <c>Metadata.ContinueProperty</c> the API server emitted.</returns>
    Task<V1PodList> ListPodsAsync(
        string? namespaceName,
        string? labelSelector,
        string? fieldSelector,
        int? limit,
        string? continueToken,
        CancellationToken cancellationToken);
}
