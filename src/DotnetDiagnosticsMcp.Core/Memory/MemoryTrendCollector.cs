using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.Memory;

/// <summary>
/// OS-level memory trend collector.
/// <list type="bullet">
/// <item><term>Linux</term><description>Reads <c>/proc/&lt;pid&gt;/smaps_rollup</c> for Rss/Pss/Anonymous
/// and <c>/proc/&lt;pid&gt;/stat</c> for page-fault counters. Pure file reads — no privileges required.</description></item>
/// <item><term>Windows</term><description>Calls <c>GetProcessMemoryInfo</c> (<c>PROCESS_MEMORY_COUNTERS_EX</c>)
/// for WorkingSetSize, PrivateUsage and PageFaultCount.</description></item>
/// </list>
/// The default constructor uses the real <c>/proc</c> root; tests can pass an alternative root.
/// </summary>
public sealed partial class MemoryTrendCollector : IMemoryTrendCollector
{
    /// <summary>Growth threshold: RSS must change faster than this (bytes/sec) to be non-stable.</summary>
    private const double GrowthThresholdBytesPerSec = 1_048_576; // 1 MiB/s

    private readonly string _procRoot;
    private readonly ILogger<MemoryTrendCollector> _logger;
    private readonly TimeProvider _clock;

    public MemoryTrendCollector(
        ILogger<MemoryTrendCollector>? logger = null,
        TimeProvider? clock = null,
        string procRoot = "/proc")
    {
        _logger = logger ?? NullLogger<MemoryTrendCollector>.Instance;
        _clock = clock ?? TimeProvider.System;
        _procRoot = procRoot;
    }

    /// <inheritdoc/>
    public async Task<MemoryTrend> CollectAsync(
        int processId,
        int durationSeconds,
        int sampleEverySeconds,
        CancellationToken cancellationToken = default)
    {
        var notes = new List<string>();
        var samples = new List<MemoryTrendSample>();
        var windowStart = _clock.GetUtcNow();

        // Number of samples: take one immediately, then one per interval until the window elapses.
        // E.g. duration=10, every=2 → at least samples at t≈0, t≈2, t≈4, t≈6, t≈8.
        // Timing jitter means the actual count may vary slightly; callers should not rely on
        // an exact count — only on at least 2 samples when the window >= 2×interval.
        var intervalSpan = TimeSpan.FromSeconds(sampleEverySeconds);
        var deadline = windowStart.AddSeconds(durationSeconds);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sample = TakeSample(processId, notes);
            if (sample is not null)
            {
                samples.Add(sample);
            }

            var now = _clock.GetUtcNow();
            if (now >= deadline) break;

            var remaining = deadline - now;
            var delay = remaining < intervalSpan ? remaining : intervalSpan;
            if (delay <= TimeSpan.Zero) break;

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        var windowEnd = _clock.GetUtcNow();
        var deltas = ComputeDeltas(samples);
        var verdict = ClassifyVerdict(deltas.RssBytesPerSec);

        return new MemoryTrend(processId, windowStart, windowEnd, samples, deltas, verdict, notes);
    }

    private MemoryTrendSample? TakeSample(int processId, List<string> notes)
    {
        var timestamp = _clock.GetUtcNow();
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return TakeLinuxSample(processId, timestamp, notes);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return TakeWindowsSample(processId, timestamp, notes);
            }

