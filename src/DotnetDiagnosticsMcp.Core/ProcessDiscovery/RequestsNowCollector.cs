using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Threading.Channels;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DotnetDiagnosticsMcp.Core.Threads;

namespace DotnetDiagnosticsMcp.Core.ProcessDiscovery;

/// <summary>
/// Tracks ASP.NET Core <c>HttpRequestIn</c> Activity start/stop pairs over a short EventPipe window,
/// and snapshots the observed start-thread stack immediately so async requests do not get a later,
/// unrelated thread's frames attributed back to them.
/// </summary>
public sealed class RequestsNowCollector : IRequestsNowCollector
{
    private const string ProviderName = "Microsoft-Diagnostics-DiagnosticSource";
    private const long MessagesKeyword = 0x1;
    private const long EventsKeyword = 0x2;
    private const long ProviderKeywords = MessagesKeyword | EventsKeyword;
    private const string FilterArgumentName = "FilterAndPayloadSpecs";
    private const string TransformSuffix = ":-TraceId;SpanId;ParentSpanId;StartTimeTicks=StartTimeUtc.Ticks;DurationTicks=Duration.Ticks;ActivitySourceName=Source.Name;DisplayName;Tags=TagObjects.*Enumerate";
    private const string AspNetCoreSource = "Microsoft.AspNetCore";
    private const string AspNetCoreHostingSource = "Microsoft.AspNetCore.Hosting";
    private const string HttpRequestInOperation = "HttpRequestIn";

    private static readonly IReadOnlyDictionary<string, string> EmptyArguments = new Dictionary<string, string>(0, StringComparer.Ordinal);

    private readonly IThreadSnapshotInspector _threadSnapshotInspector;
    private readonly ILogger<RequestsNowCollector> _logger;

    public RequestsNowCollector(
        IThreadSnapshotInspector threadSnapshotInspector,
        ILogger<RequestsNowCollector>? logger = null)
    {
        _threadSnapshotInspector = threadSnapshotInspector;
        _logger = logger ?? NullLogger<RequestsNowCollector>.Instance;
    }

