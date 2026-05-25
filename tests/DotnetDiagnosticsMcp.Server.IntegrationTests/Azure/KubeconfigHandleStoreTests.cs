using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Server.Azure;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Azure;

/// <summary>
/// Unit tests for the kubeconfig handle subsystem (#234). Locks in the
/// security-critical contract: TTL eviction with zero-on-clear, multi-use within
/// TTL, and parallel TryResolve under contention.
/// </summary>
public sealed class KubeconfigHandleStoreTests
{
    private static byte[] SampleConfig(string sentinel = "kc-content") => Encoding.UTF8.GetBytes(sentinel);

    private static AzureDiscoveryOptions Opts(TimeSpan ttl) => new() { Enabled = true, KubeconfigHandleTtl = ttl };

    [Fact]
    public void Register_RoundTrips_AndIssuesUnguessableHandle()
    {
        var clock = new ControllableClock();
        var sut = new InMemoryKubeconfigHandleStore(Opts(TimeSpan.FromMinutes(10)), clock);

        var mintA = sut.Register(SampleConfig("payload-A"));
        var mintB = sut.Register(SampleConfig("payload-B"));

        mintA.Handle.Should().StartWith("kc:");
        mintA.Handle.Length.Should().BeGreaterThan(20);
        mintA.Handle.Should().NotBe(mintB.Handle);

        var resolved = sut.TryResolve(mintA.Handle);
        resolved.Should().NotBeNull();
        Encoding.UTF8.GetString(resolved!).Should().Be("payload-A");
    }

    [Fact]
    public void TryResolve_ReturnsDefensiveCopy_NotInternalBuffer()
    {
        var clock = new ControllableClock();
        var sut = new InMemoryKubeconfigHandleStore(Opts(TimeSpan.FromMinutes(10)), clock);
        var mint = sut.Register(SampleConfig("original"));

        var first = sut.TryResolve(mint.Handle)!;
        // Mutate the returned buffer — the store entry must be unaffected.
        Array.Clear(first, 0, first.Length);

        var second = sut.TryResolve(mint.Handle)!;
        Encoding.UTF8.GetString(second).Should().Be("original");
    }

    [Fact]
    public void TryResolve_AfterTtl_ReturnsNull_AndEntryIsZeroed()
    {
        var clock = new ControllableClock();
        var sut = new InMemoryKubeconfigHandleStore(Opts(TimeSpan.FromSeconds(30)), clock);

        // Use a private buffer we can inspect post-expiry to confirm zero-on-clear.
        var payload = SampleConfig("secret-bytes");
        var observable = payload; // store takes ownership; we hold the reference
        var mint = sut.Register(payload);

        sut.TryResolve(mint.Handle).Should().NotBeNull();

        // Push past expiry — the next TryResolve must evict + zero the entry.
        clock.Advance(TimeSpan.FromSeconds(31));
        sut.TryResolve(mint.Handle).Should().BeNull();

        observable.All(b => b == 0).Should().BeTrue(
            because: "expired entries MUST be Array.Clear()'d before the dictionary release");
        sut.Count.Should().Be(0);
    }

    [Fact]
    public void MultipleResolves_WithinTtl_ReturnSamePayload_NotSingleUse()
    {
        var clock = new ControllableClock();
        var sut = new InMemoryKubeconfigHandleStore(Opts(TimeSpan.FromMinutes(10)), clock);
        var mint = sut.Register(SampleConfig("multi-use"));

        for (var i = 0; i < 5; i++)
        {
            var bytes = sut.TryResolve(mint.Handle);
            bytes.Should().NotBeNull();
            Encoding.UTF8.GetString(bytes!).Should().Be("multi-use");
        }
    }

    [Fact]
    public void UnknownHandle_ReturnsNull_WithoutSideEffects()
    {
        var clock = new ControllableClock();
        var sut = new InMemoryKubeconfigHandleStore(Opts(TimeSpan.FromMinutes(10)), clock);

        sut.TryResolve("kc:doesnotexist").Should().BeNull();
        sut.TryResolve("").Should().BeNull();
        sut.TryResolve(null!).Should().BeNull();
    }

    [Fact]
    public async Task ParallelTryResolve_IsThreadSafe_AndDoesNotCorruptEntries()
    {
        var clock = new ControllableClock();
        var sut = new InMemoryKubeconfigHandleStore(Opts(TimeSpan.FromMinutes(10)), clock);
        var handles = Enumerable.Range(0, 32)
            .Select(i => sut.Register(SampleConfig("kc-" + i)).Handle)
            .ToArray();

        var observed = new ConcurrentBag<string>();
        await Task.WhenAll(Enumerable.Range(0, 512).Select(_ => Task.Run(() =>
        {
            foreach (var h in handles)
            {
                var bytes = sut.TryResolve(h);
                if (bytes is not null) observed.Add(Encoding.UTF8.GetString(bytes));
            }
        })));

        observed.Should().OnlyContain(s => s.StartsWith("kc-", StringComparison.Ordinal));
        observed.Distinct().Count().Should().Be(32);
    }

    [Fact]
    public void TtlDefaultIsTenMinutes_WhenOptionsValueIsZeroOrNegative()
    {
        var clock = new ControllableClock();
        var sutFromZero = new InMemoryKubeconfigHandleStore(Opts(TimeSpan.Zero), clock);
        var mint = sutFromZero.Register(SampleConfig());

        // 9 minutes still valid; 11 minutes expired.
        clock.Advance(TimeSpan.FromMinutes(9));
        sutFromZero.TryResolve(mint.Handle).Should().NotBeNull();
        clock.Advance(TimeSpan.FromMinutes(2));
        sutFromZero.TryResolve(mint.Handle).Should().BeNull();
    }

    private sealed class ControllableClock : TimeProvider
    {
        private DateTimeOffset _now = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
