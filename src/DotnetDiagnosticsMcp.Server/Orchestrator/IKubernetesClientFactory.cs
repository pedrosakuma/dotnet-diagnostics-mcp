using System;
using System.IO;
using k8s;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnosticsMcp.Server.Orchestrator;

/// <summary>
/// Creates the in-process <see cref="IKubernetes"/> client the orchestrator uses to
/// talk to the cluster. Abstracted so tests can substitute a fake without spinning up
/// a kubeconfig or projected ServiceAccount token.
/// </summary>
public interface IKubernetesClientFactory
{
    /// <summary>
    /// Returns a configured Kubernetes client. The instance is owned by the factory and
    /// must NOT be disposed by callers — it is shared for the lifetime of the process.
    /// </summary>
    /// <returns>A live client when configuration was discoverable.</returns>
    /// <exception cref="KubernetesConfigurationException">
    /// Thrown when neither in-cluster ServiceAccount projection nor a kubeconfig file are
    /// reachable. The thrown message is the one the orchestrator surfaces to the LLM as
    /// the <c>KubeApiUnavailable</c> error envelope.
    /// </exception>
    IKubernetes GetClient();
}

/// <summary>
/// Thrown when the orchestrator cannot resolve cluster credentials. Distinct from
/// transport errors that surface from individual kube API calls.
/// </summary>
public sealed class KubernetesConfigurationException : Exception
{
    public KubernetesConfigurationException(string message) : base(message) { }
    public KubernetesConfigurationException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Default factory: prefers in-cluster ServiceAccount projection, falls back to the
/// caller's kubeconfig (<c>KUBECONFIG</c> env var or <c>~/.kube/config</c>) for
/// out-of-cluster operator use.
/// </summary>
/// <remarks>
/// The client is constructed lazily on first call and cached. Construction failures are
/// re-thrown on every call so the orchestrator surfaces a fresh error each time the LLM
/// retries instead of caching a permanent failure state.
/// </remarks>
internal sealed class DefaultKubernetesClientFactory : IKubernetesClientFactory
{
    private readonly ILogger<DefaultKubernetesClientFactory> _logger;
    private readonly object _gate = new();
    private IKubernetes? _client;

    public DefaultKubernetesClientFactory(ILogger<DefaultKubernetesClientFactory> logger)
    {
        _logger = logger;
    }

    public IKubernetes GetClient()
    {
        if (_client is not null) return _client;
        lock (_gate)
        {
            if (_client is not null) return _client;
            _client = Build();
            return _client;
        }
    }

    private Kubernetes Build()
    {
        KubernetesClientConfiguration? config = null;

        if (KubernetesClientConfiguration.IsInCluster())
        {
            try
            {
                config = KubernetesClientConfiguration.InClusterConfig();
                _logger.LogInformation("Kubernetes client configured from in-cluster ServiceAccount projection.");
            }
            catch (Exception ex)
            {
                throw new KubernetesConfigurationException(
                    "Detected in-cluster environment but failed to load ServiceAccount projection.", ex);
            }
        }
        else
        {
            try
            {
                config = KubernetesClientConfiguration.BuildDefaultConfig();
                _logger.LogInformation("Kubernetes client configured from kubeconfig (out-of-cluster).");
            }
            catch (FileNotFoundException ex)
            {
                throw new KubernetesConfigurationException(
                    "No in-cluster ServiceAccount projection and no kubeconfig found. " +
                    "Set KUBECONFIG or run in-cluster.", ex);
            }
            catch (Exception ex)
            {
                throw new KubernetesConfigurationException(
                    "Failed to build Kubernetes client from kubeconfig.", ex);
            }
        }

        return new Kubernetes(config);
    }
}
