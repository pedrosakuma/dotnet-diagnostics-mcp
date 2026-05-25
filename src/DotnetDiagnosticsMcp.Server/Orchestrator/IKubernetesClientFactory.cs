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
/// <para>
/// FIX 3 (#234 review): the per-handle cache is tied to the underlying
/// <see cref="IKubeconfigHandleStore"/> lifecycle. On every cache HIT we re-validate
/// the handle against the store; if it has expired we evict the cached client
/// (disposing its embedded HTTP credentials) and surface the same
/// <see cref="KubeconfigHandleNotFoundException"/> as the cold-miss expired path.
/// We also subscribe to <see cref="IKubeconfigHandleStore.HandleEvicted"/> so a
/// background TTL sweep or capacity eviction proactively kills the cached client
/// rather than waiting for the next request to discover the dangling state.
/// </para>
/// <para>
/// FIX 4 (#234 review, gpt-5.5 2nd pass): close the post-TryAdd race. The
/// HandleEvicted subscription only cleans up cache entries that already exist; if
/// the store evicts the handle BEFORE our TryAdd installs the entry, the handler
/// has nothing to remove and the freshly added entry is stale. We therefore re-peek
/// the expiry AFTER TryAdd and roll back the install (TryRemove + Dispose) if the
/// store no longer recognizes the handle, or recognizes it under a different expiry
/// — both signal a race-with-eviction we must surface as KubeconfigHandleNotFound.
/// </para>
/// <para>
/// FIX 5 (#234 review, gpt-5.5 3rd pass): close the TryAdd-loser sibling race.
/// FIX 4 covers the thread that wins TryAdd, but a concurrent caller losing TryAdd
/// against a stale winner-installed entry could still observe a matching
/// <c>existing.ExpiresAt</c> (both equal to the pre-eviction value the two
/// captured) and escape with a client for an already-evicted handle — BEFORE the
/// winner runs its post-add rollback. We mirror the winner-side guard on the loser
/// branch: re-peek the store and only return the cached client when the live
/// expiry, the captured expiry, and the cached entry's expiry all agree;
/// otherwise evict + dispose + throw the same not-found envelope.
/// </para>
/// </remarks>
internal sealed class DefaultKubernetesClientFactory : IKubernetesClientFactory, IDisposable
{
    private readonly ILogger<DefaultKubernetesClientFactory> _logger;
    private readonly IKubeconfigContext _kubeconfigContext;
    private readonly IKubeconfigHandleStore _kubeconfigStore;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, HandleCacheEntry> _handleClients = new(StringComparer.Ordinal);
    private Kubernetes? _client;

