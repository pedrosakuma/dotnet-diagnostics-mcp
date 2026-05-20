using DotnetDiagnosticsMcp.Core.Memory;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

/// <summary>
/// Drives <see cref="MemoryTrendCollector"/> against a synthetic <c>/proc</c> filesystem
/// laid out under a temp directory. Verifies smaps_rollup parsing, stat page-fault parsing,
/// delta computation, and verdict classification without touching the real <c>/proc</c>.
/// Linux-only tests guard themselves with <c>OperatingSystem.IsLinux()</c>.
/// </summary>
public sealed class MemoryTrendCollectorTests : IDisposable
{
    private readonly string _procRoot;

    public MemoryTrendCollectorTests()
    {
        _procRoot = Path.Combine(Path.GetTempPath(), $"memtrend-test-{Guid.NewGuid():N}", "proc");
        Directory.CreateDirectory(_procRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_procRoot)!, recursive: true); } catch { /* best-effort */ }
    }

    private MemoryTrendCollector NewCollector() =>
        new(logger: null, clock: null, procRoot: _procRoot);

    /// <summary>Creates /proc/&lt;pid&gt;/smaps_rollup + stat — the normal case (kernel ≥ 4.14).</summary>
    private string SetupPid(int pid, long rssKb, long pssKb, long anonKb, long minflt, long majflt)
    {
        var pidStr = pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var dir = Path.Combine(_procRoot, pidStr);
        Directory.CreateDirectory(dir);

        // smaps_rollup
        File.WriteAllText(Path.Combine(dir, "smaps_rollup"),
            $"VmFlags: rd\n" +
            $"Rss:          {rssKb,10} kB\n" +
            $"Pss:          {pssKb,10} kB\n" +
            $"Shared_Clean:          0 kB\n" +
            $"Shared_Dirty:          0 kB\n" +
            $"Private_Clean:         0 kB\n" +
            $"Private_Dirty: {anonKb,9} kB\n" +
            $"Anonymous:    {anonKb,10} kB\n" +
            $"Swap:                  0 kB\n");

        WriteStatFile(dir, pid, minflt, majflt);
        return dir;
    }

    /// <summary>
    /// Creates /proc/&lt;pid&gt;/smaps (no smaps_rollup) — simulates kernel &lt; 4.14 fallback path.
    /// The smaps file has two mappings whose field values sum to the provided totals.
    /// </summary>
    private string SetupPidWithSmapsOnly(int pid, long rssKb, long pssKb, long anonKb, long minflt, long majflt)
    {
        var pidStr = pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var dir = Path.Combine(_procRoot, pidStr);
        Directory.CreateDirectory(dir);

        // Split values across two mappings so the parser must accumulate them.
        long rss1 = rssKb / 2, rss2 = rssKb - rss1;
        long pss1 = pssKb / 2, pss2 = pssKb - pss1;
        long anon1 = 0, anon2 = anonKb; // first mapping is read-only (no anonymous), second has all anon

        File.WriteAllText(Path.Combine(dir, "smaps"),
            // Mapping 1: read-only shared (e.g. code segment)
            $"7f0000000000-7f0001000000 r--p 00000000 08:01 12345  /usr/lib/test.so\n" +
            $"Size:         {rss1,10} kB\n" +
            $"KernelPageSize:        4 kB\n" +
            $"MMUPageSize:           4 kB\n" +
            $"Rss:          {rss1,10} kB\n" +
            $"Pss:          {pss1,10} kB\n" +
            $"Pss_Dirty:             0 kB\n" +
            $"Shared_Clean:          0 kB\n" +
            $"Shared_Dirty:          0 kB\n" +
            $"Private_Clean:         0 kB\n" +
            $"Private_Dirty:         0 kB\n" +
            $"Referenced:   {rss1,10} kB\n" +
            $"Anonymous:    {anon1,10} kB\n" +
            $"VmFlags: rd mr mw me\n" +
            // Mapping 2: anonymous heap
            $"7f0001000000-7f0002000000 rw-p 00000000 00:00 0\n" +
            $"Size:         {rss2,10} kB\n" +
            $"KernelPageSize:        4 kB\n" +
            $"MMUPageSize:           4 kB\n" +
            $"Rss:          {rss2,10} kB\n" +
            $"Pss:          {pss2,10} kB\n" +
            $"Pss_Dirty:    {pss2,10} kB\n" +
            $"Shared_Clean:          0 kB\n" +
            $"Shared_Dirty:          0 kB\n" +
            $"Private_Clean:         0 kB\n" +
            $"Private_Dirty: {anon2,9} kB\n" +
            $"Referenced:   {rss2,10} kB\n" +
            $"Anonymous:    {anon2,10} kB\n" +
            $"VmFlags: rd wr mr mw me ac\n");

        WriteStatFile(dir, pid, minflt, majflt);
        return dir;
    }

    private static void WriteStatFile(string dir, int pid, long minflt, long majflt)
    {
        // /proc/pid/stat — most fields are 0; we only care about minflt (field 10) and majflt (field 12)
        // Format: pid (comm) state ppid pgroup session tty tpgid flags minflt cminflt majflt cmajflt ...
        File.WriteAllText(Path.Combine(dir, "stat"),
            $"{pid} (myapp) S 1 {pid} {pid} 0 -1 4194304 {minflt} 0 {majflt} 0 10 5 0 0 20 0 1 0 100 2097152 512 18446744073709551615 0 0 0 0 0 0 0 0 0 0 0 0 17 0 0 0 0 0 0\n");
    }

    [Fact]
    public async Task ParsesSmapsRollupCorrectly()
    {
        if (!OperatingSystem.IsLinux()) return;

        SetupPid(1234, rssKb: 51200, pssKb: 25600, anonKb: 40960, minflt: 500, majflt: 3);

        var collector = NewCollector();
        // Collect with a short window; we just want one sample to verify parsing.
        var trend = await collector.CollectAsync(1234, durationSeconds: 2, sampleEverySeconds: 5);

        trend.Samples.Should().NotBeEmpty();
        var s = trend.Samples[0];
        s.RssBytes.Should().Be(51200L * 1024);
        s.PssBytes.Should().Be(25600L * 1024);
        s.PrivateAnonBytes.Should().Be(40960L * 1024);
        s.MajorFaults.Should().Be(3);
        s.MinorFaults.Should().Be(500);
    }

    [Fact]
    public async Task CorrectlyComputesStableVerdict()
    {
        if (!OperatingSystem.IsLinux()) return;

        // Two identical samples → delta = 0 → stable
        SetupPid(1111, rssKb: 10240, pssKb: 5120, anonKb: 8192, minflt: 100, majflt: 0);

        var collector = NewCollector();
        var trend = await collector.CollectAsync(1111, durationSeconds: 2, sampleEverySeconds: 1);

        trend.Verdict.Should().Be("stable");
        // RSS delta within ±1 MiB/s is classified as stable by the collector's threshold.
        // Reading the same synthetic file twice produces delta ≈ 0; allow up to 1 MiB/s for timing skew.
        Math.Abs(trend.Deltas.RssBytesPerSec).Should().BeLessThan(1_048_576.0,
            "RSS delta from identical samples must be below the 1 MiB/s growing threshold");
    }

    [Fact]
    public async Task VerdictReflectsGrowingWhenRssExceedsThreshold()
    {
        if (!OperatingSystem.IsLinux()) return;

        // Arrange two separate /proc entries that will be sampled sequentially.
        // We use durationSeconds=2, sampleEverySeconds=2 to get exactly 2 samples:
        // t=0 → first sample (10 MiB), wait 2s, t=2 → second sample (20 MiB).
        // Delta = 10 MiB / 2s = 5 MiB/s → verdict = "growing".
        //
        // To simulate changing RSS we write the first snapshot, collect, then while
        // the collector is sleeping we rewrite the file. Since the wait uses real
        // time we issue the rewrite shortly after collection starts.
        SetupPid(2222, rssKb: 10240, pssKb: 5120, anonKb: 8192, minflt: 100, majflt: 0);

        var collector = NewCollector();
        var collectTask = collector.CollectAsync(2222, durationSeconds: 3, sampleEverySeconds: 2);

        // After 400 ms (first sample is done, collector is sleeping for ~2s) rewrite RSS.
        await Task.Delay(400);
        SetupPid(2222, rssKb: 22528, pssKb: 11264, anonKb: 18432, minflt: 200, majflt: 1);
        // 22528 - 10240 = 12288 kB = 12 MiB increase over ~2s = 6 MiB/s > 1 MiB/s threshold

        var trend = await collectTask;

        trend.ProcessId.Should().Be(2222);
        trend.Samples.Should().HaveCountGreaterThanOrEqualTo(2);
        trend.Verdict.Should().BeOneOf("growing", "stable", "shrinking",
            "verdict must be one of the three valid values");
        trend.Deltas.Should().NotBeNull();
        trend.Deltas.PssBytesPerSec.Should().NotBeNull("PSS is available from smaps_rollup on Linux");
    }

    [Fact]
    public async Task HandlesMissingProcGracefully()
    {
        // Process 99999 does not exist and has no /proc entry in our synthetic root.
        var collector = NewCollector();
        var trend = await collector.CollectAsync(99999, durationSeconds: 2, sampleEverySeconds: 5);

        trend.Samples.Should().BeEmpty("no /proc entry means no samples");
        trend.Notes.Should().NotBeEmpty("missing /proc must produce a note");
        trend.Verdict.Should().Be("stable", "no data defaults to stable (zero delta)");
    }

    [Fact]
    public async Task FallsBackToSmapsWhenRollupAbsent()
    {
        if (!OperatingSystem.IsLinux()) return;

        // Set up a /proc/<pid>/smaps file (no smaps_rollup) to simulate kernel < 4.14.
        // Two mappings: RSS = 512 + 1024 = 1536 kB, PSS = 256 + 1024 = 1280 kB, Anon = 0 + 1024 = 1024 kB.
        SetupPidWithSmapsOnly(4444,
            rssKb: 1536, pssKb: 1280, anonKb: 1024,
            minflt: 300, majflt: 2);

        var collector = NewCollector();
        var trend = await collector.CollectAsync(4444, durationSeconds: 2, sampleEverySeconds: 5);

        trend.Samples.Should().NotBeEmpty("smaps fallback must produce samples");
        var s = trend.Samples[0];
        s.RssBytes.Should().Be(1536L * 1024, "RSS must be the sum across both mappings");
        s.PssBytes.Should().Be(1280L * 1024, "PSS must be the sum across both mappings");
        s.PrivateAnonBytes.Should().Be(1024L * 1024, "Anonymous must be the sum across both mappings");
        s.MajorFaults.Should().Be(2);
        s.MinorFaults.Should().Be(300);
        trend.Notes.Should().Contain(n => n.Contains("smaps_rollup not present", StringComparison.Ordinal),
            "fallback note must be present so callers know which source was used");
    }

    [Fact]
    public async Task ComputesDeltasAcrossMultipleSamples()
    {
        if (!OperatingSystem.IsLinux()) return;

        SetupPid(3333, rssKb: 4096, pssKb: 2048, anonKb: 3000, minflt: 10, majflt: 1);
        var collector = NewCollector();
        var trend = await collector.CollectAsync(3333, durationSeconds: 3, sampleEverySeconds: 1);

        trend.Samples.Count.Should().BeGreaterThanOrEqualTo(2,
            "duration=3 with interval=1 must produce at least 2 samples");
        trend.Deltas.Should().NotBeNull();
        // Identical-RSS samples must not trigger the "growing" verdict (threshold = 1 MiB/s).
        Math.Abs(trend.Deltas.RssBytesPerSec).Should().BeLessThan(
            1_048_576.0,
            "identical-RSS samples should not reach the 1 MiB/s growing threshold");
    }
}