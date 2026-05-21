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
            return JsonSerializer.Serialize(
                new CpuSampleSessionPayload(
                    Kind: "cpu-sample",
                    ProcessId: cpu.ProcessId,
                    StartedAt: cpu.StartedAt,
                    Duration: cpu.Duration,
                    TotalSamples: cpu.TotalSamples,
                    Root: CallTreeIdentityProjector.Stamp(cpu.Root, cpu.MethodIdentities)),
                TraceSessionJsonContext.Default.CpuSampleSessionPayload);
        }

        return JsonSerializer.Serialize(
            new UnknownSessionPayload(
                Kind: "unknown",
                Error: $"Handle '{handle}' is unknown or expired. Re-run the collector to issue a fresh handle."),
            TraceSessionJsonContext.Default.UnknownSessionPayload);
    }
}
