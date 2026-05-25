using System;
using System.Threading;
using System.Threading.Tasks;

namespace DotnetDiagnosticsMcp.Server.Azure.Discovery;

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
