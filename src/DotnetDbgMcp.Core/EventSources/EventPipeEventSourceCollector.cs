using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Globalization;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDbgMcp.Core.EventSources;

/// <summary>
/// Default <see cref="IEventSourceCollector"/>: opens an EventPipe session against a single
/// EventSource and snapshots every event (name + payload) it emits in the window.
/// </summary>
public sealed class EventPipeEventSourceCollector : IEventSourceCollector
{
    private readonly ILogger<EventPipeEventSourceCollector> _logger;

    public EventPipeEventSourceCollector(ILogger<EventPipeEventSourceCollector>? logger = null)
    {
        _logger = logger ?? NullLogger<EventPipeEventSourceCollector>.Instance;
    }

    public async Task<EventSourceCapture> CaptureAsync(
        int processId,
        string providerName,
        TimeSpan duration,
        long keywords = -1,
        int eventLevel = 5,
        int maxEvents = 200,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        if (maxEvents < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEvents), "maxEvents must be >= 1.");
        }

        if (eventLevel < 0 || eventLevel > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(eventLevel), "eventLevel must be 0..5 (matches System.Diagnostics.Tracing.EventLevel).");
        }

        var providers = new[]
        {
            new EventPipeProvider(providerName, (EventLevel)eventLevel, keywords),
        };

        var client = new DiagnosticsClient(processId);
        var session = await client
            .StartEventPipeSessionAsync(providers, requestRundown: false, circularBufferMB: 64, cancellationToken)
            .ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        var captured = new ConcurrentQueue<CapturedEvent>();
        var total = 0;

        var processingTask = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                source.Dynamic.All += traceEvent =>
                {
                    if (!string.Equals(traceEvent.ProviderName, providerName, StringComparison.Ordinal))
                    {
                        return;
                    }

                    Interlocked.Increment(ref total);
                    if (captured.Count >= maxEvents)
                    {
                        return;
                    }

                    var payload = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var name in traceEvent.PayloadNames ?? Array.Empty<string>())
                    {
                        try
                        {
                            var value = traceEvent.PayloadByName(name);
                            payload[name] = value switch
                            {
                                null => string.Empty,
                                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                                _ => value.ToString() ?? string.Empty,
                            };
                        }
                        catch (Exception)
                        {
                            payload[name] = "(unserializable)";
                        }
                    }

                    captured.Enqueue(new CapturedEvent(
                        Timestamp: new DateTimeOffset(traceEvent.TimeStamp.ToUniversalTime(), TimeSpan.Zero),
                        Provider: traceEvent.ProviderName,
                        EventName: traceEvent.EventName,
                        Level: traceEvent.Level.ToString(),
                        Payload: payload));
                };

                source.Process();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EventPipe custom source ended for pid {Pid} provider {Provider}.", processId, providerName);
            }
        }, cancellationToken);

        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { await session.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            try { await processingTask.ConfigureAwait(false); } catch (Exception) { }
            session.Dispose();
        }

        return new EventSourceCapture(
            ProcessId: processId,
            Provider: providerName,
            StartedAt: startedAt,
            Duration: duration,
            TotalEvents: Volatile.Read(ref total),
            Events: captured.ToList());
    }
}
