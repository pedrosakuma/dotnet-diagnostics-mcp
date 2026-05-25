using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DotnetDiagnosticsMcp.Server.Azure.Discovery;

/// <summary>
/// Narrow adapter over <see cref="global::Azure.ResourceManager.ContainerService.ContainerServiceManagedClusterCollection"/>,
/// modelled on <see cref="DotnetDiagnosticsMcp.Server.Orchestrator.IKubernetesPodsApi"/>.
/// Exists so <see cref="AzureAksDiscovery"/> can be unit-tested with fakes without
/// pulling in the real Azure SDK pipeline.
/// </summary>
/// <remarks>
/// The adapter intentionally returns simple value records (not the SDK's LRO /
/// pageable types) — the discovery backend wants metadata projection, not the SDK
/// extension surface. Cross-subscription / cross-tenant clients are out of scope
/// here: callers pass a pre-built <see cref="global::Azure.ResourceManager.ArmClient"/>
/// via the production implementation's constructor.
/// </remarks>
public interface IAzureManagedClusterCollectionAdapter
{
    /// <summary>
    /// Enumerates managed clusters in the subscription, optionally narrowed to a
    /// single resource group. Order is the SDK's native ordering (ARM's stable
    /// list order); paging is exposed via the returned <see cref="AzureAksClusterRow"/>
    /// stream — the backend stitches a cursor onto its own envelope.
    /// </summary>
    IAsyncEnumerable<AzureAksClusterRow> ListAsync(
        string subscriptionId,
        string? resourceGroup,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fetches a cluster-user kubeconfig for the cluster at <paramref name="resourceId"/>.
    /// Maps to ARM's <c>listClusterUserCredential</c> action — requires the
    /// <em>Azure Kubernetes Service Cluster User Role</em> on the caller. Returns
    /// kubeconfig YAML bytes (already base64-decoded). Throws when the caller is
    /// forbidden or the cluster does not exist; the backend catches and folds those
    /// into a readinessWarning rather than failing the whole listing.
    /// </summary>
    Task<byte[]> GetClusterUserKubeconfigAsync(string resourceId, CancellationToken cancellationToken);
}

/// <summary>
/// Projection of one managed-cluster row pulled out of the Azure SDK. The adapter
/// flattens the SDK's nested data shape into the fields the discovery backend
/// needs so the unit-test fakes don't have to mock the SDK type graph.
/// </summary>
/// <param name="ResourceId">Full ARM resource id of the managed cluster.</param>
/// <param name="Name">Cluster name.</param>
/// <param name="Location">Azure region (e.g. <c>westeurope</c>).</param>
/// <param name="AgentPoolCount">Number of agent pools declared on the cluster.</param>
/// <param name="Fqdn">Public API server FQDN, or null when the cluster is private.</param>
/// <param name="KubernetesVersion">Currently-running Kubernetes version (e.g. <c>1.30.0</c>) when reported.</param>
/// <param name="NodeResourceGroup">Auto-managed node resource group (e.g. <c>MC_prod_prod-aks_westeurope</c>).</param>
/// <param name="IsPrivateCluster">When true, the cluster has <c>ApiServerAccessProfile.EnablePrivateCluster=true</c>.</param>
public sealed record AzureAksClusterRow(
    string ResourceId,
    string Name,
    string Location,
    int AgentPoolCount,
    string? Fqdn,
    string? KubernetesVersion,
    string? NodeResourceGroup,
    bool IsPrivateCluster);
