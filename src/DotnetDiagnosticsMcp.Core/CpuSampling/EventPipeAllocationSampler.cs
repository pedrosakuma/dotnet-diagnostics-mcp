using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.CpuSampling;

/// <summary>
/// Collects allocation samples via the <c>GCAllocationTick</c> event from
/// <c>Microsoft-Windows-DotNETRuntime</c> (keyword <c>GCKeyword=0x1</c>, level Verbose).
/// This event fires roughly every 100 KB of total managed allocations and carries the
/// <c>TypeName</c> of the most recently allocated object. It is available on both CoreCLR
/// and NativeAOT, making it suitable for answering "who is allocating?" on runtimes where
/// heap introspection via ClrMD is unavailable.
/// </summary>
/// <remarks>
/// The sampling is statistical: each event samples the most recently allocated type when
/// the 100 KB threshold is crossed. High-volume types will be sampled proportionally more
/// often, making the top-N-by-bytes result a reliable proxy for actual allocation pressure.
/// </remarks>
public sealed class EventPipeAllocationSampler
{
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";

    /// <summary>
    /// GCKeyword on <c>Microsoft-Windows-DotNETRuntime</c>. Combined with Verbose level this
    /// enables <c>GCAllocationTick</c> events that fire every ~100 KB of managed allocations.
    /// </summary>
    private const long GcKeyword = 0x1L;

    private readonly ILogger<EventPipeAllocationSampler> _logger;

    public EventPipeAllocationSampler(ILogger<EventPipeAllocationSampler>? logger = null)
    {
        _logger = logger ?? NullLogger<EventPipeAllocationSampler>.Instance;
    }

    /// <summary>
    /// Samples allocations in the target process for <paramref name="duration"/> and
    /// aggregates the captured <c>GCAllocationTick</c> events into a top-N type summary.
    /// </summary>
    public async Task<AllocationSample> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN = 25,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be (0, 5min].");
        }

        if (topN <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(topN), "topN must be positive.");
        }

        var providers = new[]
        {
            // Verbose GCKeyword enables GCAllocationTick, which fires every ~100 KB of managed
            // allocations and carries TypeName, AllocationAmount64, and AllocationKind.
            new EventPipeProvider(RuntimeProvider, EventLevel.Verbose, GcKeyword),
        };

        var client = new DiagnosticsClient(processId);
        var session = await client
            .StartEventPipeSessionAsync(providers, requestRundown: false, circularBufferMB: 128, cancellationToken)
            .ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        var byType = new ConcurrentDictionary<string, TypeAccumulator>(StringComparer.Ordinal);
        long totalEvents = 0;
        long totalBytes = 0;

        var processingTask = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                source.Clr.GCAllocationTick += traceEvent =>
                {
                    var typeName = traceEvent.TypeName;
                    if (string.IsNullOrEmpty(typeName))
                    {
                        typeName = "<unknown>";
                    }

                    var bytes = traceEvent.AllocationAmount64;
                    var kind = traceEvent.AllocationKind == Microsoft.Diagnostics.Tracing.Parsers.Clr.GCAllocationKind.Large
                        ? HeapKind.Large
                        : HeapKind.Small;

                    Interlocked.Increment(ref totalEvents);
                    Interlocked.Add(ref totalBytes, bytes);

                    var acc = byType.GetOrAdd(typeName, static t => new TypeAccumulator(t));
                    acc.Add(bytes, kind);
                };

                source.Process();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EventPipe allocation source ended for pid {Pid}.", processId);
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

        var totalEventsSnap = Volatile.Read(ref totalEvents);
        var totalBytesSnap = Volatile.Read(ref totalBytes);

        var topByBytes = byType.Values
            .OrderByDescending(a => a.TotalBytes)
            .Take(topN)
            .Select(a => a.ToRecord())
            .ToList();

        var topByCount = byType.Values
            .OrderByDescending(a => a.EventCount)
            .Take(topN)
            .Select(a => a.ToRecord())
            .ToList();

        return new AllocationSample(
            processId,
            startedAt,
            duration,
            totalEventsSnap,
            totalBytesSnap,
            topByBytes,
            topByCount);
    }

    private sealed class TypeAccumulator
    {
        private long _totalBytes;
        private long _eventCount;
        private long _smallCount;
        private long _largeCount;

        public TypeAccumulator(string typeName) => TypeName = typeName;

        public string TypeName { get; }
        public long TotalBytes => Volatile.Read(ref _totalBytes);
        public long EventCount => Volatile.Read(ref _eventCount);

        public void Add(long bytes, HeapKind kind)
        {
            Interlocked.Add(ref _totalBytes, bytes);
            Interlocked.Increment(ref _eventCount);
            if (kind == HeapKind.Large)
                Interlocked.Increment(ref _largeCount);
            else
                Interlocked.Increment(ref _smallCount);
        }

        public AllocatedType ToRecord()
        {
            var large = Volatile.Read(ref _largeCount);
            var small = Volatile.Read(ref _smallCount);
            return new AllocatedType(TypeName, TotalBytes, EventCount, large > small ? HeapKind.Large : HeapKind.Small);
        }
    }
}
