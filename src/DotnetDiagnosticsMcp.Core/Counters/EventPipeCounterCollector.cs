using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Globalization;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.Counters;

/// <summary>
/// Default <see cref="ICounterCollector"/> backed by an EventPipe session subscribed to
/// EventCounter providers (defaults to <c>System.Runtime</c> and <c>Microsoft.AspNetCore.Hosting</c>).
/// </summary>
public sealed class EventPipeCounterCollector : ICounterCollector
{
    private const string MetricsProviderName = "System.Diagnostics.Metrics";
    private const long MetricsKeywords = 0x2;

    private static readonly IReadOnlyList<string> DefaultProviders =
    [
        "System.Runtime",
        "Microsoft.AspNetCore.Hosting",
        "Microsoft-AspNetCore-Server-Kestrel",
    ];

    private readonly ILogger<EventPipeCounterCollector> _logger;

    public EventPipeCounterCollector(ILogger<EventPipeCounterCollector>? logger = null)
    {
        _logger = logger ?? NullLogger<EventPipeCounterCollector>.Instance;
    }

    public async Task<CounterSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        IReadOnlyList<string>? providers = null,
        IReadOnlyList<string>? meters = null,
        int intervalSeconds = 1,
        int maxInstrumentTimeSeries = 1000,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        if (intervalSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalSeconds), "Interval must be >= 1 second.");
        }

        if (maxInstrumentTimeSeries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxInstrumentTimeSeries), "Max instrument time series must be >= 1.");
        }

        var providerNames = providers ?? DefaultProviders;
        var eventPipeProviders = new List<EventPipeProvider>();

        if (providerNames.Count > 0)
        {
            var counterArguments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["EventCounterIntervalSec"] = intervalSeconds.ToString(CultureInfo.InvariantCulture),
            };

            eventPipeProviders.AddRange(providerNames
                .Select(name => new EventPipeProvider(name, EventLevel.Verbose, (long)EventKeywords.All, counterArguments)));
        }

        string? metricsSessionId = null;
        if (meters is { Count: > 0 })
        {
            metricsSessionId = Guid.NewGuid().ToString();
            var metricsArguments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SessionId"] = metricsSessionId,
                ["Metrics"] = string.Join(',', meters),
                ["RefreshInterval"] = intervalSeconds.ToString(CultureInfo.InvariantCulture),
                ["MaxTimeSeries"] = maxInstrumentTimeSeries.ToString(CultureInfo.InvariantCulture),
                ["MaxHistograms"] = maxInstrumentTimeSeries.ToString(CultureInfo.InvariantCulture),
            };

            eventPipeProviders.Add(new EventPipeProvider(
                MetricsProviderName,
                EventLevel.Informational,
                MetricsKeywords,
                metricsArguments));
        }

        if (eventPipeProviders.Count == 0)
        {
            var counterArguments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["EventCounterIntervalSec"] = intervalSeconds.ToString(CultureInfo.InvariantCulture),
            };

            eventPipeProviders.AddRange(DefaultProviders
                .Select(name => new EventPipeProvider(name, EventLevel.Verbose, (long)EventKeywords.All, counterArguments)));
        }

        var client = new DiagnosticsClient(processId);
        var session = await client
            .StartEventPipeSessionAsync(eventPipeProviders, requestRundown: false, circularBufferMB: 128, cancellationToken)
            .ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        var latestCounters = new ConcurrentDictionary<string, CounterValue>(StringComparer.Ordinal);
        var instrumentMetadata = new ConcurrentDictionary<int, InstrumentMetadata>();
        var latestMeters = new ConcurrentDictionary<string, MeterInstrumentValue>(StringComparer.Ordinal);
        var notes = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        var processingTask = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                source.Dynamic.All += traceEvent =>
                {
                    if (string.Equals(traceEvent.ProviderName, MetricsProviderName, StringComparison.Ordinal))
                    {
                        HandleMetricsEvent(
                            traceEvent,
                            metricsSessionId,
                            maxInstrumentTimeSeries,
                            instrumentMetadata,
                            latestMeters,
                            notes);
                        return;
                    }

                    if (!string.Equals(traceEvent.EventName, "EventCounters", StringComparison.Ordinal))
                    {
                        return;
                    }

                    var payload = ExtractCounterPayload(traceEvent);
                    if (payload is null)
                    {
                        return;
                    }

                    var key = $"{traceEvent.ProviderName}/{payload.Name}";
                    latestCounters[key] = payload with { Provider = traceEvent.ProviderName };
                };

                source.Process();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EventPipe source ended for pid {Pid}.", processId);
            }
        }, cancellationToken);

        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // best-effort stop
            }

            try
            {
                await processingTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // already logged
            }

            session.Dispose();
        }

        var counters = latestCounters.Values
            .OrderBy(c => c.Provider, StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
        var meterValues = latestMeters.Values
            .OrderBy(v => v.Meter, StringComparer.Ordinal)
            .ThenBy(v => v.Instrument, StringComparer.Ordinal)
            .ThenBy(v => FormatTags(v.Tags), StringComparer.Ordinal)
            .ToList();
        var orderedNotes = notes.Keys
            .OrderBy(note => note, StringComparer.Ordinal)
            .ToList();

        return new CounterSnapshot(processId, startedAt, duration, counters, meterValues, orderedNotes);
    }

    private static void HandleMetricsEvent(
        TraceEvent traceEvent,
        string? metricsSessionId,
        int maxInstrumentTimeSeries,
        ConcurrentDictionary<int, InstrumentMetadata> instrumentMetadata,
        ConcurrentDictionary<string, MeterInstrumentValue> latestMeters,
        ConcurrentDictionary<string, byte> notes)
    {
        if (metricsSessionId is null || !BelongsToMetricsSession(traceEvent, metricsSessionId))
        {
            return;
        }

        switch (traceEvent.EventName)
        {
            case "BeginInstrumentReporting":
                if (TryExtractInstrumentMetadata(traceEvent, out var metadata))
                {
                    instrumentMetadata[metadata.InstrumentId] = metadata;
                }
                break;

            case "CounterRateValuePublished":
            case "UpDownCounterRateValuePublished":
            case "GaugeValuePublished":
            case "HistogramValuePublished":
                if (TryExtractMeterValue(traceEvent, instrumentMetadata, out var key, out var meterValue))
                {
                    if (!latestMeters.ContainsKey(key) && latestMeters.Count >= maxInstrumentTimeSeries)
                    {
                        notes.TryAdd($"TimeSeriesLimitReached: capped at {maxInstrumentTimeSeries} series.", 0);
                        return;
                    }

                    latestMeters[key] = meterValue;
                }
                break;

            case "TimeSeriesLimitReached":
                notes.TryAdd($"TimeSeriesLimitReached: capped at {maxInstrumentTimeSeries} series.", 0);
                break;

            case "HistogramLimitReached":
                notes.TryAdd($"HistogramLimitReached: capped at {maxInstrumentTimeSeries} histograms.", 0);
                break;

            case "Error":
                var errorMessage = PayloadString(traceEvent, 1);
                if (!string.IsNullOrWhiteSpace(errorMessage))
                {
                    notes.TryAdd($"Error: {errorMessage}", 0);
                }
                break;

            case "ObservableInstrumentCallbackError":
                var callbackError = PayloadString(traceEvent, 1);
                if (!string.IsNullOrWhiteSpace(callbackError))
                {
                    notes.TryAdd($"ObservableInstrumentCallbackError: {callbackError}", 0);
                }
                break;
        }
    }

    private static bool BelongsToMetricsSession(TraceEvent traceEvent, string metricsSessionId)
        => string.Equals(PayloadString(traceEvent, 0), metricsSessionId, StringComparison.Ordinal);

    private static bool TryExtractInstrumentMetadata(TraceEvent traceEvent, out InstrumentMetadata metadata)
    {
        var instrumentId = PayloadInt32(traceEvent, 10);
        if (instrumentId <= 0)
        {
            metadata = default!;
            return false;
        }

        metadata = new InstrumentMetadata(
            instrumentId,
            PayloadString(traceEvent, 1),
            PayloadString(traceEvent, 3),
            NullIfEmpty(PayloadString(traceEvent, 5)),
            PayloadString(traceEvent, 4));
        return true;
    }

    private static bool TryExtractMeterValue(
        TraceEvent traceEvent,
        ConcurrentDictionary<int, InstrumentMetadata> instrumentMetadata,
        out string key,
        out MeterInstrumentValue meterValue)
    {
        var meter = PayloadString(traceEvent, 1);
        var instrument = PayloadString(traceEvent, 3);
        if (string.IsNullOrWhiteSpace(meter) || string.IsNullOrWhiteSpace(instrument))
        {
            key = string.Empty;
            meterValue = default!;
            return false;
        }

        var instrumentId = traceEvent.EventName switch
        {
            "GaugeValuePublished" => PayloadInt32(traceEvent, 7),
            "HistogramValuePublished" => PayloadInt32(traceEvent, 9),
            _ => PayloadInt32(traceEvent, 8),
        };

        instrumentMetadata.TryGetValue(instrumentId, out var metadata);
        var tagsText = PayloadString(traceEvent, 5);
        var tags = ParseTags(tagsText);
        var kind = metadata is { Kind.Length: > 0 }
            ? metadata.Kind
            : traceEvent.EventName switch
            {
                "GaugeValuePublished" => "Gauge",
                "HistogramValuePublished" => "Histogram",
                "UpDownCounterRateValuePublished" => "UpDownCounter",
                _ => "Counter",
            };
        var unit = metadata?.Unit ?? NullIfEmpty(PayloadString(traceEvent, 4));
        var effectiveMeter = metadata is { Meter.Length: > 0 } ? metadata.Meter : meter;
        var effectiveInstrument = metadata is { Instrument.Length: > 0 } ? metadata.Instrument : instrument;

        key = $"{effectiveMeter}\u001f{effectiveInstrument}\u001f{tagsText}";
        meterValue = traceEvent.EventName switch
        {
            "GaugeValuePublished" => new MeterInstrumentValue(
                effectiveMeter,
                effectiveInstrument,
                unit,
                kind,
                tags,
                LastValue: ParseNullableDouble(PayloadString(traceEvent, 6)),
                Rate: null,
                Histogram: null),
            "HistogramValuePublished" => new MeterInstrumentValue(
                effectiveMeter,
                effectiveInstrument,
                unit,
                kind,
                tags,
                LastValue: null,
                Rate: null,
                Histogram: ParseHistogram(traceEvent)),
            _ => new MeterInstrumentValue(
                effectiveMeter,
                effectiveInstrument,
                unit,
                kind,
                tags,
                LastValue: ParseNullableDouble(PayloadString(traceEvent, 7)),
                Rate: ParseNullableDouble(PayloadString(traceEvent, 6)),
                Histogram: null),
        };
        return true;
    }

    private static HistogramSnapshot? ParseHistogram(TraceEvent traceEvent)
    {
        var quantilesText = PayloadString(traceEvent, 6);
        var quantiles = ParseQuantiles(quantilesText);
        if (!quantiles.TryGetValue(0.5, out var p50) || !quantiles.TryGetValue(0.95, out var p95) || !quantiles.TryGetValue(0.99, out var p99))
        {
            return null;
        }

        return new HistogramSnapshot(
            Count: PayloadInt32(traceEvent, 7),
            Sum: PayloadDouble(traceEvent, 8),
            P50: p50,
            P95: p95,
            P99: p99);
    }

    private static Dictionary<double, double> ParseQuantiles(string quantilesText)
    {
        var quantiles = new Dictionary<double, double>();
        if (string.IsNullOrWhiteSpace(quantilesText))
        {
            return quantiles;
        }

        foreach (var pair in quantilesText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0 || separator == pair.Length - 1)
            {
                continue;
            }

            if (double.TryParse(pair[..separator], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var quantile) &&
                double.TryParse(pair[(separator + 1)..], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value))
            {
                quantiles[quantile] = value;
            }
        }

        return quantiles;
    }

    private static Dictionary<string, string?> ParseTags(string tagsText)
    {
        if (string.IsNullOrWhiteSpace(tagsText))
        {
            return new Dictionary<string, string?>();
        }

        var tags = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in tagsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator < 0)
            {
                tags[pair] = null;
                continue;
            }

            var key = pair[..separator];
            var value = pair[(separator + 1)..];
            tags[key] = string.Equals(value, "null", StringComparison.Ordinal) ? null : value;
        }

        return tags;
    }

    private static string FormatTags(IReadOnlyDictionary<string, string?> tags) => tags.Count == 0
        ? string.Empty
        : string.Join(",", tags.OrderBy(kvp => kvp.Key, StringComparer.Ordinal).Select(kvp => $"{kvp.Key}={kvp.Value ?? "null"}"));

    private static CounterValue? ExtractCounterPayload(TraceEvent traceEvent)
    {
        if (traceEvent.PayloadValue(0) is not IDictionary<string, object> outer)
        {
            return null;
        }

        if (!outer.TryGetValue("Payload", out var inner) || inner is not IDictionary<string, object> data)
        {
            return null;
        }

        var name = AsString(data, "Name");
        var display = AsString(data, "DisplayName");
        var unit = data.TryGetValue("DisplayUnits", out var u) ? u as string : null;

        double value;
        CounterKind kind;
        if (data.TryGetValue("Mean", out var meanObj))
        {
            value = ToDouble(meanObj);
            kind = CounterKind.Mean;
        }
        else if (data.TryGetValue("Increment", out var incObj))
        {
            value = ToDouble(incObj);
            kind = CounterKind.Sum;
        }
        else
        {
            return null;
        }

        return new CounterValue(
            Provider: traceEvent.ProviderName,
            Name: name,
            DisplayName: string.IsNullOrEmpty(display) ? name : display,
            Value: value,
            Kind: kind,
            Unit: string.IsNullOrEmpty(unit) ? null : unit);
    }

    private static string AsString(IDictionary<string, object> data, string key)
        => data.TryGetValue(key, out var v) && v is string s ? s : string.Empty;

    private static string PayloadString(TraceEvent traceEvent, int index)
        => traceEvent.PayloadValue(index)?.ToString() ?? string.Empty;

    private static int PayloadInt32(TraceEvent traceEvent, int index)
        => traceEvent.PayloadValue(index) switch
        {
            int i => i,
            long l => checked((int)l),
            null => 0,
            var value => Convert.ToInt32(value, CultureInfo.InvariantCulture),
        };

    private static double PayloadDouble(TraceEvent traceEvent, int index)
        => traceEvent.PayloadValue(index) switch
        {
            double d => d,
            float f => f,
            long l => l,
            int i => i,
            null => 0,
            var value => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        };

    private static double? ParseNullableDouble(string value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : double.Parse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);

    private static string? NullIfEmpty(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static double ToDouble(object value) => value switch
    {
        double d => d,
        float f => f,
        long l => l,
        int i => i,
        _ => Convert.ToDouble(value, CultureInfo.InvariantCulture),
    };

    private sealed record InstrumentMetadata(
        int InstrumentId,
        string Meter,
        string Instrument,
        string? Unit,
        string Kind);
}
