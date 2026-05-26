namespace DotnetDiagnosticsMcp.Core.ProcessDiscovery;

/// <summary>One in-flight ASP.NET Core request observed during a short EventPipe window.</summary>
public sealed record InFlightHttpRequest(
    string TraceId,
    string Endpoint,
    string Method,
    double StartedAtMs,
    int ThreadId,
    IReadOnlyList<string> TopFrames);

/// <summary>Aggregate result for <c>inspect_process(view="requests-now")</c>.</summary>
public sealed record RequestsNowSnapshot(
    int ProcessId,
    DateTimeOffset CapturedAt,
    TimeSpan Window,
    IReadOnlyList<InFlightHttpRequest> Requests);
