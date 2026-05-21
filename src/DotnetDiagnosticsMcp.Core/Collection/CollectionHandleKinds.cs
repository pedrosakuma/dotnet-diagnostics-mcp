namespace DotnetDiagnosticsMcp.Core.Collection;

/// <summary>
/// Canonical <c>kind</c> string each collector registers with <see cref="Drilldown.IDiagnosticHandleStore"/>.
/// The unified <c>query_collection</c> tool dispatches on these values to pick the right view shape.
/// Keep these stable — they are observable on every handle returned to MCP clients.
/// </summary>
public static class CollectionHandleKinds
{
    /// <summary>Handle backing a <see cref="Counters.CounterSnapshot"/> emitted by <c>snapshot_counters</c>.</summary>
    public const string Counters = "counters";

    /// <summary>Handle backing a <see cref="Exceptions.ExceptionSnapshot"/> emitted by <c>collect_exceptions</c>.</summary>
    public const string ExceptionSnapshot = "exception-snapshot";

    /// <summary>Handle backing a <see cref="Gc.GcSummary"/> emitted by <c>collect_gc_events</c>.</summary>
    public const string GcEvents = "gc-events";

    /// <summary>Handle backing an <see cref="EventSources.EventSourceCapture"/> emitted by <c>collect_event_source</c>.</summary>
    public const string EventSource = "event-source";

    /// <summary>Handle backing an <see cref="Activities.ActivityCapture"/> emitted by <c>collect_activities</c>.</summary>
    public const string Activities = "activities";
}
