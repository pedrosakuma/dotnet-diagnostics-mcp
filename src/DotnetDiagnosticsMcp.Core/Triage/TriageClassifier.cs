using DotnetDiagnosticsMcp.Core.Counters;

namespace DotnetDiagnosticsMcp.Core.Triage;

/// <summary>
/// Phase 12 triage classifier — extracts the auto-hint logic into a reusable classification engine.
/// Produces a <see cref="TriageResult"/> with verdict, severity, and evidence from counter snapshots.
/// </summary>
public static class TriageClassifier
{
    /// <summary>Well-known verdict for CPU-bound workloads (cpu-usage &gt; 70%).</summary>
    public const string CpuBound = "cpu-bound";

    /// <summary>Well-known verdict for GC pressure (time-in-gc &gt; 15%).</summary>
    public const string GcPressure = "gc-pressure";

    /// <summary>Well-known verdict for ThreadPool starvation (queue-length &gt; 50).</summary>
    public const string ThreadPoolStarvation = "threadpool-starvation";

    /// <summary>Well-known verdict for lock contention (contention-count &gt; 10).</summary>
    public const string LockContention = "lock-contention";

    /// <summary>Well-known verdict for I/O-bound workloads (low CPU + queue buildup).</summary>
    public const string IoBound = "io-bound";

    /// <summary>Well-known verdict for healthy systems (no triggers fired).</summary>
    public const string Healthy = "healthy";

    // Thresholds (same as auto-hints in DiagnosticTools.SnapshotCounters).
    private const double CpuCriticalThreshold = 90;
    private const double CpuDegradedThreshold = 70;
    private const double TimeInGcCriticalThreshold = 30;
    private const double TimeInGcDegradedThreshold = 15;
    private const double QueueLengthCriticalThreshold = 200;
    private const double QueueLengthDegradedThreshold = 50;
    private const double ContentionDegradedThreshold = 10;
    private const double AllocRateDegradedThreshold = 50_000_000; // 50 MB/s
    private const double IoBoundCpuThreshold = 30;
    private const double IoBoundQueueThreshold = 10;

    /// <summary>
    /// Classifies a counter snapshot into a triage result with verdict, severity, and evidence.
    /// </summary>
    /// <param name="snapshot">The counter snapshot to classify.</param>
    /// <param name="requestDurationP95">Optional HTTP request duration p95 from Meters.</param>
    /// <returns>A <see cref="TriageResult"/> with the primary verdict and any secondary findings.</returns>
    public static TriageResult Classify(CounterSnapshot snapshot, double? requestDurationP95 = null)
    {
        // Extract key counters.
        var cpu = GetCounter(snapshot, "cpu-usage");
        var timeInGc = GetCounter(snapshot, "time-in-gc");
        var queueLength = GetCounter(snapshot, "threadpool-queue-length");
        var contention = GetCounter(snapshot, "monitor-lock-contention-count");
        var allocRate = GetCounter(snapshot, "alloc-rate");
        var gen2Count = GetCounter(snapshot, "gen-2-gc-count");
        var heapSize = GetCounter(snapshot, "gc-heap-size");
        var exceptionCount = GetCounter(snapshot, "exception-count");

        // Build evidence.
        var evidence = new TriageEvidence(
            CpuUsage: cpu,
            TimeInGc: timeInGc,
            ThreadPoolQueueLength: queueLength,
            MonitorLockContentionCount: contention,
            AllocRate: allocRate,
            Gen2GcCount: gen2Count,
            GcHeapSize: heapSize,
            ExceptionCount: exceptionCount,
            RequestDurationP95: requestDurationP95);

        // Classify by priority order (same as auto-hints).
        var verdicts = new List<string>();
        var severity = TriageSeverity.Healthy;

        // CPU-bound check.
        if (cpu >= CpuDegradedThreshold)
        {
            verdicts.Add(CpuBound);
            severity = cpu >= CpuCriticalThreshold ? TriageSeverity.Critical : TriageSeverity.Degraded;
        }

        // GC pressure check.
        if (timeInGc >= TimeInGcDegradedThreshold)
        {
            verdicts.Add(GcPressure);
            var gcSeverity = timeInGc >= TimeInGcCriticalThreshold ? TriageSeverity.Critical : TriageSeverity.Degraded;
            severity = (TriageSeverity)Math.Max((int)severity, (int)gcSeverity);
        }

        // ThreadPool starvation check.
        if (queueLength >= QueueLengthDegradedThreshold)
        {
            verdicts.Add(ThreadPoolStarvation);
            var tpSeverity = queueLength >= QueueLengthCriticalThreshold ? TriageSeverity.Critical : TriageSeverity.Degraded;
            severity = (TriageSeverity)Math.Max((int)severity, (int)tpSeverity);
        }

        // Lock contention check.
        if (contention >= ContentionDegradedThreshold)
        {
            verdicts.Add(LockContention);
            severity = (TriageSeverity)Math.Max((int)severity, (int)TriageSeverity.Degraded);
        }

        // I/O-bound check (low CPU + queue buildup).
        if (cpu < IoBoundCpuThreshold && queueLength >= IoBoundQueueThreshold)
        {
            verdicts.Add(IoBound);
            severity = (TriageSeverity)Math.Max((int)severity, (int)TriageSeverity.Degraded);
        }

        // Determine primary verdict and secondaries.
        if (verdicts.Count == 0)
        {
            return new TriageResult(Healthy, TriageSeverity.Healthy, evidence);
        }

        var primary = verdicts[0];
        var secondary = verdicts.Count > 1 ? verdicts.Skip(1).ToList() : null;

        return new TriageResult(primary, severity, evidence, secondary);
    }

    private static double? GetCounter(CounterSnapshot snapshot, string name)
    {
        var counter = snapshot.Counters.FirstOrDefault(
            c => c.Provider == "System.Runtime" && c.Name == name);
        return counter?.Value;
    }
}
