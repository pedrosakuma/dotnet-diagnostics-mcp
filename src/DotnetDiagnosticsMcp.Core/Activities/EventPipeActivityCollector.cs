using System.Collections;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.Activities;

/// <summary>
/// Captures <see cref="System.Diagnostics.ActivitySource"/> stop events through the
/// <c>Microsoft-Diagnostics-DiagnosticSource</c> EventPipe provider.
/// </summary>
public sealed partial class EventPipeActivityCollector : IActivityCollector
{
    private const string ProviderName = "Microsoft-Diagnostics-DiagnosticSource";
    private const long MessagesKeyword = 0x1;
    private const long EventsKeyword = 0x2;
    private const long ProviderKeywords = MessagesKeyword | EventsKeyword;
    private const string FilterArgumentName = "FilterAndPayloadSpecs";
    private const string TransformSuffix = ":-TraceId;SpanId;ParentSpanId;StartTimeTicks=StartTimeUtc.Ticks;DurationTicks=Duration.Ticks;ActivitySourceName=Source.Name;Tags=TagObjects.*Enumerate";

    private static readonly IReadOnlyDictionary<string, string> EmptyArguments = new Dictionary<string, string>(0, StringComparer.Ordinal);
    private static readonly Dictionary<string, Regex> WildcardRegexCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object WildcardRegexLock = new();

    private readonly ILogger<EventPipeActivityCollector> _logger;

    public EventPipeActivityCollector(ILogger<EventPipeActivityCollector>? logger = null)
    {
        _logger = logger ?? NullLogger<EventPipeActivityCollector>.Instance;
    }

