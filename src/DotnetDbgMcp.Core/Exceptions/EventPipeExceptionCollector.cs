using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Globalization;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDbgMcp.Core.Exceptions;

/// <summary>
/// Default <see cref="IExceptionCollector"/> backed by an EventPipe session subscribed to the
/// runtime Exception keyword (0x8000) on <c>Microsoft-Windows-DotNETRuntime</c>.
/// </summary>
public sealed class EventPipeExceptionCollector : IExceptionCollector
{
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    private const long ExceptionKeyword = 0x8000;

    private readonly ILogger<EventPipeExceptionCollector> _logger;

    public EventPipeExceptionCollector(ILogger<EventPipeExceptionCollector>? logger = null)
    {
        _logger = logger ?? NullLogger<EventPipeExceptionCollector>.Instance;
    }

    public async Task<ExceptionSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        int maxRecent = 100,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        if (maxRecent < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRecent), "maxRecent must be >= 1.");
        }

        var providers = new[]
        {
            new EventPipeProvider(RuntimeProvider, EventLevel.Warning, ExceptionKeyword),
        };

        var client = new DiagnosticsClient(processId);
        var session = await client
            .StartEventPipeSessionAsync(providers, requestRundown: false, circularBufferMB: 64, cancellationToken)
            .ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        var recent = new ConcurrentQueue<ManagedExceptionEvent>();
        var counts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var total = 0;

        var processingTask = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                source.Clr.ExceptionStart += traceEvent =>
                {
                    Interlocked.Increment(ref total);
                    var type = traceEvent.ExceptionType ?? "(unknown)";
                    counts.AddOrUpdate(type, 1, static (_, v) => v + 1);

                    if (recent.Count < maxRecent)
                    {
                        recent.Enqueue(new ManagedExceptionEvent(
                            Timestamp: new DateTimeOffset(traceEvent.TimeStamp.ToUniversalTime(), TimeSpan.Zero),
                            ExceptionType: type,
                            ExceptionMessage: traceEvent.ExceptionMessage ?? string.Empty,
                            ExceptionHResult: "0x" + traceEvent.ExceptionHRESULT.ToString("X", CultureInfo.InvariantCulture),
                            ThreadId: traceEvent.ThreadID));
                    }
                };

                source.Process();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EventPipe exception source ended for pid {Pid}.", processId);
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

        var byType = counts
            .Select(kvp => new ExceptionCount(kvp.Key, kvp.Value))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.ExceptionType, StringComparer.Ordinal)
            .ToList();

        return new ExceptionSnapshot(
            ProcessId: processId,
            StartedAt: startedAt,
            Duration: duration,
            TotalExceptions: Volatile.Read(ref total),
            ByType: byType,
            Recent: recent.ToList());
    }
}
