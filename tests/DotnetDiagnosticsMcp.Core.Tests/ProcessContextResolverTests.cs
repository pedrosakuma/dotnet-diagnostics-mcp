using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

/// <summary>
/// Covers the bootstrap-implícito resolver (issue #42): auto-resolution semantics, error
/// surface, and the 60s per-pid capability digest cache.
/// </summary>
public sealed class ProcessContextResolverTests
{
    private static readonly DiagnosticCapabilities DefaultCaps = new(
        ProcessId: 0,
        Runtime: RuntimeFlavor.CoreClr,
        RuntimeVersion: "10.0.0",
        CanReadEventCounters: true,
        CanSampleCpu: true,
        CanCollectGcDump: true,
        CanCollectExceptions: true,
        CanCollectHttpActivity: true,
        CanCollectCustomEventSource: true,
        CanCollectProcessDump: true,
        Notes: "");

    [Fact]
    public async Task ResolveAsync_NoProcesses_ReturnsNoDotnetProcessFound()
    {
        var discovery = new StubDiscovery();
        var detector = new StubDetector(_ => DefaultCaps);
        var resolver = new ProcessContextResolver(discovery, detector, new FakeTimeProvider(), TimeSpan.FromSeconds(60));

        var result = await resolver.ResolveAsync(requestedProcessId: null, default);

        result.Context.Should().BeNull();
        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("NoDotnetProcessFound");
        result.Candidates.Should().BeNull();
        detector.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_SingleProcess_AutoResolvesAndMarksFlag()
    {
        var discovery = new StubDiscovery(new DotnetProcess(1234, "/myapp", "linux", "x64", "10.0.0", "myapp"));
        var detector = new StubDetector(_ => DefaultCaps);
        var resolver = new ProcessContextResolver(discovery, detector, new FakeTimeProvider(), TimeSpan.FromSeconds(60));

        var result = await resolver.ResolveAsync(requestedProcessId: null, default);

        result.Error.Should().BeNull();
        result.Context.Should().NotBeNull();
        result.Context!.ProcessId.Should().Be(1234);
        result.Context.AutoResolved.Should().BeTrue();
        result.Context.Runtime.Should().Be(RuntimeFlavor.CoreClr);
        result.Context.CanSampleCpu.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveAsync_MultipleProcesses_ReturnsAmbiguousWithCandidates()
    {
        var discovery = new StubDiscovery(
            new DotnetProcess(1, "/a", "linux", "x64", "10.0.0", "a"),
            new DotnetProcess(2, "/b", "linux", "x64", "10.0.0", "b"));
        var detector = new StubDetector(_ => DefaultCaps);
        var resolver = new ProcessContextResolver(discovery, detector, new FakeTimeProvider(), TimeSpan.FromSeconds(60));

        var result = await resolver.ResolveAsync(requestedProcessId: null, default);

        result.Context.Should().BeNull();
        result.Error!.Kind.Should().Be("AmbiguousDotnetProcess");
        result.Candidates.Should().HaveCount(2);
        detector.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_ExplicitPid_DoesNotMarkAutoResolved()
    {
        var discovery = new StubDiscovery(new DotnetProcess(1, "/a", "linux", "x64", "10.0.0", "a"));
        var detector = new StubDetector(_ => DefaultCaps);
        var resolver = new ProcessContextResolver(discovery, detector, new FakeTimeProvider(), TimeSpan.FromSeconds(60));

        var result = await resolver.ResolveAsync(requestedProcessId: 1, default);

        result.Context!.AutoResolved.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveAsync_DetectorThrows_SurfacesEndpointUnavailable()
    {
        var discovery = new StubDiscovery(new DotnetProcess(7, "/x", "linux", "x64", "10.0.0", "x"));
        var detector = new StubDetector(_ => throw new InvalidOperationException("socket gone"));
        var resolver = new ProcessContextResolver(discovery, detector, new FakeTimeProvider(), TimeSpan.FromSeconds(60));

        var result = await resolver.ResolveAsync(requestedProcessId: 7, default);

        result.Context.Should().BeNull();
        result.Error!.Kind.Should().Be("EndpointUnavailable");
    }

    [Fact]
    public async Task ResolveAsync_RepeatedCallsWithinTtl_HitCache()
    {
        var discovery = new StubDiscovery(new DotnetProcess(42, "/x", "linux", "x64", "10.0.0", "x"));
        var detector = new StubDetector(_ => DefaultCaps);
        var clock = new FakeTimeProvider();
        var resolver = new ProcessContextResolver(discovery, detector, clock, TimeSpan.FromSeconds(60));

        await resolver.ResolveAsync(42, default);
        await resolver.ResolveAsync(42, default);
        clock.Advance(TimeSpan.FromSeconds(30));
        await resolver.ResolveAsync(42, default);

        detector.CallCount.Should().Be(1, "second + third calls fall within the 60s TTL");
    }

    [Fact]
    public async Task ResolveAsync_AfterTtlExpiry_ReprobesDetector()
    {
        var discovery = new StubDiscovery(new DotnetProcess(42, "/x", "linux", "x64", "10.0.0", "x"));
        var detector = new StubDetector(_ => DefaultCaps);
        var clock = new FakeTimeProvider();
        var resolver = new ProcessContextResolver(discovery, detector, clock, TimeSpan.FromSeconds(60));

        await resolver.ResolveAsync(42, default);
        clock.Advance(TimeSpan.FromSeconds(61));
        await resolver.ResolveAsync(42, default);

        detector.CallCount.Should().Be(2);
    }

    private sealed class StubDiscovery : IProcessDiscovery
    {
        private readonly IReadOnlyList<DotnetProcess> _processes;
        public StubDiscovery(params DotnetProcess[] processes) => _processes = processes;
        public IReadOnlyList<DotnetProcess> ListProcesses() => _processes;
        public DotnetProcess? TryGetProcess(int processId) => _processes.FirstOrDefault(p => p.ProcessId == processId);
    }

    private sealed class StubDetector : ICapabilityDetector
    {
        private readonly Func<int, DiagnosticCapabilities> _factory;
        public int CallCount { get; private set; }
        public StubDetector(Func<int, DiagnosticCapabilities> factory) => _factory = factory;
        public Task<DiagnosticCapabilities> DetectAsync(int processId, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_factory(processId));
        }
    }

    /// <summary>
    /// Minimal manually-advanced clock. Avoids pulling in Microsoft.Extensions.TimeProvider.Testing
    /// just for two TTL assertions.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
