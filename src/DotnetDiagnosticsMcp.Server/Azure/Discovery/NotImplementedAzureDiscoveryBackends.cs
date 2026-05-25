using System;
using System.Threading;
using System.Threading.Tasks;

namespace DotnetDiagnosticsMcp.Server.Azure.Discovery;

/// <summary>
/// Fallback <see cref="IAzureAksDiscovery"/> implementation. Throws on every call.
/// </summary>
/// <remarks>
/// Replaced in #234 by <see cref="AzureAksDiscovery"/> and no longer registered by
/// <c>AddAzureDiscoveryServices</c>; the type is retained so any external wiring
/// that still references it surfaces a deterministic error rather than silently
/// returning an empty result.
/// </remarks>
internal sealed class NotImplementedAzureAksDiscovery : IAzureAksDiscovery
{
    public Task<AzurePagedResult<AzureAksClusterCandidate>> ListAsync(
        AzureDiscoveryRequest request, CancellationToken cancellationToken)
        => throw new NotImplementedException(
            "Azure AKS discovery is implemented by AzureAksDiscovery as of PR #234 (parent #230).");
}
