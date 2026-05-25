using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DotnetDiagnosticsMcp.Server.Azure.Discovery;

/// <summary>
/// Production <see cref="IAzureWebAppsDiscovery"/> implementation (issue #233).
/// Consumes <see cref="IAzureWebSiteCollectionAdapter"/> so the backend can be
/// unit-tested with a fake adapter while still honoring the Azure SDK's native
/// continuation-token paging at runtime.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="ListAsync"/> call consumes exactly one Azure SDK page. The
/// adapter's continuation token is passed through verbatim as the
/// <see cref="AzurePagedResult{T}.NextCursor"/>; the tool surface treats it as
/// opaque. Function apps (<c>kind</c> containing <c>functionapp</c>) are filtered
/// out per Q1 in the design discussion — sidecar diagnostics is out of scope for
/// Functions. Per-page filtering (function apps, stopped sites) may shrink the
/// result list below <c>request.Limit</c>; the cursor still advances so the LLM
/// can fetch the next page if needed.
/// </para>
/// <para>
/// No app settings or connection strings are surfaced. Only metadata exposed by
/// the <c>WebSiteData</c> envelope is mapped.
/// </para>
/// </remarks>
internal sealed class DefaultAzureWebAppsDiscovery : IAzureWebAppsDiscovery
{
    private const string FunctionAppKindMarker = "functionapp";
    private const string LinuxKindMarker = "linux";
    private const string StoppedState = "Stopped";

    private readonly IAzureWebSiteCollectionAdapter _adapter;

    public DefaultAzureWebAppsDiscovery(IAzureWebSiteCollectionAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _adapter = adapter;
    }

    public async Task<AzurePagedResult<AzureWebAppCandidate>> ListAsync(
        AzureDiscoveryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await foreach (var page in _adapter
            .GetSitesAsPagesAsync(request.SubscriptionId, request.ResourceGroup, request.Limit, request.Cursor, cancellationToken)
            .ConfigureAwait(false))
        {
            var items = new List<AzureWebAppCandidate>(page.Items.Count);
            foreach (var site in page.Items)
            {
                // Functions are out of scope (Q1): the sidecar-based diagnostic socket
                // approach does not apply to the Functions Consumption / Premium hosts.
                if (site.Kind.Contains(FunctionAppKindMarker, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!request.IncludeStopped &&
                    string.Equals(site.State, StoppedState, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var warnings = new List<string>();
                if (!site.Kind.Contains(LinuxKindMarker, StringComparison.OrdinalIgnoreCase))
                {
                    // Sidecar attach relies on the Linux App Service multi-container surface;
                    // Windows sites have no equivalent multi-container topology today.
                    warnings.Add("Windows OS — sidecar not supported");
                }

                var (runtimeStack, runtimeVersion) = ResolveRuntime(site.LinuxFxVersion, site.NetFrameworkVersion);

                items.Add(new AzureWebAppCandidate(
                    ResourceId: site.ResourceId,
                    Name: site.Name,
                    Location: site.Location,
                    State: site.State,
                    Kind: site.Kind,
                    ReadinessWarnings: warnings,
                    DefaultHostName: site.DefaultHostName,
                    RuntimeStack: runtimeStack,
                    RuntimeVersion: runtimeVersion,
                    InstanceCount: site.NumberOfWorkers));
            }

            return new AzurePagedResult<AzureWebAppCandidate>(items, page.ContinuationToken);
        }

        // Adapter yielded zero pages — return an empty result.
        return new AzurePagedResult<AzureWebAppCandidate>(Array.Empty<AzureWebAppCandidate>(), null);
    }

    private static (string? Stack, string? Version) ResolveRuntime(string? linuxFxVersion, string? netFrameworkVersion)
    {
        if (!string.IsNullOrEmpty(linuxFxVersion))
        {
            // linuxFxVersion is "STACK|VERSION" — e.g. "DOTNETCORE|8.0".
            var bar = linuxFxVersion.IndexOf('|');
            if (bar > 0 && bar < linuxFxVersion.Length - 1)
            {
                return (linuxFxVersion, linuxFxVersion[(bar + 1)..]);
            }
            return (linuxFxVersion, null);
        }
        if (!string.IsNullOrEmpty(netFrameworkVersion))
        {
            return (netFrameworkVersion, netFrameworkVersion);
        }
        return (null, null);
    }
}

/// <summary>
/// Production <see cref="IAzureContainerAppsDiscovery"/> implementation
/// (issue #233). Same shape as <see cref="DefaultAzureWebAppsDiscovery"/>:
/// adapter-mediated, one Azure SDK page per call, opaque cursor pass-through.
/// </summary>
internal sealed class DefaultAzureContainerAppsDiscovery : IAzureContainerAppsDiscovery
{
    private const string StoppedState = "Stopped";

    private readonly IAzureContainerAppCollectionAdapter _adapter;

    public DefaultAzureContainerAppsDiscovery(IAzureContainerAppCollectionAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _adapter = adapter;
    }

    public async Task<AzurePagedResult<AzureContainerAppCandidate>> ListAsync(
        AzureDiscoveryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await foreach (var page in _adapter
            .GetContainerAppsAsPagesAsync(request.SubscriptionId, request.ResourceGroup, request.Limit, request.Cursor, cancellationToken)
            .ConfigureAwait(false))
        {
            var items = new List<AzureContainerAppCandidate>(page.Items.Count);
            foreach (var app in page.Items)
            {
                if (!request.IncludeStopped &&
                    string.Equals(app.RunningState, StoppedState, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var warnings = new List<string>();
                if (app.ContainerCount <= 1)
                {
                    // The sidecar topology requires a second container in the same revision
                    // (the dotnet-diagnostics-mcp container). A single-container revision
                    // means the app hasn't been migrated yet.
                    warnings.Add("No second container detected — sidecar topology not deployed");
                }
                if (app.MinReplicas == 0)
                {
                    // MinReplicas=0 means the app may be scaled to zero and unreachable
                    // for diagnostics. We don't probe live replica count to avoid an
                    // extra API call; this is a best-effort hint.
                    warnings.Add("Scale=0");
                }

                items.Add(new AzureContainerAppCandidate(
                    ResourceId: app.ResourceId,
                    Name: app.Name,
                    Location: app.Location,
                    ContainerImages: app.ContainerImages,
                    ProvisioningState: app.ProvisioningState,
                    RunningState: app.RunningState,
                    ReadinessWarnings: warnings,
                    LatestRevisionFqdn: app.LatestRevisionFqdn,
                    MinReplicas: app.MinReplicas,
                    MaxReplicas: app.MaxReplicas));
            }

            return new AzurePagedResult<AzureContainerAppCandidate>(items, page.ContinuationToken);
        }

        return new AzurePagedResult<AzureContainerAppCandidate>(Array.Empty<AzureContainerAppCandidate>(), null);
    }
}