            if (notes.Count == 0)
            {
                notes.Add($"Memory trend collection is not supported on {RuntimeInformation.OSDescription}. Only Linux and Windows are implemented.");
            }
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to take memory snapshot for pid {ProcessId}", processId);
            if (notes.All(n => !n.Contains("snapshot", StringComparison.OrdinalIgnoreCase)))
            {
                notes.Add($"Snapshot failed: {ex.GetType().Name}: {ex.Message}");
            }
            return null;
        }
    }

    // ---- Linux -------------------------------------------------------------------

    private MemoryTrendSample? TakeLinuxSample(int processId, DateTimeOffset timestamp, List<string> notes)
    {
        var pid = processId.ToString(CultureInfo.InvariantCulture);
        var smapsPath = Path.Combine(_procRoot, pid, "smaps_rollup");
        var statPath = Path.Combine(_procRoot, pid, "stat");

        var smaps = ReadSmapsRollup(smapsPath, notes);
        if (smaps is null) return null;

        var (minorFaults, majorFaults) = ReadStatFaults(statPath, notes);

        return new MemoryTrendSample(
            Timestamp: timestamp,
            RssBytes: smaps.Value.RssKb * 1024L,
            PssBytes: smaps.Value.PssKb * 1024L,
            PrivateAnonBytes: smaps.Value.AnonKb * 1024L,
            HeapRegionBytes: null,
            MajorFaults: majorFaults,
            MinorFaults: minorFaults);
    }

    private readonly record struct SmapsSnapshot(long RssKb, long PssKb, long AnonKb);

    private static SmapsSnapshot? ReadSmapsRollup(string path, List<string> notes)
    {
        long rss = 0, pss = 0, anon = 0;
        bool hasRss = false;
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                // Format: "FieldName:    1234 kB"
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0) continue;
                var key = line.AsSpan(0, colon).Trim();
                var rest = line.AsSpan(colon + 1).Trim();
                // Remove " kB" suffix
                var spaceIdx = rest.IndexOf(' ');
                var valueSpan = spaceIdx > 0 ? rest[..spaceIdx] : rest;
                if (!long.TryParse(valueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) continue;

                if (key.SequenceEqual("Rss")) { rss = v; hasRss = true; }
                else if (key.SequenceEqual("Pss")) pss = v;
                else if (key.SequenceEqual("Anonymous")) anon = v;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            notes.Add($"Could not read {path}: {ex.GetType().Name}. Process may have exited or /proc is restricted.");
            return null;
        }

        if (!hasRss) return null;
        return new SmapsSnapshot(rss, pss, anon);
    }

    private static (long MinorFaults, long MajorFaults) ReadStatFaults(string path, List<string> notes)
    {
        try
        {
            var content = File.ReadAllText(path);
            // /proc/<pid>/stat format: "pid (comm) state ppid ... minflt cminflt majflt cmajflt ..."
            // The comm field can contain spaces and parens; find the last ')' to skip it safely.
            var lastParen = content.LastIndexOf(')');
            if (lastParen < 0) return (0, 0);

            var afterComm = content.AsSpan(lastParen + 1);
            // Fields after ')' are space-separated; first is 'state' (offset 0).
            // minflt is at offset 7, majflt at offset 9 (0-based relative to state).
            // We need indices 0–9, so allocate 10 ranges + 2 safety slots = 12 total.
            Span<Range> ranges = stackalloc Range[12];
            var count = afterComm.Split(ranges, ' ', StringSplitOptions.RemoveEmptyEntries);

            long minflt = 0, majflt = 0;
            if (count > 7 && long.TryParse(afterComm[ranges[7]], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v7)) minflt = v7;
            if (count > 9 && long.TryParse(afterComm[ranges[9]], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v9)) majflt = v9;
            return (minflt, majflt);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            notes.Add($"Could not read {path}: {ex.GetType().Name}. Page-fault counters will be 0.");
            return (0, 0);
        }
    }

    // ---- Windows -----------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static MemoryTrendSample? TakeWindowsSample(int processId, DateTimeOffset timestamp, List<string> notes)
    {
        var pmc = new PROCESS_MEMORY_COUNTERS_EX();
        pmc.cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX>();

        IntPtr hProcess = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, (uint)processId);
        if (hProcess == IntPtr.Zero)
        {
            var err = Marshal.GetLastPInvokeError();
            notes.Add($"OpenProcess({processId}) failed with error {err}. Check that the sidecar runs with sufficient privileges.");
            return null;
        }

        try
        {
            if (!GetProcessMemoryInfo(hProcess, ref pmc, pmc.cb))
            {
                var err = Marshal.GetLastPInvokeError();
                notes.Add($"GetProcessMemoryInfo({processId}) failed with error {err}.");
                return null;
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }

        return new MemoryTrendSample(
            Timestamp: timestamp,
            RssBytes: (long)pmc.WorkingSetSize,
            PssBytes: null,
            PrivateAnonBytes: (long)pmc.PrivateUsage,
            HeapRegionBytes: null,
            // Windows PageFaultCount combines minor + major; report as MinorFaults with MajorFaults=0
            // so the delta computation stays consistent.
            MajorFaults: 0,
            MinorFaults: (long)pmc.PageFaultCount);
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [SupportedOSPlatform("windows")]
    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(IntPtr hProcess, ref PROCESS_MEMORY_COUNTERS_EX ppsmemCounters, uint cb);

    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    [SupportedOSPlatform("windows")]
    private struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    // ---- Deltas & verdict --------------------------------------------------------

    private static MemoryTrendDeltas ComputeDeltas(List<MemoryTrendSample> samples)
    {
        if (samples.Count < 2)
        {
            return new MemoryTrendDeltas(0, null, null);
        }

        var first = samples[0];
        var last = samples[^1];
        var elapsed = (last.Timestamp - first.Timestamp).TotalSeconds;
        if (elapsed <= 0)
        {
            return new MemoryTrendDeltas(0, null, null);
        }

        var rssBytesPerSec = (last.RssBytes - first.RssBytes) / elapsed;
        double? pssBytesPerSec = first.PssBytes is not null && last.PssBytes is not null
            ? (last.PssBytes.Value - first.PssBytes.Value) / elapsed
            : null;
        double? majorFaultsPerSec = (last.MajorFaults - first.MajorFaults) / elapsed;

        return new MemoryTrendDeltas(rssBytesPerSec, pssBytesPerSec, majorFaultsPerSec);
    }

    private static string ClassifyVerdict(double rssBytesPerSec)
    {
        return rssBytesPerSec switch
        {
            > GrowthThresholdBytesPerSec => "growing",
            < -GrowthThresholdBytesPerSec => "shrinking",
            _ => "stable",
        };
    }
}