    public async Task<ActivityCapture> CollectAsync(
        int processId,
        TimeSpan duration,
        IReadOnlyList<string>? sources = null,
        int maxActivities = 200,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        if (maxActivities < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxActivities), "maxActivities must be >= 1.");
        }

        var normalizedSourceFilters = NormalizeSourceFilters(sources);
        var providerArguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FilterArgumentName] = BuildFilterSpec(["*"]),
        };

        var client = new DiagnosticsClient(processId);
        var session = await client
            .StartEventPipeSessionAsync(
                [new EventPipeProvider(ProviderName, EventLevel.Verbose, ProviderKeywords, providerArguments)],
                requestRundown: false,
                circularBufferMB: 64,
                cancellationToken)
            .ConfigureAwait(false);

        var collectionStartedAt = DateTimeOffset.UtcNow;
        var capturedActivities = new List<CapturedActivity>(Math.Min(maxActivities, 256));
        var totalActivities = 0;
        var completedActivities = 0;

        var processingTask = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                source.Dynamic.All += traceEvent =>
                {
                    if (!string.Equals(traceEvent.ProviderName, ProviderName, StringComparison.Ordinal) ||
                        !IsActivityStopEvent(traceEvent.EventName) ||
                        !TryCreateActivity(traceEvent, normalizedSourceFilters, collectionStartedAt, out var activity))
                    {
                        return;
                    }

                    totalActivities++;
                    completedActivities++;
                    if (capturedActivities.Count < maxActivities)
                    {
                        capturedActivities.Add(activity);
                    }
                };

                source.Process();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Activity EventPipe source ended for pid {Pid}.", processId);
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

        capturedActivities = capturedActivities
            .OrderBy(activity => activity.StartedAt)
            .ThenBy(activity => activity.SourceName, StringComparer.Ordinal)
            .ThenBy(activity => activity.OperationName, StringComparer.Ordinal)
            .ToList();

        return new ActivityCapture(
            ProcessId: processId,
            SourceFilters: normalizedSourceFilters,
            StartedAt: collectionStartedAt,
            Duration: duration,
            TotalActivities: totalActivities,
            CompletedActivities: completedActivities,
            Activities: capturedActivities,
            BySource: BuildSourceSummary(capturedActivities),
            ByOperation: BuildOperationSummary(capturedActivities));
    }

    private static bool TryCreateActivity(
        TraceEvent traceEvent,
        List<string>? sourceFilters,
        DateTimeOffset collectionStartedAt,
        out CapturedActivity activity)
    {
        activity = default!;

        var sourceName = FirstNonEmpty(
            FormatString(traceEvent.PayloadByName("ActivitySourceName")),
            FormatString(traceEvent.PayloadByName("SourceName")));
        var operationName = FirstNonEmpty(
            FormatString(traceEvent.PayloadByName("ActivityName")),
            FormatString(traceEvent.PayloadByName("EventName")));

        if (string.IsNullOrWhiteSpace(sourceName) ||
            string.IsNullOrWhiteSpace(operationName) ||
            !MatchesAnyFilter(sourceName, sourceFilters))
        {
            return false;
        }

        var arguments = ExtractArguments(traceEvent.PayloadByName("Arguments"));
        var traceId = NullIfEmpty(GetArgument(arguments, "TraceId"));
        var spanId = NullIfEmpty(GetArgument(arguments, "SpanId"));
        var parentSpanId = NormalizeParentSpanId(GetArgument(arguments, "ParentSpanId"));
        var startedAt = ParseStartedAt(arguments) ?? traceEvent.TimeStamp.ToUniversalTime();
        var duration = ParseDuration(arguments);
        var stoppedAt = duration is { } capturedDuration
            ? startedAt + capturedDuration
            : new DateTimeOffset(traceEvent.TimeStamp.ToUniversalTime(), TimeSpan.Zero);
        var id = ComposeActivityId(traceId, spanId) ?? ComposeFallbackId(sourceName, operationName, startedAt);
        var parentId = ComposeActivityId(traceId, parentSpanId);

        activity = new CapturedActivity(
            SourceName: sourceName,
            OperationName: operationName,
            Id: id,
            ParentId: parentId,
            TraceId: traceId,
            SpanId: spanId,
            ParentSpanId: parentSpanId,
            StartedAt: new DateTimeOffset(startedAt, TimeSpan.Zero),
            StoppedAt: stoppedAt,
            Duration: duration,
            Tags: ParseTagPairs(GetArgument(arguments, "Tags")));
        return true;
    }

    private static List<string>? NormalizeSourceFilters(IReadOnlyList<string>? sources)
    {
        if (sources is null || sources.Count == 0)
        {
            return null;
        }

        var normalized = sources
            .Where(static source => !string.IsNullOrWhiteSpace(source))
            .Select(static source => source.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return normalized.Count == 0 ? null : normalized;
    }

    private static string BuildFilterSpec(IReadOnlyList<string> providerFilters) =>
        string.Join('\n', providerFilters.Select(static filter => $"[AS]{filter}/Stop{TransformSuffix}"));

    private static bool IsActivityStopEvent(string eventName) =>
        eventName.EndsWith("Stop", StringComparison.OrdinalIgnoreCase);

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

    private static IReadOnlyDictionary<string, string> ParseTagPairs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return EmptyArguments;
        }

        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in TagPairRegex().Matches(raw))
        {
            var content = match.Groups[1].Value;
            var separator = content.IndexOf(", ", StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var key = content[..separator];
            var value = content[(separator + 2)..];
            if (!string.IsNullOrWhiteSpace(key))
            {
                tags[key] = value;
            }
        }

        return tags;
    }

    private static TimeSpan? ParseDuration(IReadOnlyDictionary<string, string> arguments)
    {
        var rawTicks = GetArgument(arguments, "DurationTicks");
        return long.TryParse(rawTicks, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) && ticks >= 0
            ? TimeSpan.FromTicks(ticks)
            : null;
    }

    private static DateTime? ParseStartedAt(IReadOnlyDictionary<string, string> arguments)
    {
        var rawTicks = GetArgument(arguments, "StartTimeTicks");
        if (!long.TryParse(rawTicks, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) || ticks <= 0)
        {
            return null;
        }

        return new DateTime(ticks, DateTimeKind.Utc);
    }

    private static string? GetArgument(IReadOnlyDictionary<string, string> arguments, string key) =>
        arguments.TryGetValue(key, out var value) ? value : null;

    private static string? NormalizeParentSpanId(string? value)
    {
        var normalized = NullIfEmpty(value);
        return string.IsNullOrEmpty(normalized) || normalized.All(static ch => ch == '0') ? null : normalized;
    }

    private static string? ComposeActivityId(string? traceId, string? spanId) =>
        string.IsNullOrWhiteSpace(traceId) || string.IsNullOrWhiteSpace(spanId)
            ? null
            : $"00-{traceId}-{spanId}-01";

    private static string ComposeFallbackId(string sourceName, string operationName, DateTime startedAt) =>
        $"{sourceName}:{operationName}:{startedAt.Ticks.ToString(CultureInfo.InvariantCulture)}";

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool MatchesAnyFilter(string sourceName, List<string>? filters)
    {
        if (filters is null || filters.Count == 0)
        {
            return true;
        }

        return filters.Any(pattern => WildcardToRegex(pattern).IsMatch(sourceName));
    }

    private static string FormatString(object? value) => value switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static List<ActivitySourceSummary> BuildSourceSummary(IReadOnlyList<CapturedActivity> activities) =>
        activities
            .GroupBy(activity => activity.SourceName, StringComparer.Ordinal)
            .Select(group => new ActivitySourceSummary(
                SourceName: group.Key,
                Count: group.Count(),
                CompletedCount: group.Count(activity => activity.Duration is not null),
                AverageDurationMs: AverageDurationMs(group.Select(activity => activity.Duration)),
                MaxDurationMs: MaxDurationMs(group.Select(activity => activity.Duration))))
            .OrderByDescending(summary => summary.Count)
            .ThenByDescending(summary => summary.MaxDurationMs)
            .ThenBy(summary => summary.SourceName, StringComparer.Ordinal)
            .ToList();

    private static List<ActivityOperationSummary> BuildOperationSummary(IReadOnlyList<CapturedActivity> activities) =>
        activities
            .GroupBy(activity => (activity.SourceName, activity.OperationName))
            .Select(group => new ActivityOperationSummary(
                SourceName: group.Key.SourceName,
                OperationName: group.Key.OperationName,
                Count: group.Count(),
                CompletedCount: group.Count(activity => activity.Duration is not null),
                AverageDurationMs: AverageDurationMs(group.Select(activity => activity.Duration)),
                MaxDurationMs: MaxDurationMs(group.Select(activity => activity.Duration))))
            .OrderByDescending(summary => summary.Count)
            .ThenByDescending(summary => summary.MaxDurationMs)
            .ThenBy(summary => summary.SourceName, StringComparer.Ordinal)
            .ThenBy(summary => summary.OperationName, StringComparer.Ordinal)
            .ToList();

    private static double AverageDurationMs(IEnumerable<TimeSpan?> durations)
    {
        var values = durations
            .Where(static duration => duration is not null)
            .Select(static duration => duration!.Value.TotalMilliseconds)
            .ToList();
        return values.Count == 0 ? 0 : values.Average();
    }

    private static double MaxDurationMs(IEnumerable<TimeSpan?> durations) =>
        durations.Where(static duration => duration is not null)
            .Select(static duration => duration!.Value.TotalMilliseconds)
            .DefaultIfEmpty(0)
            .Max();

    private static Regex WildcardToRegex(string pattern)
    {
        lock (WildcardRegexLock)
        {
            if (WildcardRegexCache.TryGetValue(pattern, out var cached))
            {
                return cached;
            }

            var builder = new StringBuilder(pattern.Length * 2);
            builder.Append('^');
            foreach (var ch in pattern)
            {
                builder.Append(ch switch
                {
                    '*' => ".*",
                    '?' => ".",
                    _ => Regex.Escape(ch.ToString()),
                });
            }

            builder.Append('$');
            cached = new Regex(builder.ToString(), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
            WildcardRegexCache[pattern] = cached;
            return cached;
        }
    }

    [GeneratedRegex("\\[(.*?)\\]", RegexOptions.CultureInvariant)]
    private static partial Regex TagPairRegex();
}
