using System.Threading;
using System.Threading.Tasks;
using k8s;
using k8s.Models;

namespace DotnetDiagnosticsMcp.Server.Orchestrator;

/// <summary>
/// Production implementation of <see cref="IKubernetesPodsApi"/>: a thin pass-through
/// to the official <see cref="IKubernetes"/> client.
/// </summary>
internal sealed class KubernetesPodsApi : IKubernetesPodsApi
{
    private readonly IKubernetesClientFactory _factory;

    public KubernetesPodsApi(IKubernetesClientFactory factory)
    {
        _factory = factory;
    }

    public Task<V1PodList> ListPodsAsync(
        string? namespaceName,
        string? labelSelector,
        string? fieldSelector,
        int? limit,
        string? continueToken,
        CancellationToken cancellationToken)
    {
        var client = _factory.GetClient();
        if (string.IsNullOrEmpty(namespaceName))
        {
            return client.CoreV1.ListPodForAllNamespacesAsync(
                labelSelector: labelSelector,
                fieldSelector: fieldSelector,
                limit: limit,
                continueParameter: continueToken,
                cancellationToken: cancellationToken);
        }

        return client.CoreV1.ListNamespacedPodAsync(
            namespaceParameter: namespaceName,
            labelSelector: labelSelector,
            fieldSelector: fieldSelector,
            limit: limit,
            continueParameter: continueToken,
            cancellationToken: cancellationToken);
    }
}
