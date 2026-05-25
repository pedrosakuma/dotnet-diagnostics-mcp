using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.ResourceManager.AppContainers;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.Resources;

namespace DotnetDiagnosticsMcp.Server.Azure.Discovery;

/// <summary>
/// One page of <typeparamref name="T"/> rows plus the Azure SDK continuation token
/// (opaque string). Mirrors Azure SDK <c>Page&lt;T&gt;</c> without leaking the Azure
/// SDK dependency onto the discovery backends, so the unit tests can fake the
/// adapter without referencing <c>Azure.Core</c>.
/// </summary>
internal sealed record AzurePage<T>(IReadOnlyList<T> Items, string? ContinuationToken);

/// <summary>
/// Snapshot of a single App Service site, decoupled from
/// <see cref="WebSiteResource"/> / <see cref="WebSiteData"/> so the
/// <see cref="DefaultAzureWebAppsDiscovery"/> backend can be unit-tested with
/// an in-memory adapter. Mirrors the seam used by <c>IKubernetesPodsApi</c>.
/// </summary>
internal sealed record AzureWebSiteSnapshot(
    string ResourceId,
    string Name,
    string Location,
    string State,
    string Kind,
    string? DefaultHostName,
    string? LinuxFxVersion,
    string? NetFrameworkVersion,
    int? NumberOfWorkers);

/// <summary>
/// Snapshot of a single Container App, decoupled from
/// <see cref="ContainerAppResource"/> / <see cref="ContainerAppData"/>.
/// </summary>
internal sealed record AzureContainerAppSnapshot(
    string ResourceId,
    string Name,
    string Location,
    IReadOnlyList<string> ContainerImages,
    string ProvisioningState,
    string RunningState,
    string? LatestRevisionFqdn,
    int? MinReplicas,
    int? MaxReplicas,
    int ContainerCount);

/// <summary>
/// Thin adapter over <see cref="AppServiceExtensions.GetWebSitesAsync(SubscriptionResource, CancellationToken)"/>
/// and <see cref="AppServiceExtensions.GetWebSites(ResourceGroupResource)"/>. The
/// real implementation calls into the Azure SDK; the unit tests substitute a
/// fake that yields pre-baked <see cref="AzurePage{T}"/> instances.
/// </summary>
internal interface IAzureWebSiteCollectionAdapter
{
    IAsyncEnumerable<AzurePage<AzureWebSiteSnapshot>> GetSitesAsPagesAsync(
        string subscriptionId,
        string? resourceGroup,
        int pageSize,
        string? continuationToken,
        CancellationToken cancellationToken);
}

/// <summary>
/// Thin adapter over <see cref="AppContainersExtensions.GetContainerAppsAsync(SubscriptionResource, CancellationToken)"/>
/// and <see cref="AppContainersExtensions.GetContainerApps(ResourceGroupResource)"/>.
/// </summary>
internal interface IAzureContainerAppCollectionAdapter
{
    IAsyncEnumerable<AzurePage<AzureContainerAppSnapshot>> GetContainerAppsAsPagesAsync(
        string subscriptionId,
        string? resourceGroup,
        int pageSize,
        string? continuationToken,
        CancellationToken cancellationToken);
}

/// <summary>
/// Production <see cref="IAzureWebSiteCollectionAdapter"/>: pass-through to the
/// Azure Resource Manager App Service SDK.
/// </summary>
internal sealed class DefaultAzureWebSiteCollectionAdapter : IAzureWebSiteCollectionAdapter
{
    private readonly IAzureArmClientFactory _factory;

