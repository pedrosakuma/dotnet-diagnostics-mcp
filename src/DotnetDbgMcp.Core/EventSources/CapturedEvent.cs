namespace DotnetDbgMcp.Core.EventSources;

/// <summary>A single event captured from a user-specified EventSource.</summary>
public sealed record CapturedEvent(
    DateTimeOffset Timestamp,
    string Provider,
    string EventName,
    string Level,
    IReadOnlyDictionary<string, string> Payload);

/// <summary>Aggregated capture window for a custom EventSource probe.</summary>
public sealed record EventSourceCapture(
    int ProcessId,
    string Provider,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    int TotalEvents,
    IReadOnlyList<CapturedEvent> Events);
