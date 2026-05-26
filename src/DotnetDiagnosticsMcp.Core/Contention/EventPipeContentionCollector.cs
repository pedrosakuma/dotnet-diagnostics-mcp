using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.Contention;

public sealed class EventPipeContentionCollector : IContentionCollector
{
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    private const long ContentionKeyword = 0x4000;
    private const string UnknownCallSite = "(unknown)";
    private const string UnknownModule = "(unknown)";

    private readonly ILogger<EventPipeContentionCollector> _logger;

    public EventPipeContentionCollector(ILogger<EventPipeContentionCollector>? logger = null)
    {
        _logger = logger ?? NullLogger<EventPipeContentionCollector>.Instance;
    }

    public async Task<ContentionSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        var providers = new[]
        {
            new EventPipeProvider(RuntimeProvider, EventLevel.Verbose, ContentionKeyword),
        };

        var client = new DiagnosticsClient(processId);
        var session = await client
            .StartEventPipeSessionAsync(providers, requestRundown: false, circularBufferMB: 64, cancellationToken)
            .ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        var notes = new HashSet<string>(StringComparer.Ordinal);
        var events = new List<ContentionEventSample>();
        var pendingByThread = new Dictionary<int, Stack<PendingContention>>();

        var processingTask = Task.Run(() =>
        {
            try
            {
                using var source = new Microsoft.Diagnostics.Tracing.EventPipeEventSource(session.EventStream);
                source.Clr.ContentionStart += data =>
                {
                    var pending = new PendingContention(
                        StartedAt: ToUtcOffset(data.TimeStamp),
                        LockId: data.LockID,
                        AssociatedObjectId: data.AssociatedObjectID,
                        OwnerManagedThreadId: data.LockOwnerThreadID > 0 && data.LockOwnerThreadID <= int.MaxValue
                            ? (int)data.LockOwnerThreadID
                            : null,
                        CallSite: ExtractCallSite(data, notes));

                    if (!pendingByThread.TryGetValue(data.ThreadID, out var stack))
                    {
                        stack = new Stack<PendingContention>();
                        pendingByThread[data.ThreadID] = stack;
                    }

                    stack.Push(pending);
                };

                source.Clr.ContentionStop += data =>
                {
                    if (!pendingByThread.TryGetValue(data.ThreadID, out var stack) || stack.Count == 0)
                    {
                        notes.Add("Observed ContentionStop without a matching ContentionStart; the event was skipped.");
                        return;
                    }

                    var pending = stack.Pop();
                    var stoppedAt = ToUtcOffset(data.TimeStamp);
                    var durationFromStop = data.DurationNs > 0
                        ? TimeSpan.FromMilliseconds(data.DurationNs / 1_000_000d)
                        : stoppedAt - pending.StartedAt;
                    var waitDuration = durationFromStop >= TimeSpan.Zero ? durationFromStop : TimeSpan.Zero;

                    events.Add(new ContentionEventSample(
                        StartedAt: pending.StartedAt,
                        StoppedAt: stoppedAt,
                        Duration: waitDuration,
                        ContendingThreadId: data.ThreadID,
                        OwnerManagedThreadId: pending.OwnerManagedThreadId,
                        LockId: pending.LockId,
                        AssociatedObjectId: pending.AssociatedObjectId,
                        CallSiteMethod: pending.CallSite.Method,
                        CallSiteModule: pending.CallSite.Module));
                };

                source.Process();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EventPipe contention source ended for pid {Pid}.", processId);
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

        var unfinishedStarts = pendingByThread.Sum(static entry => entry.Value.Count);
        if (unfinishedStarts > 0)
        {
            notes.Add($"{unfinishedStarts} ContentionStart event(s) did not observe a matching stop before the window ended.");
        }

        var orderedEvents = events
            .OrderByDescending(static item => item.Duration)
            .ThenBy(static item => item.CallSiteMethod, StringComparer.Ordinal)
            .ToList();

        if (orderedEvents.Count == 0)
        {
            if (OperatingSystem.IsLinux())
            {
                notes.Add("No lock contention events were observed in the collection window. Current Linux runtimes may not emit ContentionStart/Stop over EventPipe even when monitor-lock-contention counters rise.");
            }
            else
            {
                notes.Add("No lock contention events were observed in the collection window.");
            }
        }

        var durations = orderedEvents
            .Select(static item => item.Duration)
            .OrderBy(static item => item)
            .ToList();
        var distinctMonitors = orderedEvents
            .Where(static item => item.LockId != 0)
            .Select(static item => item.LockId)
            .Distinct()
            .Count();
        var totalDuration = orderedEvents.Aggregate(TimeSpan.Zero, static (sum, item) => sum + item.Duration);

        return new ContentionSnapshot(
            ProcessId: processId,
            StartedAt: startedAt,
            Duration: duration,
            TotalEvents: orderedEvents.Count,
            DistinctMonitors: distinctMonitors,
            TotalContentionDuration: totalDuration,
            P50ContentionDuration: Percentile(durations, 0.50),
            P95ContentionDuration: Percentile(durations, 0.95),
            MaxContentionDuration: durations.Count > 0 ? durations[^1] : TimeSpan.Zero,
            Events: orderedEvents,
            Notes: notes.OrderBy(static note => note, StringComparer.Ordinal).ToList());
    }

    private static DateTimeOffset ToUtcOffset(DateTime timestamp) =>
        new(timestamp.ToUniversalTime(), TimeSpan.Zero);

    private static TimeSpan Percentile(List<TimeSpan> orderedDurations, double percentile)
    {
        if (orderedDurations.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var index = (int)Math.Ceiling((orderedDurations.Count * percentile) - 1);
        index = Math.Clamp(index, 0, orderedDurations.Count - 1);
        return orderedDurations[index];
    }

    private static CallSite ExtractCallSite(ContentionStartTraceData data, HashSet<string> notes)
    {
        var stack = data.CallStack();
        if (stack is null)
        {
            notes.Add("ContentionStart did not carry call stacks in this session; byCallSite falls back to '(unknown)'.");
            return new CallSite(UnknownCallSite, UnknownModule);
        }

        CallSite? fallback = null;
        var frame = stack;
        while (frame is not null)
        {
            var candidate = FormatCallSite(frame);
            if (candidate is not null)
            {
                fallback ??= candidate;
                if (!IsInfrastructureFrame(candidate.Method))
                {
                    return candidate;
                }
            }

            frame = frame.Caller;
        }

        return fallback ?? new CallSite(UnknownCallSite, UnknownModule);
    }

    private static CallSite? FormatCallSite(TraceCallStack frame)
    {
        var method = frame.CodeAddress?.FullMethodName;
        if (string.IsNullOrWhiteSpace(method))
        {
            method = frame.CodeAddress?.Method?.FullMethodName;
        }

        if (string.IsNullOrWhiteSpace(method))
        {
            method = frame.ToString();
        }

        if (string.IsNullOrWhiteSpace(method))
        {
            return null;
        }

        var module = frame.CodeAddress?.ModuleName;
        if (string.IsNullOrWhiteSpace(module))
        {
            module = frame.CodeAddress?.Method?.MethodModuleFile?.Name;
        }

        if (string.IsNullOrWhiteSpace(module))
        {
            module = frame.CodeAddress?.ModuleFile?.Name;
        }

        return new CallSite(method.Trim(), string.IsNullOrWhiteSpace(module) ? UnknownModule : Path.GetFileName(module.Trim()));
    }

    private static bool IsInfrastructureFrame(string frame)
        => frame.StartsWith("System.Threading.Monitor", StringComparison.Ordinal)
            || frame.StartsWith("System.Threading.Lock", StringComparison.Ordinal)
            || frame.Contains("System.Private.CoreLib!System.Threading.Monitor", StringComparison.Ordinal)
            || frame.Contains("AwareLock", StringComparison.Ordinal)
            || frame.Contains("ExecutionContext.Run", StringComparison.Ordinal)
            || frame.Contains("ThreadPoolWorkQueue", StringComparison.Ordinal)
            || frame.Contains("PortableThreadPool", StringComparison.Ordinal)
            || frame.Contains("Task.Execute", StringComparison.Ordinal)
            || frame.Contains("AwaitTaskContinuation", StringComparison.Ordinal);

    private sealed record PendingContention(
        DateTimeOffset StartedAt,
        ulong LockId,
        ulong AssociatedObjectId,
        int? OwnerManagedThreadId,
        CallSite CallSite);

    private sealed record CallSite(string Method, string Module);
}
