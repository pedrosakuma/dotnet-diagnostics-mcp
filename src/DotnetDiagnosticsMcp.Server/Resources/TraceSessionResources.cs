using System.ComponentModel;
using System.Text.Json;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Drilldown;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Resources;

/// <summary>
/// Templated Resource that exposes the in-memory artifact behind a drill-down handle as a
/// read-only blob. Complements <c>get_call_tree</c>: tools are for the LLM to drive the
/// investigation; Resources are for clients (or the LLM itself) to pull the raw artifact on
/// demand without going through a tool round-trip.
/// </summary>
[McpServerResourceType]
public sealed class TraceSessionResources
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerResource(
        UriTemplate = "trace://session/{handle}",
        Name = "trace-session",
        Title = "Drill-down trace session",
        MimeType = "application/json")]
    [Description(
        "JSON snapshot of the artifact registered under a drill-down handle. " +
        "For cpu-sample handles the body is the full call tree; for other kinds it's the typed payload. " +
        "Returns an error contents block when the handle is unknown or expired.")]
    public static string ReadSession(IDiagnosticHandleStore handles, string handle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);

        var cpu = handles.TryGet<CpuSampleTraceArtifact>(handle);
        if (cpu is not null)
        {
            return JsonSerializer.Serialize(new
            {
                kind = "cpu-sample",
                processId = cpu.ProcessId,
                startedAt = cpu.StartedAt,
                duration = cpu.Duration,
                totalSamples = cpu.TotalSamples,
                root = cpu.Root,
            }, JsonOptions);
        }

        return JsonSerializer.Serialize(new
        {
            kind = "unknown",
            error = $"Handle '{handle}' is unknown or expired. Re-run the collector to issue a fresh handle.",
        }, JsonOptions);
    }
}
