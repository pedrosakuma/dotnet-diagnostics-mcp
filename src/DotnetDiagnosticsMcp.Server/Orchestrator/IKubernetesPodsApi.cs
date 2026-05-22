using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using k8s;
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

    /// <summary>
    /// Reads a single Pod by namespace + name. Used to validate the target before
    /// patching an ephemeral container and to poll readiness after the patch.
    /// </summary>
    Task<V1Pod> ReadPodAsync(
        string namespaceName,
        string name,
        CancellationToken cancellationToken);

    /// <summary>
    /// Patches a Pod's <c>pods/ephemeralcontainers</c> subresource to add a single
    /// ephemeral diagnostics container. The patch is JSON-strategic-merge over the
    /// canonical <see cref="V1EphemeralContainer"/> list.
    /// </summary>
    /// <param name="namespaceName">Pod namespace.</param>
    /// <param name="name">Pod name.</param>
    /// <param name="ephemeralContainer">The container spec to append.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated <see cref="V1Pod"/> reported by the API server.</returns>
    Task<V1Pod> AddEphemeralContainerAsync(
        string namespaceName,
        string name,
        V1EphemeralContainer ephemeralContainer,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens a port-forward WebSocket to a single port on the target Pod and returns a
    /// running <see cref="IStreamDemuxer"/>. Channel 0 carries data for the requested
    /// port; channel 1 carries error bytes. Caller owns the demuxer lifetime — disposing
    /// it closes the underlying WebSocket.
    /// </summary>
    /// <param name="namespaceName">Pod namespace.</param>
    /// <param name="name">Pod name.</param>
    /// <param name="podPort">TCP port to forward to inside the Pod.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IStreamDemuxer> OpenPortForwardAsync(
        string namespaceName,
        string name,
        int podPort,
        CancellationToken cancellationToken);
}
