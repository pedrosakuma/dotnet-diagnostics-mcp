using System.ComponentModel;
using System.Text.Json;
using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.Security;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Resources;

/// <summary>
/// Templated Resource that exposes a previously-captured <see cref="HeapSnapshotArtifact"/>
/// keyed by its drilldown handle as a read-only JSON blob. Complements
/// <c>query_heap_snapshot</c>: the tool answers narrow, parameterized questions; this Resource
/// returns the full snapshot for clients that prefer to pull and analyze locally.
/// </summary>
[McpServerResourceType]
public sealed class HeapSnapshotResources
{
    [McpServerResource(
        UriTemplate = "heap://snapshot/{handle}",
        Name = "heap-snapshot",
        Title = "Drilldown heap snapshot",
        MimeType = "application/json")]
    [Description(
        "JSON snapshot of the HeapSnapshotArtifact registered under a drilldown handle by inspect_dump " +
        "or inspect_live_heap. Includes runtime info, heap totals, top-N types (snapshot retains up to ~200), " +
        "any walked retention paths, and provenance fields (origin, captured-at, walk duration). " +
        "Duplicate-string previews are ALWAYS metadata-only on this Resource (issue #165 / H4) — Resources have " +
        "no per-call opt-in surface, so we keep them closed regardless of `Diagnostics:AllowSensitiveHeapValues`. " +
        "Use `query_heap_snapshot view=duplicate-strings includeSensitiveValues=true` (subject to the server gate) " +
        "to reveal redacted previews. " +
        "Returns an error contents block when the handle is unknown or expired.")]
    public static string ReadSnapshot(
        IDiagnosticHandleStore handles,
        string handle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);

        var snapshot = handles.TryGet<HeapSnapshotArtifact>(handle);
        if (snapshot is null)
        {
            return JsonSerializer.Serialize(
                new HeapSnapshotErrorPayload(
                    Kind: "unknown",
                    Error: $"Handle '{handle}' is unknown or expired. Re-run inspect_dump or inspect_live_heap to issue a fresh handle."),
                HeapSnapshotJsonContext.Default.HeapSnapshotErrorPayload);
        }

        // B4 / issue #165 / H4: Resources cannot take a per-call `includeSensitiveValues`
        // parameter, so we never emit raw previews here regardless of the server gate.
        // Callers that need previews must go through `query_heap_snapshot` which enforces
        // both the server flag AND the per-call opt-in.
        var sanitized = SanitizeSnapshot(snapshot);
        return JsonSerializer.Serialize(sanitized, HeapSnapshotJsonContext.Default.HeapSnapshotArtifact);
    }

    private static HeapSnapshotArtifact SanitizeSnapshot(HeapSnapshotArtifact snapshot)
    {
        if (snapshot.DuplicateStrings is null || snapshot.DuplicateStrings.Count == 0) return snapshot;

        var sanitizedDuplicates = snapshot.DuplicateStrings.Select(s =>
            new DuplicateStringStat(SensitiveDataRedactor.MetadataOnlyPlaceholder, s.StringLength, s.InstanceCount, s.TotalBytes, s.PreviewTruncated))
            .ToArray();

        return snapshot with { DuplicateStrings = sanitizedDuplicates };
    }
}

