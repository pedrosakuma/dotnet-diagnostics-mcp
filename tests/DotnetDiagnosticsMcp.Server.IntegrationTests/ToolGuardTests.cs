using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.Threads;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

/// <summary>
/// Regression for #32. Verifies that uncaught exceptions thrown by ClrMD-backed inspectors
/// are translated into a structured <see cref="DiagnosticResult{T}"/> with a stable
/// <see cref="DiagnosticError.Kind"/> instead of bubbling into the MCP SDK envelope as a
/// generic "An error occurred invoking 'X'." text content.
/// </summary>
public sealed class ToolGuardTests
{
    [Fact]
    public async Task InspectLiveHeap_PtraceFailure_TranslatesToPermissionDenied()
    {
        var inspector = new ThrowingDumpInspector(
            new ClrDiagnosticsException("Could not PTRACE_ATTACH to any thread of the process 1234."));
        var handles = new MemoryDiagnosticHandleStore();

        var result = await DiagnosticTools.InspectLiveHeap(
            inspector, handles, processId: 1234, cancellationToken: default);

        result.IsError.Should().BeTrue();
        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("PermissionDenied");
        result.Error.Message.Should().Contain("PTRACE_ATTACH");
        result.Summary.Should().Contain("pid 1234");
        result.Summary.Should().Contain("ptrace");
    }

    [Fact]
    public async Task InspectLiveHeap_ServerNotAvailable_TranslatesToEndpointUnavailable()
    {
        var inspector = new ThrowingDumpInspector(new ServerNotAvailableException("socket gone"));
        var handles = new MemoryDiagnosticHandleStore();

        var result = await DiagnosticTools.InspectLiveHeap(
            inspector, handles, processId: 999, cancellationToken: default);

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be("EndpointUnavailable");
        result.Error.Message.Should().Be("socket gone");
    }

    [Fact]
    public async Task CollectThreadSnapshot_PtraceFailure_TranslatesToPermissionDenied()
    {
        var inspector = new ThrowingThreadSnapshotInspector(
            new ClrDiagnosticsException("Could not PTRACE_ATTACH to any thread of the process 42."));
        var handles = new MemoryDiagnosticHandleStore();

        var result = await DiagnosticTools.CollectThreadSnapshot(
            inspector, handles, processId: 42, cancellationToken: default);

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be("PermissionDenied");
        result.Error.Message.Should().Contain("PTRACE_ATTACH");
    }

    private sealed class ThrowingDumpInspector : IDumpInspector
    {
        private readonly Exception _ex;
        public ThrowingDumpInspector(Exception ex) { _ex = ex; }

        public Task<HeapSnapshotArtifact> InspectAsync(string dumpFilePath, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default)
            => throw _ex;

        public Task<HeapSnapshotArtifact> InspectLiveAsync(int processId, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default)
            => throw _ex;
    }

    private sealed class ThrowingThreadSnapshotInspector : IThreadSnapshotInspector
    {
        private readonly Exception _ex;
        public ThrowingThreadSnapshotInspector(Exception ex) { _ex = ex; }

        public Task<ThreadSnapshotArtifact> InspectLiveAsync(int processId, ThreadSnapshotOptions? options = null, CancellationToken cancellationToken = default)
            => throw _ex;

        public Task<ThreadSnapshotArtifact> InspectDumpAsync(string dumpFilePath, ThreadSnapshotOptions? options = null, CancellationToken cancellationToken = default)
            => throw _ex;
    }
}
