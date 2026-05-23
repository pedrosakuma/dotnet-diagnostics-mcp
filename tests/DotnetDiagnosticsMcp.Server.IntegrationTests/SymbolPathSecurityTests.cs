using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.Memory;
using DotnetDiagnosticsMcp.Core.OffCpu;
using DotnetDiagnosticsMcp.Core.Security;
using DotnetDiagnosticsMcp.Core.Threads;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

/// <summary>
/// B4 / issue #165 / M3 — assert the SSRF guard on every tool that forwards
/// caller-supplied <c>symbolPath</c> into a symbol resolver: <c>collect_off_cpu_sample</c>,
/// <c>collect_thread_snapshot</c>, <c>inspect_dump</c>, <c>inspect_live_heap</c>.
/// (<c>collect_cpu_sample</c> is covered separately in <see cref="CollectCpuSampleSecurityTests"/>.)
/// </summary>
public sealed class SymbolPathSecurityTests
{
    private const string RemotePath = @"srv*c:\sym*https://msdl.microsoft.com/download/symbols";
    private const string LocalPath = "/srv/symbols";

    [Fact]
    public async Task CollectOffCpuSample_RemoteHost_NotAllowlisted_IsRejected()
    {
        var sampler = new ThrowingOffCpuSampler();
        var store = new MemoryDiagnosticHandleStore();

        var result = await DiagnosticTools.CollectOffCpuSample(
            sampler, store, ToolGuardTests.EchoResolver(),
            new SymbolServerAllowlist(null),
            processId: 42,
            durationSeconds: 1,
            symbolPath: RemotePath);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("SymbolServerNotAllowed");
        sampler.Invocations.Should().Be(0);
    }

