namespace DotnetDiagnosticsMcp.Core.Memory;

/// <summary>
/// A single memory snapshot taken at a point in time during a trend-collection window.
/// All byte fields come from the OS — no EventPipe, no runtime attach required.
/// </summary>
/// <param name="Timestamp">UTC instant of the snapshot.</param>
/// <param name="RssBytes">Resident Set Size in bytes (physical RAM the process is currently using).
/// Linux: <c>Rss</c> from <c>/proc/&lt;pid&gt;/smaps_rollup</c>.
/// Windows: <c>WorkingSetSize</c> from <c>PROCESS_MEMORY_COUNTERS_EX</c>.</param>
/// <param name="PssBytes">Proportional Set Size in bytes (shared pages charged proportionally to each user).
/// Linux only (<c>Pss</c> from smaps_rollup). Null on Windows — no equivalent metric.</param>
/// <param name="PrivateAnonBytes">Private anonymous (heap/stack/mmap-anonymous) bytes.
/// Linux: <c>Anonymous</c> from smaps_rollup.
/// Windows: <c>PrivateUsage</c> (private committed bytes) from <c>PROCESS_MEMORY_COUNTERS_EX</c>.</param>
/// <param name="HeapRegionBytes">Bytes attributed to the process's main heap segment
/// (<c>[heap]</c> in <c>/proc/&lt;pid&gt;/maps</c>). Null when the information is not
/// available without a full smaps walk (most configurations).</param>
/// <param name="MajorFaults">Cumulative major page faults (required a disk read) since process start.
/// Linux: field 12 of <c>/proc/&lt;pid&gt;/stat</c>.
/// Windows: 0 — Windows does not separate major/minor faults.</param>
/// <param name="MinorFaults">Cumulative minor page faults (served from page cache) since process start.
/// Linux: field 10 of <c>/proc/&lt;pid&gt;/stat</c>.
/// Windows: <c>PageFaultCount</c> from <c>PROCESS_MEMORY_COUNTERS_EX</c> (minor + major combined).</param>
public sealed record MemoryTrendSample(
    DateTimeOffset Timestamp,
    long RssBytes,
    long? PssBytes,
    long? PrivateAnonBytes,
    long? HeapRegionBytes,
    long MajorFaults,
    long MinorFaults);

/// <summary>
/// Per-second rates computed between the first and last sample in the collection window.
/// Negative values indicate the metric is decreasing (memory being released).
/// </summary>
/// <param name="RssBytesPerSec">Rate of change of RSS in bytes per second.</param>
/// <param name="PssBytesPerSec">Rate of change of PSS in bytes per second. Null when PSS is unavailable (Windows).</param>
/// <param name="MajorFaultsPerSec">Rate of major page faults per second.</param>
public sealed record MemoryTrendDeltas(
    double RssBytesPerSec,
    double? PssBytesPerSec,
    double? MajorFaultsPerSec);

/// <summary>
/// Memory growth trend for a process over a configured observation window.
/// Produced by <see cref="IMemoryTrendCollector"/>; no EventPipe or runtime attach is required.
/// </summary>
/// <param name="ProcessId">The observed OS process id.</param>
/// <param name="WindowStart">UTC start of the collection window.</param>
/// <param name="WindowEnd">UTC end of the collection window.</param>
/// <param name="Samples">Ordered list of per-interval snapshots (≥ 2 on success).</param>
/// <param name="Deltas">Per-second rates computed first→last sample.</param>
/// <param name="Verdict">
/// Growth classification:
/// <list type="bullet">
/// <item><term>growing</term><description>RSS is increasing faster than 1 MiB/s.</description></item>
/// <item><term>shrinking</term><description>RSS is decreasing faster than 1 MiB/s.</description></item>
/// <item><term>stable</term><description>RSS change is within ±1 MiB/s.</description></item>
/// </list>
/// </param>
/// <param name="Notes">Informational notes — e.g. unavailable metrics on the current OS.</param>
public sealed record MemoryTrend(
    int ProcessId,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    IReadOnlyList<MemoryTrendSample> Samples,
    MemoryTrendDeltas Deltas,
    string Verdict,
    IReadOnlyList<string> Notes);
