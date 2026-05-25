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

    /// <summary>
    /// FIX 5 (#234 review, gpt-5.5 3rd pass): the TryAdd-LOSER sibling race.
    ///
    /// Approach chosen: two concurrent <c>factory.GetClient()</c> calls coordinated
    /// through a store double that gates the factory's <c>TryPeekExpiry</c> calls
    /// per-count. We deliberately did NOT use a test-only seam to pre-populate the
    /// private <c>_handleClients</c> cache because the loser branch is only
    /// reachable when <c>TryGetValue</c> misses initially AND <c>TryAdd</c>
    /// subsequently fails — a state pre-seeding cannot produce (pre-seeding drives
    /// the cache-hit revalidation branch, which FIX 3 already covers). Driving
    /// both threads past TryAdd with deterministic ordering is the only way to
    /// exercise the loser branch.
    ///
    /// Orchestration:
    /// <list type="number">
    ///   <item>Both threads pause inside their FIRST TryPeekExpiry (capture-of-expiresAt).</item>
    ///   <item>Test evicts the handle from the store (HandleEvicted fires; factory
    ///     cache is still empty so the subscriber has nothing to remove).</item>
    ///   <item>Release thread #1 → it builds, TryAdd succeeds (WINNER), then pauses
    ///     inside its post-add TryPeekExpiry so the loser can run with the stale
    ///     entry still visible in the cache.</item>
    ///   <item>Release thread #2 → it builds, TryAdd FAILS (LOSER branch), the
    ///     fix's re-peek returns null (store evicted), throws.</item>
    ///   <item>Release winner's post-peek → it returns null too and throws.</item>
    /// </list>
    ///
    /// Assertions: the loser task (= first task to complete) throws
    /// KubeconfigHandleNotFoundException, and <c>_handleClients</c> contains no
    /// entry for the evicted handle after both tasks return.
    /// </summary>
    [Fact]
    public async Task TryAddLoserPath_StaleEntry_ThrowsHandleNotFound()
    {
        var clock = new ManualClock();
        var realStore = new InMemoryKubeconfigHandleStore(
            new AzureDiscoveryOptions
            {
                Enabled = true,
                KubeconfigHandleTtl = TimeSpan.FromMinutes(10),
                KubeconfigHandleMaxEntries = 1, // second Register forces capacity eviction
            },
            clock);

        var firstPeekReached1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPeekReached2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeFirstPeek1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeFirstPeek2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var winnerPostPeekReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeWinnerPostPeek = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var loserRaceStore = new LoserRaceStoreDouble(
            realStore,
            firstPeekReached1, firstPeekReached2,
            resumeFirstPeek1, resumeFirstPeek2,
            winnerPostPeekReached, resumeWinnerPostPeek);

        var context = new AsyncLocalKubeconfigContext();
        var factory = new DefaultKubernetesClientFactory(
            NullLogger<DefaultKubernetesClientFactory>.Instance,
            context,
            loserRaceStore);

        var mintA = realStore.Register((byte[])MinimalKubeconfig.Clone());
        loserRaceStore.PauseForHandle = mintA.Handle;

        // Two concurrent GetClient calls — both race into TryPeekExpiry, both pause.
        var taskA = Task.Run(() =>
        {
            using (context.Push(mintA.Handle))
            {
                return factory.GetClient();
            }
        });
        var taskB = Task.Run(() =>
        {
            using (context.Push(mintA.Handle))
            {
                return factory.GetClient();
            }
        });

        // Both threads parked at the capture-of-expiresAt peek.
        var bothPaused = Task.WhenAll(firstPeekReached1.Task, firstPeekReached2.Task);
        (await Task.WhenAny(bothPaused, Task.Delay(TimeSpan.FromSeconds(5))))
            .Should().BeSameAs(bothPaused, "both racing threads must reach the first-peek pause window");

        // Evict mintA from the underlying store via capacity overflow. HandleEvicted
        // fires through the double; factory's subscriber finds the cache empty
        // (TryAdd hasn't run yet on either thread) and the no-op completes.
        var mintB = realStore.Register((byte[])MinimalKubeconfig.Clone());
        mintB.Handle.Should().NotBe(mintA.Handle);
        realStore.TryPeekExpiry(mintA.Handle).Should().BeNull("real store has dropped mintA");

        // Release the winner first. It returns from TryPeekExpiry with the captured
        // (pre-eviction) expiry, builds, TryAdd succeeds (cache empty), then enters
        // its post-add re-peek (count 3) — paused so the loser can observe the
        // stale entry.
        resumeFirstPeek1.SetResult();
        (await Task.WhenAny(winnerPostPeekReached.Task, Task.Delay(TimeSpan.FromSeconds(5))))
            .Should().BeSameAs(winnerPostPeekReached.Task, "winner must reach its post-peek pause window");

        // Now release the loser. It builds, TryAdd fails (winner's stale entry is
        // present), enters the loser branch. With the FIX (post-TryAdd re-peek on
        // the loser branch) the fresh peek (count 4) returns null and the loser
        // throws KubeconfigHandleNotFoundException. WITHOUT the fix it would
        // observe existing.ExpiresAt == capturedExpiresAt and return the stale
        // client.
        resumeFirstPeek2.SetResult();

        // The loser completes FIRST because the winner is still parked at its
        // post-peek. Whichever of (taskA, taskB) finishes first is the loser.
        var finishedFirst = await Task.WhenAny(taskA, taskB).WaitAsync(TimeSpan.FromSeconds(5));
        var loserTask = finishedFirst;
        var winnerTask = ReferenceEquals(loserTask, taskA) ? taskB : taskA;

        Func<Task> loserAct = async () => await loserTask;
        await loserAct.Should().ThrowAsync<KubeconfigHandleNotFoundException>(
            "the TryAdd-loser branch must re-validate against the store and refuse a stale winner entry");

        // Release the winner so it unwinds cleanly (its post-peek now returns null
        // too — the FIX-4 winner-side rollback kicks in and the winner also throws).
        resumeWinnerPostPeek.SetResult();
        Func<Task> winnerAct = async () => await winnerTask;
        await winnerAct.Should().ThrowAsync<KubeconfigHandleNotFoundException>(
            "the winner's FIX-4 rollback must also surface KubeconfigHandleNotFound");

        // Cache cleanup: neither the loser's rollback nor the winner's rollback
        // may leave a dangling entry behind. The cold-miss path under the still-
        // evicted handle must produce the same envelope.
        loserRaceStore.PauseForHandle = null;
        using (context.Push(mintA.Handle))
        {
            Action coldMiss = () => factory.GetClient();
            coldMiss.Should().Throw<KubeconfigHandleNotFoundException>(
                "neither rollback may leave a stale entry behind for the evicted handle");
        }
    }

    /// <summary>
    /// Store double that pauses the factory's TryPeekExpiry calls in a deterministic
    /// per-count order:
    /// <list type="bullet">
    ///   <item>count 1 — first thread's capture-of-expiresAt; pause on resumeFirstPeek1.</item>
    ///   <item>count 2 — second thread's capture-of-expiresAt; pause on resumeFirstPeek2.</item>
    ///   <item>count 3 — first post-peek (the TryAdd winner); pause on resumeWinnerPostPeek; returns a FRESH read so the winner sees the now-null expiry.</item>
    ///   <item>count 4+ — fresh read pass-through (used by the loser's post-peek check from FIX 5).</item>
    /// </list>
    /// </summary>
    private sealed class LoserRaceStoreDouble : IKubeconfigHandleStore
    {
        private readonly IKubeconfigHandleStore _inner;
        private readonly TaskCompletionSource _firstPeekReached1;
        private readonly TaskCompletionSource _firstPeekReached2;
        private readonly TaskCompletionSource _resumeFirstPeek1;
        private readonly TaskCompletionSource _resumeFirstPeek2;
        private readonly TaskCompletionSource _winnerPostPeekReached;
        private readonly TaskCompletionSource _resumeWinnerPostPeek;
        private int _peekCount;

        public LoserRaceStoreDouble(
            IKubeconfigHandleStore inner,
            TaskCompletionSource firstPeekReached1,
            TaskCompletionSource firstPeekReached2,
            TaskCompletionSource resumeFirstPeek1,
            TaskCompletionSource resumeFirstPeek2,
            TaskCompletionSource winnerPostPeekReached,
            TaskCompletionSource resumeWinnerPostPeek)
        {
            _inner = inner;
            _firstPeekReached1 = firstPeekReached1;
            _firstPeekReached2 = firstPeekReached2;
            _resumeFirstPeek1 = resumeFirstPeek1;
            _resumeFirstPeek2 = resumeFirstPeek2;
            _winnerPostPeekReached = winnerPostPeekReached;
            _resumeWinnerPostPeek = resumeWinnerPostPeek;
            _inner.HandleEvicted += (s, e) => HandleEvicted?.Invoke(this, e);
        }

        public string? PauseForHandle { get; set; }

        public int Count => _inner.Count;

        public event EventHandler<KubeconfigHandleEvictedEventArgs>? HandleEvicted;

        public KubeconfigHandleMint Register(byte[] kubeconfig) => _inner.Register(kubeconfig);

        public byte[]? TryResolve(string handle) => _inner.TryResolve(handle);

        public DateTimeOffset? TryPeekExpiry(string handle)
        {
            // Capture the live expiry up front so that count-1 / count-2 return
            // the pre-eviction value the factory would have observed without the
            // pause — the whole point of the test is for both threads to walk
            // into TryAdd carrying the same (now-stale) expiresAt.
            var live = _inner.TryPeekExpiry(handle);
            if (handle != PauseForHandle)
            {
                return live;
            }

            var count = Interlocked.Increment(ref _peekCount);
            switch (count)
            {
                case 1:
                    _firstPeekReached1.TrySetResult();
                    _resumeFirstPeek1.Task.GetAwaiter().GetResult();
                    return live;
                case 2:
                    _firstPeekReached2.TrySetResult();
                    _resumeFirstPeek2.Task.GetAwaiter().GetResult();
                    return live;
                case 3:
                    // The winner's post-add re-peek. Pause so the loser can race
                    // through its TryAdd-fails branch with the stale entry still
                    // present. Return a FRESH read after resume — the underlying
                    // store has evicted the handle by now, so this returns null
                    // and the winner's FIX-4 rollback triggers.
                    _winnerPostPeekReached.TrySetResult();
                    _resumeWinnerPostPeek.Task.GetAwaiter().GetResult();
                    return _inner.TryPeekExpiry(handle);
                default:
                    // Loser's post-peek (FIX-5 re-check) and any subsequent reads.
                    // Always fresh — the underlying store no longer has the handle.
                    return _inner.TryPeekExpiry(handle);
            }
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
