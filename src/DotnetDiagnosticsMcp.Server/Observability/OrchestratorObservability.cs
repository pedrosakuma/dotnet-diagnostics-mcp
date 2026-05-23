using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Text.Json;
using DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;
using DotnetDiagnosticsMcp.Server.Security;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnosticsMcp.Server.Observability;

public sealed class OrchestratorObservability : IDisposable
{
    public const string MeterName = "DotnetDiagnosticsMcp.Server.Orchestrator";
    public const string ActivitySourceName = "DotnetDiagnosticsMcp.Server.Orchestrator";
    public const string MetricsReadScope = "metrics-read";

    private readonly Counter<long> _attachTotal;
    private readonly Histogram<double> _attachDurationSeconds;
    private readonly Counter<long> _proxyRequestsTotal;
    private readonly Counter<long> _reaperEvictedTotal;
    private readonly ActivitySource _activitySource;
    private readonly AuditLogWriter _auditLogWriter;

    public OrchestratorObservability(
        IMeterFactory meterFactory,
        IInvestigationStore store,
        AuditLogWriter auditLogWriter)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(auditLogWriter);

        var meter = meterFactory.Create(MeterName);
        _attachTotal = meter.CreateCounter<long>(
            name: "mcp_orchestrator_attach_total",
            description: "Total orchestrator attach attempts by outcome and reason.");
        _attachDurationSeconds = meter.CreateHistogram<double>(
            name: "mcp_orchestrator_attach_duration_seconds",
            unit: "s",
            description: "Duration of orchestrator attach operations.");
        _proxyRequestsTotal = meter.CreateCounter<long>(
            name: "mcp_orchestrator_proxy_requests_total",
            description: "Total proxied orchestrator tool calls by tool and outcome.");
        _reaperEvictedTotal = meter.CreateCounter<long>(
            name: "mcp_orchestrator_reaper_evicted_total",
            description: "Total investigation reaper evictions by reason.");
        meter.CreateObservableGauge<long>(
            name: "mcp_orchestrator_active_investigations",
            observeValue: () => CountActiveInvestigations(store),
            description: "Current number of non-terminal orchestrator investigations.");

        _activitySource = new ActivitySource(ActivitySourceName);
        _auditLogWriter = auditLogWriter;
    }

    public Activity? StartAttachActivity(string podNamespace, string podName, string? containerName)
    {
        var activity = _activitySource.StartActivity("Orchestrator.AttachAsync", ActivityKind.Internal);
        activity?.SetTag("k8s.namespace.name", podNamespace);
        activity?.SetTag("k8s.pod.name", podName);
        activity?.SetTag("container.name", containerName ?? string.Empty);
        return activity;
    }

    public Activity? StartProxyActivity(string handleId, string toolName)
    {
        var activity = _activitySource.StartActivity("Orchestrator.ProxyToolCall", ActivityKind.Internal);
        activity?.SetTag("mcp.investigation.handle", handleId);
        activity?.SetTag("tool.name", toolName);
        return activity;
    }

    public void RecordAttach(
        BearerPrincipal? principal,
        string podNamespace,
        string podName,
        string? containerName,
        string? handleId,
        string outcome,
        string reason,
        TimeSpan duration)
    {
        _attachTotal.Add(1, new KeyValuePair<string, object?>("outcome", outcome), new KeyValuePair<string, object?>("reason", reason));
        _attachDurationSeconds.Record(duration.TotalSeconds);

        _auditLogWriter.Write(
            new AuditPayload(
                EventName: "audit.orchestrator.attach",
                Principal: principal?.Name ?? "system",
                Outcome: outcome,
                HandleId: handleId,
                Reason: reason,
                ToolName: null,
                Namespace: podNamespace,
                PodName: podName,
                ContainerName: containerName,
                DurationSeconds: duration.TotalSeconds));
    }

    public void RecordDetach(
        BearerPrincipal? principal,
        string handleId,
        string reason,
        string outcome)
    {
        _auditLogWriter.Write(
            new AuditPayload(
                EventName: "audit.orchestrator.detach",
                Principal: principal?.Name ?? "system",
                Outcome: outcome,
                HandleId: handleId,
                Reason: reason,
                ToolName: null,
                Namespace: null,
                PodName: null,
                ContainerName: null,
                DurationSeconds: null));
    }

    public void RecordProxyCall(BearerPrincipal? principal, string handleId, string toolName, string outcome)
    {
        _proxyRequestsTotal.Add(
            1,
            new KeyValuePair<string, object?>("tool", toolName),
            new KeyValuePair<string, object?>("outcome", outcome));

        _auditLogWriter.Write(
            new AuditPayload(
                EventName: "audit.orchestrator.proxy_call",
                Principal: principal?.Name ?? "system",
                Outcome: outcome,
                HandleId: handleId,
                Reason: null,
                ToolName: toolName,
                Namespace: null,
                PodName: null,
                ContainerName: null,
                DurationSeconds: null));
    }

    public void RecordReaperEviction(string reason)
        => _reaperEvictedTotal.Add(1, new KeyValuePair<string, object?>("reason", reason));

    private static long CountActiveInvestigations(IInvestigationStore store)
        => store.Snapshot().LongCount(handle => handle.State is InvestigationState.Active or InvestigationState.Attaching);

    public void Dispose() => _activitySource.Dispose();
}

public sealed class AuditLogWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly TextWriter _writer;
    private readonly object _gate = new();

    public AuditLogWriter(TextWriter? writer = null)
    {
        _writer = writer ?? Console.Out;
    }

    internal void Write(AuditPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var record = new Dictionary<string, object?>
        {
            ["@timestamp"] = DateTimeOffset.UtcNow,
            ["event.name"] = payload.EventName,
            ["event.outcome"] = payload.Outcome,
            ["user.name"] = payload.Principal,
            ["mcp.investigation.handle"] = payload.HandleId,
            ["reason"] = payload.Reason,
            ["tool.name"] = payload.ToolName,
            ["k8s.namespace.name"] = payload.Namespace,
            ["k8s.pod.name"] = payload.PodName,
            ["container.name"] = payload.ContainerName,
            ["event.duration_seconds"] = payload.DurationSeconds,
        };

        lock (_gate)
        {
            _writer.WriteLine(JsonSerializer.Serialize(record, SerializerOptions));
            _writer.Flush();
        }
    }
}

internal sealed record AuditPayload(
    string EventName,
    string Principal,
    string Outcome,
    string? HandleId,
    string? Reason,
    string? ToolName,
    string? Namespace,
    string? PodName,
    string? ContainerName,
    double? DurationSeconds);
