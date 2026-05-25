using System;
using System.Text;
using DotnetDiagnosticsMcp.Server.Azure;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Orchestrator;

/// <summary>
/// FIX 3 (#234 review): the per-handle <c>IKubernetes</c> cache in
/// <see cref="DefaultKubernetesClientFactory"/> must NOT outlive the underlying
/// kubeconfig handle. Asserts that:
/// <list type="bullet">
///   <item>A cache hit re-validates against the store and refuses stale entries.</item>
///   <item>The factory subscribes to <see cref="IKubeconfigHandleStore.HandleEvicted"/>
///     and proactively disposes its cached client.</item>
/// </list>
/// </summary>
public sealed class KubeconfigClientCacheEvictionTests
{
    private static readonly byte[] MinimalKubeconfig = Encoding.UTF8.GetBytes(
        "apiVersion: v1\n" +
        "kind: Config\n" +
        "clusters:\n- name: t\n  cluster:\n    server: https://fake.example\n" +
        "users:\n- name: t\n  user:\n    token: opaque\n" +
        "contexts:\n- name: t\n  context:\n    cluster: t\n    user: t\n" +
        "current-context: t\n");

    [Fact]
    public void CachedClient_AfterStoreEviction_IsDropped_AndNewCallReturnsHandleNotFound()
    {
        var clock = new ManualClock();
        var store = new InMemoryKubeconfigHandleStore(
            new AzureDiscoveryOptions
            {
                Enabled = true,
                KubeconfigHandleTtl = TimeSpan.FromMinutes(10),
                KubeconfigHandleMaxEntries = 1, // forces capacity eviction on the second Register
            },
            clock);
        var context = new AsyncLocalKubeconfigContext();
        var factory = new DefaultKubernetesClientFactory(
            NullLogger<DefaultKubernetesClientFactory>.Instance,
            context,
            store);

        // Register and prime the cache.
        var mintA = store.Register((byte[])MinimalKubeconfig.Clone());
        using (context.Push(mintA.Handle))
        {
            var client = factory.GetClient();
            client.Should().NotBeNull();

            // Hit again to confirm cache path is exercised.
            factory.GetClient().Should().BeSameAs(client);
        }

        // Evict mintA from the store by force-overflowing capacity.
        var mintB = store.Register((byte[])MinimalKubeconfig.Clone());
        // Now mintA is gone from the store AND from the factory cache (via HandleEvicted).

        using (context.Push(mintA.Handle))
        {
            Action act = () => factory.GetClient();
            act.Should().Throw<KubeconfigHandleNotFoundException>(
                "the per-handle cache must NOT outlive the underlying store entry");
        }

        // The OTHER handle (mintB) is still live, so its cache entry should build fine.
        using (context.Push(mintB.Handle))
        {
            factory.GetClient().Should().NotBeNull();
        }
    }

    [Fact]
    public void CacheHit_AfterTtlExpiry_IsRevalidatedAndRejected()
    {
        var clock = new ManualClock();
        var store = new InMemoryKubeconfigHandleStore(
            new AzureDiscoveryOptions
            {
                Enabled = true,
                KubeconfigHandleTtl = TimeSpan.FromMinutes(5),
                KubeconfigHandleMaxEntries = 16,
            },
            clock);
        var context = new AsyncLocalKubeconfigContext();
        var factory = new DefaultKubernetesClientFactory(
            NullLogger<DefaultKubernetesClientFactory>.Instance,
            context,
            store);

        var mint = store.Register((byte[])MinimalKubeconfig.Clone());

        using (context.Push(mint.Handle))
        {
            factory.GetClient().Should().NotBeNull();

            // Advance past TTL — store will evict on the next access. We do NOT
            // explicitly invoke the store; the factory's re-validation on cache hit
            // must catch the expiry.
            clock.Advance(TimeSpan.FromMinutes(10));

            Action act = () => factory.GetClient();
            act.Should().Throw<KubeconfigHandleNotFoundException>(
                "a cached client must die with its store entry, not survive past TTL");
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