    public DefaultKubernetesClientFactory(
        ILogger<DefaultKubernetesClientFactory> logger,
        IKubeconfigContext kubeconfigContext,
        IKubeconfigHandleStore kubeconfigStore)
    {
        _logger = logger;
        _kubeconfigContext = kubeconfigContext;
        _kubeconfigStore = kubeconfigStore;

        // FIX 3 — proactive cache invalidation on store-driven eviction. The
        // subscription is balanced in Dispose so the factory does not leak event
        // wiring across container rebuilds in tests.
        _kubeconfigStore.HandleEvicted += OnHandleEvicted;
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
        // FIX 3 — re-validate every cache hit against the store. The cached client
        // embeds the kubeconfig credential material in its HttpClient pipeline; once
        // the store no longer recognizes the handle, the client must die with it.
        if (_handleClients.TryGetValue(handle, out var cached))
        {
            var liveExpiry = _kubeconfigStore.TryPeekExpiry(handle);
            if (liveExpiry is not null && liveExpiry.Value == cached.ExpiresAt)
            {
                return cached.Client;
            }

            // Either the store has evicted/expired the handle, or it was re-minted
            // with a different expiry. Either way, the cached client is dead.
            EvictHandleClient(handle);
            // Fall through to the cold-miss path so we hand back a unified error or
            // build a fresh client if the store still resolves the handle.
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

        var expiresAt = _kubeconfigStore.TryPeekExpiry(handle);

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var config = KubernetesClientConfiguration.BuildConfigFromConfigFile(stream);
            var built = new Kubernetes(config);

            // If TryPeekExpiry came back null after a successful TryResolve we lost
            // a race with eviction — fall through and surface "not found" rather
            // than caching a client we cannot prove is live.
            if (expiresAt is null)
            {
                built.Dispose();
                throw new KubeconfigHandleNotFoundException(
                    "Kubeconfig handle expired during client construction. Re-run discover_azure to mint a fresh handle.");
            }

            var entry = new HandleCacheEntry(built, expiresAt.Value);
            if (!_handleClients.TryAdd(handle, entry))
            {
                // Lost the race; dispose ours and reuse the existing one — but
                // only after re-validating against the store. FIX 5 (#234 review,
                // gpt-5.5 3rd pass): the winner-path post-add re-peek (below) is
                // not enough on its own. If the store evicted the handle between
                // our captured TryPeekExpiry and the TryAdd above, the winner
                // installed a stale entry. A loser arriving here would otherwise
                // observe `existing.ExpiresAt == expiresAt.Value` (both equal to
                // the pre-eviction value), return `existing.Client`, and escape
                // with a client for an already-evicted handle BEFORE the winner
                // ran its rollback. Re-peek the store one more time and only
                // accept the cached entry when all three values agree (live
                // expiry == captured expiry == cached expiry); on any drift,
                // evict + dispose and surface the same not-found envelope as
                // the winner-path rollback.
                built.Dispose();
                var loserLiveExpiry = _kubeconfigStore.TryPeekExpiry(handle);
                if (loserLiveExpiry is not null
                    && loserLiveExpiry.Value == expiresAt.Value
                    && _handleClients.TryGetValue(handle, out var existing)
                    && existing.ExpiresAt == expiresAt.Value
                    && loserLiveExpiry.Value == existing.ExpiresAt)
                {
                    return existing.Client;
                }
                EvictHandleClient(handle);
                throw new KubeconfigHandleNotFoundException(
                    "Kubeconfig handle expired during client construction. Re-run discover_azure to mint a fresh handle.");
            }

            // FIX 4 (#234 review, gpt-5.5 2nd pass): close the post-eviction race.
            // Between the earlier TryResolve/TryPeekExpiry capture and the TryAdd above,
            // the store may have evicted (TTL sweep / capacity overflow / disposal) or
            // re-minted this handle. If the eviction event fired BEFORE our TryAdd
            // succeeded, our HandleEvicted subscriber found nothing to remove — the
            // cache entry we just installed is stale and the subscriber will never
            // run again for that prior eviction. Re-validate against the store one
            // more time; on any drift, evict + dispose + surface the same not-found
            // envelope as the cold-miss path.
            var postAddExpiry = _kubeconfigStore.TryPeekExpiry(handle);
            if (postAddExpiry is null || postAddExpiry.Value != expiresAt.Value)
            {
                EvictHandleClient(handle);
                throw new KubeconfigHandleNotFoundException(
                    "Kubeconfig handle was evicted during client construction. Re-run discover_azure to mint a fresh handle.");
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

    private void OnHandleEvicted(object? sender, KubeconfigHandleEvictedEventArgs e)
    {
        EvictHandleClient(e.Handle);
    }

    private void EvictHandleClient(string handle)
    {
        if (_handleClients.TryRemove(handle, out var evicted))
        {
            try { evicted.Client.Dispose(); }
            catch { /* defensive: a faulty Dispose must not leak past the cache */ }
        }
    }

    public void Dispose()
    {
        _kubeconfigStore.HandleEvicted -= OnHandleEvicted;
        foreach (var kv in _handleClients)
        {
            try { kv.Value.Client.Dispose(); }
            catch { /* swallow */ }
        }
        _handleClients.Clear();
        try { _client?.Dispose(); }
        catch { /* swallow */ }
    }

    private readonly record struct HandleCacheEntry(IKubernetes Client, DateTimeOffset ExpiresAt);

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