    public DefaultAzureWebSiteCollectionAdapter(IAzureArmClientFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public async IAsyncEnumerable<AzurePage<AzureWebSiteSnapshot>> GetSitesAsPagesAsync(
        string subscriptionId,
        string? resourceGroup,
        int pageSize,
        string? continuationToken,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = _factory.Create(subscriptionId);
        var subId = SubscriptionResource.CreateResourceIdentifier(subscriptionId);
        var sub = client.GetSubscriptionResource(subId);

        AsyncPageable<WebSiteResource> pageable;
        if (string.IsNullOrWhiteSpace(resourceGroup))
        {
            pageable = sub.GetWebSitesAsync(cancellationToken);
        }
        else
        {
            var rg = await sub.GetResourceGroupAsync(resourceGroup, cancellationToken).ConfigureAwait(false);
            pageable = rg.Value.GetWebSites().GetAllAsync(cancellationToken: cancellationToken);
        }

        await foreach (var page in pageable
            .AsPages(continuationToken, pageSize)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            var items = new List<AzureWebSiteSnapshot>(page.Values.Count);
            foreach (var site in page.Values)
            {
                var data = site.Data;
                items.Add(new AzureWebSiteSnapshot(
                    ResourceId: data.Id?.ToString() ?? string.Empty,
                    Name: data.Name ?? string.Empty,
                    Location: data.Location.Name ?? string.Empty,
                    State: data.State ?? "Unknown",
                    Kind: data.Kind ?? string.Empty,
                    DefaultHostName: data.DefaultHostName,
                    LinuxFxVersion: data.SiteConfig?.LinuxFxVersion,
                    NetFrameworkVersion: data.SiteConfig?.NetFrameworkVersion,
                    NumberOfWorkers: data.SiteConfig?.NumberOfWorkers));
            }
            yield return new AzurePage<AzureWebSiteSnapshot>(items, page.ContinuationToken);
        }
    }
}

/// <summary>
/// Production <see cref="IAzureContainerAppCollectionAdapter"/>: pass-through to
/// the Azure Resource Manager Container Apps SDK.
/// </summary>
internal sealed class DefaultAzureContainerAppCollectionAdapter : IAzureContainerAppCollectionAdapter
{
    private readonly IAzureArmClientFactory _factory;

    public DefaultAzureContainerAppCollectionAdapter(IAzureArmClientFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public async IAsyncEnumerable<AzurePage<AzureContainerAppSnapshot>> GetContainerAppsAsPagesAsync(
        string subscriptionId,
        string? resourceGroup,
        int pageSize,
        string? continuationToken,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = _factory.Create(subscriptionId);
        var subId = SubscriptionResource.CreateResourceIdentifier(subscriptionId);
        var sub = client.GetSubscriptionResource(subId);

        AsyncPageable<ContainerAppResource> pageable;
        if (string.IsNullOrWhiteSpace(resourceGroup))
        {
            pageable = sub.GetContainerAppsAsync(cancellationToken);
        }
        else
        {
            var rg = await sub.GetResourceGroupAsync(resourceGroup, cancellationToken).ConfigureAwait(false);
            pageable = rg.Value.GetContainerApps().GetAllAsync(cancellationToken: cancellationToken);
        }

        await foreach (var page in pageable
            .AsPages(continuationToken, pageSize)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            var items = new List<AzureContainerAppSnapshot>(page.Values.Count);
            foreach (var app in page.Values)
            {
                var data = app.Data;
                var template = data.Template;
                var images = new List<string>();
                var containerCount = 0;
                if (template?.Containers is not null)
                {
                    foreach (var c in template.Containers)
                    {
                        containerCount++;
                        if (!string.IsNullOrEmpty(c.Image))
                        {
                            images.Add(c.Image);
                        }
                    }
                }

                items.Add(new AzureContainerAppSnapshot(
                    ResourceId: data.Id?.ToString() ?? string.Empty,
                    Name: data.Name ?? string.Empty,
                    Location: data.Location.Name ?? string.Empty,
                    ContainerImages: images,
                    ProvisioningState: data.ProvisioningState?.ToString() ?? "Unknown",
                    RunningState: data.RunningStatus?.ToString() ?? "Unknown",
                    LatestRevisionFqdn: data.LatestRevisionFqdn,
                    MinReplicas: template?.Scale?.MinReplicas,
                    MaxReplicas: template?.Scale?.MaxReplicas,
                    ContainerCount: containerCount));
            }
            yield return new AzurePage<AzureContainerAppSnapshot>(items, page.ContinuationToken);
        }
    }
}
