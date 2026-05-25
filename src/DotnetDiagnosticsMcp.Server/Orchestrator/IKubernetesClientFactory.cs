using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
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
    /// <remarks>
    /// #234 — when the ambient <see cref="IKubeconfigContext.CurrentHandle"/> is non-null,
    /// the factory resolves the handle through <see cref="IKubeconfigHandleStore"/> and
    /// returns an ephemeral client built from the in-memory bytes (cached per-handle for
    /// the duration of the handle's lifetime — the bytes never touch disk). When no
    /// handle is active, the legacy in-cluster / kubeconfig discovery is used.
    /// </remarks>
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
public class KubernetesConfigurationException : Exception
{
    public KubernetesConfigurationException(string message) : base(message) { }
    public KubernetesConfigurationException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when <see cref="IKubeconfigContext.CurrentHandle"/> references a handle that
/// the <see cref="IKubeconfigHandleStore"/> cannot resolve (unknown or expired). The
/// orchestrator tool layer translates this into a structured
/// <see cref="OrchestratorErrorKinds.KubeconfigHandleNotFound"/> /
/// <see cref="OrchestratorErrorKinds.KubeconfigHandleExpired"/> envelope.
/// </summary>
public sealed class KubeconfigHandleNotFoundException : KubernetesConfigurationException
{
    public KubeconfigHandleNotFoundException(string message) : base(message) { }
}

/// <summary>
/// Default factory: prefers in-cluster ServiceAccount projection, falls back to the
/// caller's kubeconfig (<c>KUBECONFIG</c> env var or <c>~/.kube/config</c>) for
/// out-of-cluster operator use. When an AKS kubeconfig handle is active on the
/// ambient <see cref="IKubeconfigContext"/>, it overrides both paths.
/// </summary>
/// <remarks>
/// The default client is constructed lazily on first call and cached. Per-handle
/// clients are cached separately (keyed by handle id) so concurrent investigations
/// against different clusters don't reload the same bytes. Construction failures are
/// re-thrown on every call so the orchestrator surfaces a fresh error each time the
/// LLM retries instead of caching a permanent failure state.
/// </remarks>
internal sealed class DefaultKubernetesClientFactory : IKubernetesClientFactory
{
    private readonly ILogger<DefaultKubernetesClientFactory> _logger;
    private readonly IKubeconfigContext _kubeconfigContext;
    private readonly IKubeconfigHandleStore _kubeconfigStore;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, IKubernetes> _handleClients = new(StringComparer.Ordinal);
    private IKubernetes? _client;

    public DefaultKubernetesClientFactory(
        ILogger<DefaultKubernetesClientFactory> logger,
        IKubeconfigContext kubeconfigContext,
        IKubeconfigHandleStore kubeconfigStore)
    {
        _logger = logger;
        _kubeconfigContext = kubeconfigContext;
        _kubeconfigStore = kubeconfigStore;
    }

    public IKubernetes GetClient()
    {
        var handle = _kubeconfigContext.CurrentHandle;
        if (!string.IsNullOrEmpty(handle))
        {
            return GetOrBuildHandleClient(handle);
        }

        if (_client is not null) return _client;
        lock (_gate)
        {
            if (_client is not null) return _client;
            _client = Build();
            return _client;
        }
    }

    private IKubernetes GetOrBuildHandleClient(string handle)
    {
        // We do not key the cache on the bytes themselves, because that would force
        // us to keep a hash of the kubeconfig in memory. Handles already round-trip
        // through a one-time mint, so caching by handle id is equivalent.
        if (_handleClients.TryGetValue(handle, out var cached))
        {
            return cached;
        }

        var bytes = _kubeconfigStore.TryResolve(handle);
        if (bytes is null)
        {
            // Never log the handle value — treat it as a bearer credential. The
            // message intentionally elides the handle so a server log scrape can't
            // identify which handle expired.
            _logger.LogWarning("Kubeconfig handle resolution failed (handle present: true, resolved: false). Returning KubeconfigHandleNotFound to caller.");
            throw new KubeconfigHandleNotFoundException(
                "Kubeconfig handle is unknown or has expired. Re-run discover_azure(kind=aksclusters, includeKubeconfig=true) to mint a fresh handle.");
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var config = KubernetesClientConfiguration.BuildConfigFromConfigFile(stream);
            var built = new Kubernetes(config);

            if (!_handleClients.TryAdd(handle, built))
            {
                // Lost the race; dispose ours and return the existing one. Disposing
                // also closes the underlying HttpClient so we don't leak sockets.
                built.Dispose();
                return _handleClients[handle];
            }
            return built;
        }
        finally
        {
            // The defensive copy from TryResolve never enters any other surface —
            // overwrite immediately so a heap walker can't lift it back out.
            Array.Clear(bytes, 0, bytes.Length);
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

