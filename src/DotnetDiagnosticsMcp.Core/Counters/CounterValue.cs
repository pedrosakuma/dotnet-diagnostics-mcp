namespace DotnetDiagnosticsMcp.Core.Counters;

/// <summary>Kind of <see cref="CounterValue"/>: a mean over the interval, or a sum of increments.</summary>
public enum CounterKind
{
    Mean,
    Sum,
}

/// <summary>A single counter sample reported by an EventCounters EventSource.</summary>
public sealed record CounterValue(
    string Provider,
    string Name,
    string DisplayName,
    double Value,
    CounterKind Kind,
    string? Unit = null);

/// <summary>Percentile snapshot reconstituted from a Meter histogram payload.</summary>
public sealed record HistogramSnapshot(
    long Count,
    double Sum,
    double P50,
    double P95,
    double P99);

/// <summary>A single Meter time series emitted via System.Diagnostics.Metrics.</summary>
public sealed record MeterInstrumentValue(
    string Meter,
    string Instrument,
    string? Unit,
    string Kind,
    IReadOnlyDictionary<string, string?> Tags,
    double? LastValue,
    double? Rate,
    HistogramSnapshot? Histogram);

/// <summary>Final aggregation returned by <see cref="ICounterCollector"/>.</summary>
public sealed record CounterSnapshot(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyList<CounterValue> Counters,
    IReadOnlyList<MeterInstrumentValue> Meters,
    IReadOnlyList<string> Notes);
