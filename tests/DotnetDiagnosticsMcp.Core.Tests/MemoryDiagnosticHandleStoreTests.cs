using DotnetDiagnosticsMcp.Core.Drilldown;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnosticsMcp.Core.Tests;

public class MemoryDiagnosticHandleStoreTests
{
    [Fact]
    public void Register_IssuesUniqueIdsAndStoresArtifact()
    {
        var store = new MemoryDiagnosticHandleStore();
        var first = store.Register(123, "cpu-sample", new Payload("a"), TimeSpan.FromMinutes(5));
        var second = store.Register(123, "cpu-sample", new Payload("b"), TimeSpan.FromMinutes(5));

        first.Id.Should().NotBeNullOrWhiteSpace();
        second.Id.Should().NotBe(first.Id);

        store.TryGet<Payload>(first.Id)!.Value.Should().Be("a");
        store.TryGet<Payload>(second.Id)!.Value.Should().Be("b");
    }

    [Fact]
    public void TryGet_ReturnsNullAfterTtlElapses()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new MemoryDiagnosticHandleStore(clock: clock);
        var handle = store.Register(1, "cpu-sample", new Payload("x"), TimeSpan.FromMinutes(10));

        clock.Advance(TimeSpan.FromMinutes(11));

        store.TryGet<Payload>(handle.Id).Should().BeNull("the artifact must be evicted once TTL elapses");
    }

    [Fact]
    public void InvalidateForProcess_RemovesEveryHandleForThatPid()
    {
        var store = new MemoryDiagnosticHandleStore();
        var keep = store.Register(99, "cpu-sample", new Payload("keep"), TimeSpan.FromMinutes(5));
        var drop1 = store.Register(42, "cpu-sample", new Payload("a"), TimeSpan.FromMinutes(5));
        var drop2 = store.Register(42, "gc-dump", new Payload("b"), TimeSpan.FromMinutes(5));

        store.InvalidateForProcess(42).Should().Be(2);

        store.TryGet<Payload>(keep.Id).Should().NotBeNull();
        store.TryGet<Payload>(drop1.Id).Should().BeNull();
        store.TryGet<Payload>(drop2.Id).Should().BeNull();
    }

    [Fact]
    public void Register_EvictsOldestWhenCapacityReached()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new MemoryDiagnosticHandleStore(maxEntries: 2, clock: clock);

        var first = store.Register(1, "cpu-sample", new Payload("1"), TimeSpan.FromMinutes(5));
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = store.Register(2, "cpu-sample", new Payload("2"), TimeSpan.FromMinutes(5));
        clock.Advance(TimeSpan.FromSeconds(1));
        var third = store.Register(3, "cpu-sample", new Payload("3"), TimeSpan.FromMinutes(5));

        store.TryGet<Payload>(first.Id).Should().BeNull("oldest entry must be evicted to make room");
        store.TryGet<Payload>(second.Id).Should().NotBeNull();
        store.TryGet<Payload>(third.Id).Should().NotBeNull();
    }

    private sealed record Payload(string Value);

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualClock(DateTimeOffset start) { _now = start; }
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
