namespace DotnetDbgMcp.Core.EventSources;

/// <summary>
/// Generic EventSource passthrough: subscribes to any EventSource by name (e.g.
/// <c>System.Net.Http</c>, <c>Microsoft.AspNetCore.Hosting</c> or a custom one) and
/// returns up to a caller-specified number of events emitted during the window.
/// </summary>
public interface IEventSourceCollector
{
    Task<EventSourceCapture> CaptureAsync(
        int processId,
        string providerName,
        TimeSpan duration,
        long keywords = -1,
        int eventLevel = 5,
        int maxEvents = 200,
        CancellationToken cancellationToken = default);
}