    [Fact]
    public async Task CollectOffCpuSample_LocalPath_PassesThrough()
    {
        var sampler = new StubOffCpuSampler();
        var store = new MemoryDiagnosticHandleStore();

        var result = await DiagnosticTools.CollectOffCpuSample(
            sampler, store, ToolGuardTests.EchoResolver(),
            new SymbolServerAllowlist(null),
            processId: 42,
            durationSeconds: 1,
            symbolPath: LocalPath);

        result.Error.Should().BeNull();
        sampler.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task CollectThreadSnapshot_RemoteHost_NotAllowlisted_IsRejected()
    {
        var inspector = new ThrowingThreadSnapshotInspector();
        var store = new MemoryDiagnosticHandleStore();

        var result = await DiagnosticTools.CollectThreadSnapshot(
            inspector, store, ToolGuardTests.EchoResolver(),
            new SymbolServerAllowlist(null),
            processId: 42,
            symbolPath: RemotePath);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("SymbolServerNotAllowed");
        inspector.Invocations.Should().Be(0);
    }

    [Fact]
    public async Task CollectThreadSnapshot_RemoteHost_OnAllowlist_PassesThrough()
    {
        var inspector = new StubThreadSnapshotInspector();
        var store = new MemoryDiagnosticHandleStore();
        var options = new SecurityOptions { SymbolServerAllowlist = { "msdl.microsoft.com" } };

        var result = await DiagnosticTools.CollectThreadSnapshot(
            inspector, store, ToolGuardTests.EchoResolver(),
            new SymbolServerAllowlist(options),
            processId: 42,
            symbolPath: RemotePath);

        result.Error.Should().BeNull();
        inspector.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task InspectDump_RemoteHost_NotAllowlisted_IsRejected()
    {
        var inspector = new ThrowingDumpInspector();
        var store = new MemoryDiagnosticHandleStore();

        var result = await DiagnosticTools.InspectDump(
            inspector, store,
            new SymbolServerAllowlist(null),
            dumpFilePath: "/tmp/x.dmp",
            symbolPath: RemotePath);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("SymbolServerNotAllowed");
        inspector.Invocations.Should().Be(0);
    }

    [Fact]
    public async Task InspectLiveHeap_RemoteHost_NotAllowlisted_IsRejected()
    {
        var inspector = new ThrowingDumpInspector();
        var store = new MemoryDiagnosticHandleStore();

        var result = await DiagnosticTools.InspectLiveHeap(
            inspector, store, ToolGuardTests.EchoResolver(),
            new SymbolServerAllowlist(null),
            processId: 42,
            symbolPath: RemotePath);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("SymbolServerNotAllowed");
        inspector.Invocations.Should().Be(0);
    }

    // ---- test doubles ----

    private sealed class ThrowingOffCpuSampler : IOffCpuSampler
    {
        public int Invocations { get; private set; }
        public bool IsAvailable() => true;
        public Task<OffCpuSampleResult> SampleAsync(int processId, TimeSpan duration, int topN = 25, string? symbolPath = null, CancellationToken cancellationToken = default)
        {
            Invocations++;
            throw new InvalidOperationException("sampler must not be reached when symbol path is rejected");
        }
    }

    private sealed class StubOffCpuSampler : IOffCpuSampler
    {
        public int Invocations { get; private set; }
        public bool IsAvailable() => true;
        public Task<OffCpuSampleResult> SampleAsync(int processId, TimeSpan duration, int topN = 25, string? symbolPath = null, CancellationToken cancellationToken = default)
        {
            Invocations++;
            var snap = new OffCpuSnapshot(processId, DateTimeOffset.UtcNow, duration, 0, 0, Array.Empty<OffCpuStackHotspot>(), 0, "stub");
            var artifact = new OffCpuSnapshotArtifact(processId, DateTimeOffset.UtcNow, duration, 0, 0, Array.Empty<OffCpuStackHotspot>(), Array.Empty<OffCpuThreadView>(), "stub");
            return Task.FromResult(new OffCpuSampleResult(snap, artifact));
        }
    }

    private sealed class ThrowingThreadSnapshotInspector : IThreadSnapshotInspector
    {
        public int Invocations { get; private set; }
        public Task<ThreadSnapshotArtifact> InspectLiveAsync(int processId, ThreadSnapshotOptions? options = null, CancellationToken cancellationToken = default)
        {
            Invocations++;
            throw new InvalidOperationException("inspector must not be reached when symbol path is rejected");
        }
        public Task<ThreadSnapshotArtifact> InspectDumpAsync(string dumpFilePath, ThreadSnapshotOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("not used");
    }

    private sealed class StubThreadSnapshotInspector : IThreadSnapshotInspector
    {
        public int Invocations { get; private set; }
        public Task<ThreadSnapshotArtifact> InspectLiveAsync(int processId, ThreadSnapshotOptions? options = null, CancellationToken cancellationToken = default)
        {
            Invocations++;
            return Task.FromResult(new ThreadSnapshotArtifact(
                ThreadSnapshotOrigin.Live, processId, DateTimeOffset.UtcNow, TimeSpan.Zero,
                "stub", "0", Array.Empty<ManagedThread>(), Array.Empty<MonitorLockState>()));
        }
        public Task<ThreadSnapshotArtifact> InspectDumpAsync(string dumpFilePath, ThreadSnapshotOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("not used");
    }

    private sealed class ThrowingDumpInspector : IDumpInspector
    {
        public int Invocations { get; private set; }
        public Task<HeapSnapshotArtifact> InspectAsync(string dumpFilePath, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default)
        {
            Invocations++;
            throw new InvalidOperationException("inspector must not be reached when symbol path is rejected");
        }
        public Task<HeapSnapshotArtifact> InspectLiveAsync(int processId, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default)
        {
            Invocations++;
            throw new InvalidOperationException("inspector must not be reached when symbol path is rejected");
        }
        public Task<HeapObjectInspection> InspectObjectAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<HeapGcRootInspection> InspectGcRootAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<HeapObjectSizeInspection> InspectObjectSizeAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
