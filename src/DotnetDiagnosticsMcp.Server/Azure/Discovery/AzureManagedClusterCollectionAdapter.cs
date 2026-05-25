using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using global::Azure;
using global::Azure.ResourceManager;
using global::Azure.ResourceManager.ContainerService;
using global::Azure.ResourceManager.Resources;

namespace DotnetDiagnosticsMcp.Server.Azure.Discovery;

/// <summary>
/// Production <see cref="IAzureManagedClusterCollectionAdapter"/> backed by
/// <see cref="ArmClient"/> (built by <see cref="IAzureArmClientFactory"/>) and the
/// <c>Azure.ResourceManager.ContainerService</c> extensions.
/// </summary>
internal sealed class AzureManagedClusterCollectionAdapter : IAzureManagedClusterCollectionAdapter
{
    private readonly IAzureArmClientFactory _armFactory;

    public AzureManagedClusterCollectionAdapter(IAzureArmClientFactory armFactory)
    {
        _armFactory = armFactory;
    }

    public async IAsyncEnumerable<AzureAksClusterRow> ListAsync(
        string subscriptionId,
        string? resourceGroup,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var arm = _armFactory.Create(subscriptionId);
        var subscription = arm.GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(subscriptionId));

        if (string.IsNullOrEmpty(resourceGroup))
        {
            await foreach (var cluster in subscription.GetContainerServiceManagedClustersAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return Project(cluster);
            }
        }
        else
        {
            // Resource-group scoped listing — saves one ARM round-trip on the common
            // "ops investigates a single env" path.
            var rg = await subscription.GetResourceGroupAsync(resourceGroup, cancellationToken).ConfigureAwait(false);
            var collection = rg.Value.GetContainerServiceManagedClusters();
            await foreach (var cluster in collection.GetAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return Project(cluster);
            }
        }
    }

    public async Task<byte[]> GetClusterUserKubeconfigAsync(string resourceId, CancellationToken cancellationToken)
    {
        // ResourceIdentifier carries the subscription / RG / cluster name; ArmClient
        // is resolved from the cluster's subscription scope so we always use the
        // caller's credential. listClusterUserCredential requires the
        // "Azure Kubernetes Service Cluster User Role" — 403 surfaces as RequestFailedException.
        var identifier = new global::Azure.Core.ResourceIdentifier(resourceId);
        var arm = _armFactory.Create(identifier.SubscriptionId!);
        var resource = arm.GetContainerServiceManagedClusterResource(identifier);
        var credentials = await resource.GetClusterUserCredentialsAsync(
            serverFqdn: null,
            format: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var kubeconfigs = credentials.Value?.Kubeconfigs;
        var kubeconfig = kubeconfigs is { Count: > 0 } ? kubeconfigs[0] : null;
        if (kubeconfig?.Value is null || kubeconfig.Value.Length == 0)
        {
            throw new InvalidOperationException(
                "Azure returned an empty kubeconfig payload from listClusterUserCredential.");
        }

        // Defensive copy — the SDK owns the response buffer, and we hand the bytes
        // straight to the kubeconfig handle store which expects ownership.
        var bytes = new byte[kubeconfig.Value.Length];
        Buffer.BlockCopy(kubeconfig.Value, 0, bytes, 0, kubeconfig.Value.Length);
        return bytes;
    }

    private static AzureAksClusterRow Project(ContainerServiceManagedClusterResource cluster)
    {
        var data = cluster.Data;
        var isPrivate = data?.ApiServerAccessProfile?.IsPrivateClusterEnabled == true;
        var fqdn = isPrivate ? null : data?.Fqdn;
        var k8sVersion = data?.CurrentKubernetesVersion ?? data?.KubernetesVersion;
        var agentPoolCount = data?.AgentPoolProfiles?.Count ?? 0;

        return new AzureAksClusterRow(
            ResourceId: cluster.Id.ToString(),
            Name: data?.Name ?? cluster.Id.Name,
            Location: data?.Location.Name ?? string.Empty,
            AgentPoolCount: agentPoolCount,
            Fqdn: fqdn,
            KubernetesVersion: k8sVersion,
            NodeResourceGroup: data?.NodeResourceGroup,
            IsPrivateCluster: isPrivate);
    }
}

/// <summary>
/// Lightweight exception envelope the adapter surfaces when ARM rejects the
/// listClusterUserCredential call with a permission error. The backend catches it
/// and folds the result into a per-cluster readinessWarning instead of failing the
/// entire listing.
/// </summary>
internal static class AzureAksAdapterExceptions
{
    public static bool IsForbidden(this RequestFailedException ex) =>
        ex.Status == (int)HttpStatusCode.Forbidden || ex.Status == (int)HttpStatusCode.Unauthorized;
}
