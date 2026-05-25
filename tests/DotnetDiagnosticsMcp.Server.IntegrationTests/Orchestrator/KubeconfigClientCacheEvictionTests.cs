using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>
    /// FIX 4 (#234 review, gpt-5.5 2nd pass): deterministic post-TryAdd eviction race.
    /// Wraps the real store with a double that pauses inside the FIRST
    /// <see cref="IKubeconfigHandleStore.TryPeekExpiry"/> call — the one the factory
    /// uses to capture the expiry it will install into its cache. While paused, the
    /// underlying store evicts the entry (HandleEvicted fires; the factory's
    /// subscriber finds nothing to remove because TryAdd hasn't run yet). After
    /// resume the factory installs a now-stale cache entry, and the new post-TryAdd
    /// re-check must catch the drift and surface KubeconfigHandleNotFound.
    /// </summary>
    [Fact]
    public async Task PostTryAddEviction_RollsBackCacheEntry_AndThrowsHandleNotFound()
    {
        var clock = new ManualClock();
        var realStore = new InMemoryKubeconfigHandleStore(
            new AzureDiscoveryOptions
            {
                Enabled = true,
                KubeconfigHandleTtl = TimeSpan.FromMinutes(10),
                KubeconfigHandleMaxEntries = 1, // second Register forces capacity eviction of the first
            },
            clock);

        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var raceStore = new RaceStoreDouble(realStore, paused, resume);

        var context = new AsyncLocalKubeconfigContext();
        var factory = new DefaultKubernetesClientFactory(
            NullLogger<DefaultKubernetesClientFactory>.Instance,
            context,
            raceStore);

        var mintA = realStore.Register((byte[])MinimalKubeconfig.Clone());
        raceStore.PauseForHandle = mintA.Handle;

        // Start the racy GetClient on a background task; it will resolve bytes, then
        // pause inside TryPeekExpiry before the cache TryAdd.
        var getClientTask = Task.Run(() =>
        {
            using (context.Push(mintA.Handle))
            {
                return factory.GetClient();
            }
        });

        // Wait for the factory to enter the paused window — TryPeekExpiry has already
        // returned the live (non-null) expiry to the factory but the factory hasn't
        // yet built the client or run TryAdd.
        (await Task.WhenAny(paused.Task, Task.Delay(TimeSpan.FromSeconds(5))))
            .Should().BeSameAs(paused.Task, "factory must reach the post-peek pause window");

        // Force eviction of mintA: registering a second handle with MaxEntries=1
        // capacity overflows the oldest entry and fires HandleEvicted synchronously.
        var mintB = realStore.Register((byte[])MinimalKubeconfig.Clone());
        mintB.Handle.Should().NotBe(mintA.Handle);
        realStore.TryPeekExpiry(mintA.Handle).Should().BeNull("real store has dropped mintA");

        // Release the factory; it will build the client, TryAdd (succeeds — nothing
        // was there because HandleEvicted ran before TryAdd), then re-peek and find
        // the entry gone.
        resume.SetResult();

        Func<Task> act = async () => await getClientTask;
        await act.Should().ThrowAsync<KubeconfigHandleNotFoundException>(
            "post-TryAdd re-check must detect the eviction-before-add race");

        // Cache entry was rolled back — a fresh attempt under the still-evicted
        // handle hits the cold-miss path and produces the same envelope.
        raceStore.PauseForHandle = null; // never pause again
        using (context.Push(mintA.Handle))
        {
            Action coldMiss = () => factory.GetClient();
            coldMiss.Should().Throw<KubeconfigHandleNotFoundException>();
        }
    }

    /// <summary>
    /// Wraps a real <see cref="IKubeconfigHandleStore"/> and pauses inside the FIRST
    /// <see cref="TryPeekExpiry"/> call for <see cref="PauseForHandle"/> so the test
    /// can deterministically inject an eviction between the factory's expiry capture
    /// and its cache TryAdd. All other surface members are pass-throughs; the
    /// HandleEvicted event is forwarded so the factory's subscriber still sees real
    /// evictions.
    /// </summary>
    private sealed class RaceStoreDouble : IKubeconfigHandleStore
    {
        private readonly IKubeconfigHandleStore _inner;
        private readonly TaskCompletionSource _paused;
        private readonly TaskCompletionSource _resume;
        private int _peekCount;

        public RaceStoreDouble(IKubeconfigHandleStore inner, TaskCompletionSource paused, TaskCompletionSource resume)
        {
            _inner = inner;
            _paused = paused;
            _resume = resume;
            _inner.HandleEvicted += (s, e) => HandleEvicted?.Invoke(this, e);
        }

        public string? PauseForHandle { get; set; }

        public int Count => _inner.Count;

        public event EventHandler<KubeconfigHandleEvictedEventArgs>? HandleEvicted;

        public KubeconfigHandleMint Register(byte[] kubeconfig) => _inner.Register(kubeconfig);

        public byte[]? TryResolve(string handle) => _inner.TryResolve(handle);

        public DateTimeOffset? TryPeekExpiry(string handle)
        {
            // Capture the live expiry FIRST so the value handed back is what the
            // factory would have observed in the absence of the race.
            var live = _inner.TryPeekExpiry(handle);
            if (handle == PauseForHandle && Interlocked.Increment(ref _peekCount) == 1)
            {
                _paused.TrySetResult();
                _resume.Task.GetAwaiter().GetResult();
                // Intentionally return the pre-eviction value so the factory builds
                // a client with the stale expiry and then trips the post-TryAdd
                // re-check (which gets the fresh, now-null reading).
                return live;
            }
            return live;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
