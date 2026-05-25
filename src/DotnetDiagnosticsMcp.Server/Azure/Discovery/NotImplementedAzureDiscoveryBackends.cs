using System;
using System.Threading;
using System.Threading.Tasks;

namespace DotnetDiagnosticsMcp.Server.Azure.Discovery;

/// <summary>
/// Default <see cref="IAzureWebAppsDiscovery"/> implementation. Throws on every call —
/// the real backend lands in #233.
/// </summary>
internal sealed class NotImplementedAzureWebAppsDiscovery : IAzureWebAppsDiscovery
{
    public Task<AzurePagedResult<AzureWebAppCandidate>> ListAsync(
        AzureDiscoveryRequest request, CancellationToken cancellationToken)
        => throw new NotImplementedException(
            "Azure App Service discovery is implemented in PR #233 (parent #230).");
}

/// <summary>
/// Default <see cref="IAzureContainerAppsDiscovery"/> implementation. Throws on every call —
/// the real backend lands in #233.
/// </summary>
internal sealed class NotImplementedAzureContainerAppsDiscovery : IAzureContainerAppsDiscovery
{
    public Task<AzurePagedResult<AzureContainerAppCandidate>> ListAsync(
        AzureDiscoveryRequest request, CancellationToken cancellationToken)
        => throw new NotImplementedException(
            "Azure Container Apps discovery is implemented in PR #233 (parent #230).");
}

/// <summary>
/// Default <see cref="IAzureAksDiscovery"/> implementation. Throws on every call —
/// the real backend lands in #234.
/// </summary>
internal sealed class NotImplementedAzureAksDiscovery : IAzureAksDiscovery
{
    public Task<AzurePagedResult<AzureAksClusterCandidate>> ListAsync(
        AzureDiscoveryRequest request, CancellationToken cancellationToken)
        => throw new NotImplementedException(
            "Azure AKS discovery is implemented in PR #234 (parent #230).");
}
