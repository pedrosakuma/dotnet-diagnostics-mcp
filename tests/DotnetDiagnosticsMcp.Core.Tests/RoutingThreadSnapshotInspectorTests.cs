using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.Threads;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class RoutingThreadSnapshotInspectorTests
{
    [Fact]
    public async Task InspectLiveAsync_WhenFirstBackendPermissionDenied_TriesNextBackend()
    {
        var caps = new DiagnosticCapabilities(
            ProcessId: 321,
            Runtime: RuntimeFlavor.NativeAot,
            RuntimeVersion: "10.0.0",
            CanReadEventCounters: true,
            CanSampleCpu: true,
            CanCollectGcDump: false,
            CanCollectExceptions: true,
            CanCollectHttpActivity: true,
            CanCollectCustomEventSource: true,
            CanCollectProcessDump: true,
            Notes: string.Empty);
        var detector = new StubCapabilityDetector(caps);

        var first = new StubBackend(
            backendId: "first-native",
            order: 10,
            canHandle: runtime => runtime == RuntimeFlavor.NativeAot,
            inspectLive: _ => throw new UnauthorizedAccessException("ptrace denied"));
        var expected = new ThreadSnapshotArtifact(
            Origin: ThreadSnapshotOrigin.Live,
            ProcessId: 321,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(10),
            RuntimeName: "NativeAot",
            RuntimeVersion: "10.0.0",
            Threads: Array.Empty<ManagedThread>(),
            Locks: Array.Empty<MonitorLockState>())
        {
            Source = "linux-native-stack",
        };
        var second = new StubBackend(
            backendId: "second-native",
            order: 20,
            canHandle: runtime => runtime == RuntimeFlavor.NativeAot,
            inspectLive: _ => expected);

        var router = new RoutingThreadSnapshotInspector(detector, new[] { first, second });

        var snapshot = await router.InspectLiveAsync(321, cancellationToken: CancellationToken.None);

        first.LiveCallCount.Should().Be(1);
        second.LiveCallCount.Should().Be(1);
        snapshot.Source.Should().Be("linux-native-stack");
        snapshot.ProcessId.Should().Be(321);
    }

    private sealed class StubCapabilityDetector : ICapabilityDetector
    {
        private readonly DiagnosticCapabilities _caps;

        public StubCapabilityDetector(DiagnosticCapabilities caps) => _caps = caps;

        public Task<DiagnosticCapabilities> DetectAsync(int processId, CancellationToken cancellationToken = default)
            => Task.FromResult(_caps with { ProcessId = processId });
    }

    private sealed class StubBackend : IThreadSnapshotBackend
    {
        private readonly Func<RuntimeFlavor, bool> _canHandle;
        private readonly Func<int, ThreadSnapshotArtifact> _inspectLive;

        public StubBackend(
            string backendId,
            int order,
            Func<RuntimeFlavor, bool> canHandle,
            Func<int, ThreadSnapshotArtifact> inspectLive)
        {
            BackendId = backendId;
            Order = order;
            _canHandle = canHandle;
            _inspectLive = inspectLive;
        }

        public int LiveCallCount { get; private set; }

        public string BackendId { get; }

        public int Order { get; }

        public string? Preconditions => null;

        public bool CanHandleLive(RuntimeFlavor runtime) => _canHandle(runtime);

        public bool CanHandleDump => false;

        public Task<ThreadSnapshotArtifact> InspectLiveAsync(
            int processId,
            ThreadSnapshotOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LiveCallCount++;
            return Task.FromResult(_inspectLive(processId));
        }

        public Task<ThreadSnapshotArtifact> InspectDumpAsync(
            string dumpFilePath,
            ThreadSnapshotOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