    public async Task<RequestsNowSnapshot> CollectAsync(
        int processId,
        TimeSpan window,
        int topFrames,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId), "Process id must be positive.");
        }

        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "Window must be positive.");
        }

        if (topFrames < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(topFrames), "topFrames must be >= 1.");
        }

        var client = new DiagnosticsClient(processId);
        var providerArguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FilterArgumentName] = BuildFilterSpec(),
        };

        var session = await client
            .StartEventPipeSessionAsync(
                [new EventPipeProvider(ProviderName, EventLevel.Verbose, ProviderKeywords, providerArguments)],
                requestRundown: false,
                circularBufferMB: 64,
                cancellationToken)
            .ConfigureAwait(false);

        var requests = new ConcurrentDictionary<string, PendingRequest>(StringComparer.Ordinal);
        var snapshotRequests = Channel.CreateUnbounded<SnapshotCaptureRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        var snapshotOptions = new ThreadSnapshotOptions(MaxFramesPerThread: Math.Max(16, topFrames));

        var snapshotTask = Task.Run(async () =>
        {
            await foreach (var snapshotRequest in snapshotRequests.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                try
                {
                    var threadSnapshot = await _threadSnapshotInspector
                        .InspectLiveAsync(processId, snapshotOptions, CancellationToken.None)
                        .ConfigureAwait(false);
                    var frames = FindFramesForThread(threadSnapshot, snapshotRequest.ThreadId, topFrames);
                    if (requests.TryGetValue(snapshotRequest.Key, out var existing))
                    {
                        requests.TryUpdate(snapshotRequest.Key, existing with { TopFrames = frames }, existing);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "Requests-now failed to capture immediate stack for pid {Pid} thread {ThreadId}.",
                        processId,
                        snapshotRequest.ThreadId);
                }
            }
        }, CancellationToken.None);

        var processingTask = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                source.Dynamic.All += traceEvent =>
                {
                    if (!string.Equals(traceEvent.ProviderName, ProviderName, StringComparison.Ordinal) ||
                        !TryCreateEvent(traceEvent, out var requestEvent))
                    {
                        return;
                    }

                    if (requestEvent.IsStart)
                    {
                        requests[requestEvent.Key] = requestEvent.PendingRequest!;
                        snapshotRequests.Writer.TryWrite(new SnapshotCaptureRequest(requestEvent.Key, requestEvent.PendingRequest!.ThreadId));
                    }
                    else
                    {
                        requests.TryRemove(requestEvent.Key, out _);
                    }
                };

                source.Process();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Requests-now EventPipe source ended for pid {Pid}.", processId);
            }
        }, cancellationToken);

        try
        {
            await Task.Delay(window, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { await session.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            try { await processingTask.ConfigureAwait(false); } catch (Exception) { }
            snapshotRequests.Writer.TryComplete();
            try { await snapshotTask.ConfigureAwait(false); } catch (Exception) { }
            session.Dispose();
        }

        var capturedAt = DateTimeOffset.UtcNow;
        var inFlight = requests.Values
            .Where(static request => request.TopFrames.Length > 0)
            .OrderBy(request => request.StartedAt)
            .ThenBy(request => request.Endpoint, StringComparer.Ordinal)
            .Select(request => new InFlightHttpRequest(
                TraceId: request.TraceId,
                Endpoint: request.Endpoint,
                Method: request.Method,
                StartedAtMs: Math.Max(0, (capturedAt - request.StartedAt).TotalMilliseconds),
                ThreadId: request.ThreadId,
                TopFrames: request.TopFrames))
            .ToArray();

        return new RequestsNowSnapshot(processId, capturedAt, window, inFlight);
    }

    private static string BuildFilterSpec() =>
        $"[AS]*/Start{TransformSuffix}\n[AS]*/Stop{TransformSuffix}";

    private static bool TryCreateEvent(TraceEvent traceEvent, out RequestEvent requestEvent)
    {
        requestEvent = default;

        var sourceName = FirstNonEmpty(
            FormatString(traceEvent.PayloadByName("ActivitySourceName")),
            FormatString(traceEvent.PayloadByName("SourceName")));
        if (!IsAspNetCoreRequestSource(sourceName))
        {
            return false;
        }

        var eventName = traceEvent.EventName ?? string.Empty;
        var isStart = eventName.EndsWith("Start", StringComparison.OrdinalIgnoreCase);
        var isStop = eventName.EndsWith("Stop", StringComparison.OrdinalIgnoreCase);
        if (!isStart && !isStop)
        {
            return false;
        }

        var operationName = FirstNonEmpty(
            FormatString(traceEvent.PayloadByName("ActivityName")),
            FormatString(traceEvent.PayloadByName("EventName")));
        if (!operationName.Contains(HttpRequestInOperation, StringComparison.Ordinal))
        {
            return false;
        }

        var arguments = ExtractArguments(traceEvent.PayloadByName("Arguments"));
        var tags = ParseTagPairs(GetArgument(arguments, "Tags"));
        var startedAt = ParseStartedAt(arguments) ?? new DateTimeOffset(traceEvent.TimeStamp.ToUniversalTime(), TimeSpan.Zero);
        var traceId = FirstNonEmpty(GetArgument(arguments, "TraceId"), FormatString(traceEvent.PayloadByName("TraceId")));
        var spanId = FirstNonEmpty(GetArgument(arguments, "SpanId"), FormatString(traceEvent.PayloadByName("SpanId")));
        var key = ComposeActivityId(traceId, spanId) ?? ComposeFallbackId(startedAt, traceEvent.ThreadID);

        requestEvent = new RequestEvent(
            Key: key,
            IsStart: isStart,
            PendingRequest: isStart
                ? new PendingRequest(
                    TraceId: string.IsNullOrWhiteSpace(traceId) ? key : traceId,
                    Endpoint: ResolveEndpoint(tags, arguments),
                    Method: ResolveMethod(tags, arguments),
                    StartedAt: startedAt,
                    ThreadId: traceEvent.ThreadID,
                    TopFrames: Array.Empty<string>())
                : null);
        return true;
    }

    private static string[] FindFramesForThread(ThreadSnapshotArtifact snapshot, int threadId, int topFrames) =>
        snapshot.Threads
            .Where(thread => unchecked((int)thread.OSThreadId) == threadId)
            .OrderByDescending(static thread => thread.Frames.Count)
            .ThenByDescending(static thread => thread.IsLikelyBlocked)
            .SelectMany(static thread => thread.Frames)
            .Take(topFrames)
            .Select(static frame => frame.DisplayName)
            .Where(static frame => !string.IsNullOrWhiteSpace(frame))
            .ToArray();

    private static bool IsAspNetCoreRequestSource(string sourceName) =>
        string.Equals(sourceName, AspNetCoreSource, StringComparison.Ordinal) ||
        string.Equals(sourceName, AspNetCoreHostingSource, StringComparison.Ordinal) ||
        sourceName.StartsWith(AspNetCoreSource + ".", StringComparison.Ordinal);

    private static string ResolveEndpoint(
        IReadOnlyDictionary<string, string> tags,
        IReadOnlyDictionary<string, string> arguments)
    {
        var displayName = GetArgument(arguments, "DisplayName");
        return FirstNonEmpty(
            GetTag(tags, "http.route"),
            GetTag(tags, "route"),
            GetTag(tags, "url.path"),
            GetTag(tags, "http.target"),
            GetTag(tags, "path"),
            GetArgument(arguments, "Path"),
            GetArgument(arguments, "RequestPath"),
            GetArgument(arguments, "Endpoint"),
            TryParseDisplayName(displayName).Endpoint,
            "(unknown)");
    }

    private static string ResolveMethod(
        IReadOnlyDictionary<string, string> tags,
        IReadOnlyDictionary<string, string> arguments)
    {
        var displayName = GetArgument(arguments, "DisplayName");
        return FirstNonEmpty(
            GetTag(tags, "http.request.method"),
            GetTag(tags, "http.method"),
            GetTag(tags, "method"),
            GetArgument(arguments, "Method"),
            GetArgument(arguments, "RequestMethod"),
            GetArgument(arguments, "HttpMethod"),
            TryParseDisplayName(displayName).Method,
            "(unknown)");
    }

    private static string? GetTag(IReadOnlyDictionary<string, string> tags, string name) =>
        tags.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static (string? Method, string? Endpoint) TryParseDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return default;
        }

        var separator = displayName.IndexOf(' ');
        if (separator <= 0 || separator >= displayName.Length - 1)
        {
            return default;
        }

        var method = displayName[..separator].Trim();
        var endpoint = displayName[(separator + 1)..].Trim();
        return method.Length == 0 || endpoint.Length == 0 ? default : (method, endpoint);
    }

    private static IReadOnlyDictionary<string, string> ExtractArguments(object? payload)
    {
        if (payload is null)
        {
            return EmptyArguments;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (payload is IEnumerable enumerable && payload is not string)
        {
            foreach (var item in enumerable)
            {
                if (item is null)
                {
                    continue;
                }

                if (TryGetKeyValue(item, out var key, out var value))
                {
                    result[key] = FormatString(value);
                }
            }
        }

        return result;
    }

    private static bool TryGetKeyValue(object item, out string key, out object? value)
    {
        if (item is IDictionary<string, object> dictionary)
        {
            key = dictionary.TryGetValue("Key", out var dictionaryKey) ? FormatString(dictionaryKey) : string.Empty;
            value = dictionary.TryGetValue("Value", out var dictionaryValue) ? dictionaryValue : null;
            return !string.IsNullOrWhiteSpace(key);
        }

        if (item is DictionaryEntry entry)
        {
            key = FormatString(entry.Key);
            value = entry.Value;
            return !string.IsNullOrWhiteSpace(key);
        }

        if (item is IDictionary nonGenericDictionary)
        {
            key = nonGenericDictionary.Contains("Key") ? FormatString(nonGenericDictionary["Key"]) : string.Empty;
            value = nonGenericDictionary.Contains("Value") ? nonGenericDictionary["Value"] : null;
            return !string.IsNullOrWhiteSpace(key);
        }

        var type = item.GetType();
        var keyProperty = type.GetProperty("Key");
        var valueProperty = type.GetProperty("Value");
        if (keyProperty is not null && valueProperty is not null)
        {
            key = FormatString(keyProperty.GetValue(item));
            value = valueProperty.GetValue(item);
            return !string.IsNullOrWhiteSpace(key);
        }

        key = string.Empty;
        value = null;
        return false;
    }

    private static IReadOnlyDictionary<string, string> ParseTagPairs(string? serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return EmptyArguments;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawPair in serialized.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = rawPair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = rawPair[..separator].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            result[key] = rawPair[(separator + 1)..].Trim();
        }

        return result;
    }

    private static DateTimeOffset? ParseStartedAt(IReadOnlyDictionary<string, string> arguments)
    {
        var raw = GetArgument(arguments, "StartTimeTicks");
        return long.TryParse(raw, out var ticks)
            ? new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc))
            : null;
    }

    private static string? GetArgument(IReadOnlyDictionary<string, string> arguments, string key) =>
        arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static string? ComposeActivityId(string? traceId, string? spanId) =>
        !string.IsNullOrWhiteSpace(traceId) && !string.IsNullOrWhiteSpace(spanId)
            ? traceId + "/" + spanId
            : null;

    private static string ComposeFallbackId(DateTimeOffset startedAt, int threadId) =>
        startedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + threadId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string FormatString(object? value) => value switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private sealed record PendingRequest(
        string TraceId,
        string Endpoint,
        string Method,
        DateTimeOffset StartedAt,
        int ThreadId,
        string[] TopFrames);

    private readonly record struct SnapshotCaptureRequest(
        string Key,
        int ThreadId);

    private readonly record struct RequestEvent(
        string Key,
        bool IsStart,
        PendingRequest? PendingRequest);
}
