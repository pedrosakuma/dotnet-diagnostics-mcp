using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
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
            inspector, handles, EchoResolver(), processId: 1234, cancellationToken: default);

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
            inspector, handles, EchoResolver(), processId: 999, cancellationToken: default);

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
            inspector, handles, EchoResolver(), processId: 42, cancellationToken: default);

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be("PermissionDenied");
        result.Error.Message.Should().Contain("PTRACE_ATTACH");
    }

    [Fact]
    public async Task InspectLiveHeap_OperationNotPermitted_TranslatesToPermissionDenied()
    {
        // Canonical EPERM message that ClrMD can produce when the kernel rejects ptrace
        // before the runtime reaches the explicit "PTRACE" / "permission" wording. Without
        // the EPERM-aware classifier, this falls through to Internal and the LLM loses
        // the cue to suggest CAP_SYS_PTRACE / a dump fallback.
        var inspector = new ThrowingDumpInspector(
            new ClrDiagnosticsException("Operation not permitted"));
        var handles = new MemoryDiagnosticHandleStore();

        var result = await DiagnosticTools.InspectLiveHeap(
            inspector, handles, EchoResolver(), processId: 7, cancellationToken: default);

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be("PermissionDenied");
    }

    [Fact]
    public async Task InspectLiveHeap_NestedWin32EPERM_TranslatesToPermissionDenied()
    {
        // ClrMD often nests a Win32Exception (NativeErrorCode=1) inside its
        // ClrDiagnosticsException. The classifier must walk InnerException, not just
        // pattern-match the outer Message.
        var inner = new System.ComponentModel.Win32Exception(1);
        var outer = new ClrDiagnosticsException("Attach failed", inner);
        var inspector = new ThrowingDumpInspector(outer);
        var handles = new MemoryDiagnosticHandleStore();

        var result = await DiagnosticTools.InspectLiveHeap(
            inspector, handles, EchoResolver(), processId: 9, cancellationToken: default);

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be("PermissionDenied");
    }

    [Fact]
    public void ToolErrorSurfaceFilter_BuildErrorText_IncludesTopExceptionTypeMessageAndChain()
    {
        var inner = new System.ComponentModel.Win32Exception(1, "Operation not permitted");
        var outer = new InvalidOperationException("PTRACE_ATTACH failed", inner);

        var text = ToolErrorSurfaceFilter.BuildErrorText("inspect_live_heap", outer);

        text.Should().Contain("inspect_live_heap failed:");
        text.Should().Contain("InvalidOperationException");
        text.Should().Contain("PTRACE_ATTACH failed");
        text.Should().Contain("Exception chain:");
        text.Should().Contain("System.ComponentModel.Win32Exception");
        text.Should().Contain("Operation not permitted");
    }

    [Fact]
    public void ToolErrorSurfaceFilter_BuildErrorText_HandlesNullAndEmptyMessages()
    {
        var ex = new InvalidOperationException(string.Empty);
        var text = ToolErrorSurfaceFilter.BuildErrorText("snapshot_counters", ex);

        text.Should().Contain("snapshot_counters failed:");
        text.Should().Contain("(no message)", "the formatter must not render empty messages as blanks");
    }

    [Fact]
    public void ToolErrorSurfaceFilter_IsRethrowable_RethrowsCanceledOperationAndProtocolExceptions()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var canceled = new OperationCanceledException("user cancel", cts.Token);
        ToolErrorSurfaceFilter.IsRethrowable(canceled, cts.Token).Should().BeTrue();

        var notCanceled = new OperationCanceledException("stale");
        ToolErrorSurfaceFilter.IsRethrowable(notCanceled, CancellationToken.None).Should().BeFalse(
            "cancellation rethrow only applies when the request-bound CT was cancelled");

        var protoEx = new ModelContextProtocol.McpProtocolException("bad frame");
        ToolErrorSurfaceFilter.IsRethrowable(protoEx, CancellationToken.None).Should().BeTrue();

        var arbitrary = new InvalidOperationException("anything else");
        ToolErrorSurfaceFilter.IsRethrowable(arbitrary, CancellationToken.None).Should().BeFalse(
            "every non-protocol/non-cancel exception must be surfaced as a structured error");
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

    /// <summary>
    /// Trivial resolver that echoes the explicit pid back as a CoreCLR ProcessContext, so the
    /// guard tests can keep targeting an explicit pid without standing up the live discovery
    /// + capability stack. Returns NoDotnetProcessFound if pid is null/0.
    /// </summary>
    internal static IProcessContextResolver EchoResolver()
        => new EchoProcessContextResolver();

    private sealed class EchoProcessContextResolver : IProcessContextResolver
    {
        public Task<ProcessContextResolution> ResolveAsync(int? requestedProcessId, CancellationToken cancellationToken = default)
        {
            if (requestedProcessId is not int pid || pid <= 0)
            {
                return Task.FromResult(new ProcessContextResolution(
                    Context: null,
                    Error: new DiagnosticError("NoDotnetProcessFound", "no live processes in stub resolver"),
                    Candidates: null));
            }
            var ctx = new ProcessContext(pid, RuntimeFlavor.CoreClr, RuntimeVersion: null, CanSampleCpu: true, CanCollectGcDump: true, AutoResolved: false);
            return Task.FromResult(new ProcessContextResolution(Context: ctx, Error: null, Candidates: null));
        }
    }
}
