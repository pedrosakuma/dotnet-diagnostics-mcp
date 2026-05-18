using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDbgMcp.Core.Gc;

/// <summary>
/// Default <see cref="IGcCollector"/> backed by an EventPipe session subscribed to the
/// runtime GC keyword (0x1) on <c>Microsoft-Windows-DotNETRuntime</c>. Pairs
/// GCStart/GCStop events to compute pause durations per collection.
/// </summary>
public sealed class EventPipeGcCollector : IGcCollector
{
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    private const long GcKeyword = 0x1;

    private readonly ILogger<EventPipeGcCollector> _logger;

    public EventPipeGcCollector(ILogger<EventPipeGcCollector>? logger = null)
    {
        _logger = logger ?? NullLogger<EventPipeGcCollector>.Instance;
    }

    public async Task<GcSummary> CollectAsync(
        int processId,
        TimeSpan duration,
        int maxEvents = 200,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        if (maxEvents < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEvents), "maxEvents must be >= 1.");
        }

        var providers = new[]
        {
            new EventPipeProvider(RuntimeProvider, EventLevel.Informational, GcKeyword),
        };

        var client = new DiagnosticsClient(processId);
        var session = await client
            .StartEventPipeSessionAsync(providers, requestRundown: false, circularBufferMB: 64, cancellationToken)
            .ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        var events = new ConcurrentQueue<GcEvent>();
        var pending = new ConcurrentDictionary<long, GCStartTraceData>();

        var processingTask = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                source.Clr.GCStart += traceEvent =>
                {
                    var data = (GCStartTraceData)traceEvent.Clone();
                    pending[data.Count] = data;
                };

                source.Clr.GCStop += traceEvent =>
                {
                    if (!pending.TryRemove(traceEvent.Count, out var start))
                    {
                        return;
                    }

                    if (events.Count >= maxEvents)
                    {
                        return;
                    }

                    var pause = traceEvent.TimeStamp - start.TimeStamp;
                    events.Enqueue(new GcEvent(
                        Timestamp: new DateTimeOffset(start.TimeStamp.ToUniversalTime(), TimeSpan.Zero),
                        Generation: start.Depth,
                        Reason: start.Reason.ToString(),
                        Type: start.Type.ToString(),
                        PauseDuration: pause < TimeSpan.Zero ? TimeSpan.Zero : pause));
                };

                source.Process();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EventPipe GC source ended for pid {Pid}.", processId);
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

        var collected = events.ToList();
        var totalPause = collected.Aggregate(TimeSpan.Zero, (acc, e) => acc + e.PauseDuration);
        var maxPause = collected.Count == 0 ? TimeSpan.Zero : collected.Max(e => e.PauseDuration);
        var perGen = collected
            .GroupBy(e => e.Generation)
            .Select(g => new GenerationStats(g.Key, g.Count()))
            .OrderBy(g => g.Generation)
            .ToList();

        return new GcSummary(
            ProcessId: processId,
            StartedAt: startedAt,
            Duration: duration,
            TotalCollections: collected.Count,
            TotalPauseTime: totalPause,
            MaxPauseTime: maxPause,
            Generations: perGen,
            Events: collected);
    }
}
