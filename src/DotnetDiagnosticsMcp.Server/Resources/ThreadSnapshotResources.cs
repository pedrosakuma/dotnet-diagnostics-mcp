using System.ComponentModel;
using System.Text.Json;
using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Threads;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Resources;

/// <summary>
/// Templated Resource that exposes a previously-captured <see cref="ThreadSnapshotArtifact"/>
/// keyed by its drilldown handle as a read-only JSON blob.
/// </summary>
[McpServerResourceType]
public sealed class ThreadSnapshotResources
{
    [McpServerResource(
        UriTemplate = "thread://snapshot/{handle}",
        Name = "thread-snapshot",
        Title = "Drilldown thread + lock snapshot",
        MimeType = "application/json")]
    [Description(
        "JSON snapshot of the ThreadSnapshotArtifact registered under a drilldown handle by " +
        "collect_thread_snapshot. Includes runtime info, every managed thread (state, stack frames " +
        "with MethodIdentity handoff for dotnet-assembly-mcp, inferred wait reason), the lock " +
        "(SyncBlock) graph with owners + waiter counts, and optional ThreadPool counters/queues " +
        "when captured by the backend. Returns an error contents block when the handle is unknown " +
        "or expired.")]
    public static string ReadSnapshot(IDiagnosticHandleStore handles, string handle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);

        var snapshot = handles.TryGet<ThreadSnapshotArtifact>(handle);
        if (snapshot is null)
        {
            return JsonSerializer.Serialize(
                new ThreadSnapshotErrorPayload(
                    Kind: "unknown",
                    Error: $"Handle '{handle}' is unknown or expired. Re-run collect_thread_snapshot to issue a fresh handle."),
                ThreadSnapshotJsonContext.Default.ThreadSnapshotErrorPayload);
        }

        return JsonSerializer.Serialize(snapshot, ThreadSnapshotJsonContext.Default.ThreadSnapshotArtifact);
    }
}
