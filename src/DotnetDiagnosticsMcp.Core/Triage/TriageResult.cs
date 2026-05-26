namespace DotnetDiagnosticsMcp.Core.Triage;

/// <summary>
/// Phase 12 triage result — IoT-style diagnostic classification that does the heavy lifting
/// server-side and returns actionable leads. The LLM just follows the first hint.
/// </summary>
/// <param name="Verdict">Primary classification: cpu-bound, gc-pressure, threadpool-starvation, lock-contention, io-bound, healthy.</param>
/// <param name="Severity">Overall severity: critical, degraded, or healthy.</param>
/// <param name="Evidence">Key counter values that drove the classification.</param>
/// <param name="SecondaryVerdicts">Additional classifications if multiple issues detected (e.g., gc-pressure + allocation-heavy).</param>
public sealed record TriageResult(
    string Verdict,
    TriageSeverity Severity,
    TriageEvidence Evidence,
    IReadOnlyList<string>? SecondaryVerdicts = null);

/// <summary>Severity levels for triage classification.</summary>
public enum TriageSeverity
{
    /// <summary>All metrics within normal bounds.</summary>
    Healthy,

    /// <summary>Some metrics elevated but not critical.</summary>
    Degraded,

    /// <summary>Metrics indicate significant performance impact.</summary>
    Critical
}

/// <summary>
/// Key counter evidence that drove the triage classification. Provides just enough context
/// for the LLM to understand why a verdict was reached without full counter dumps.
/// </summary>
/// <param name="CpuUsage">CPU usage percentage (0-100).</param>
/// <param name="TimeInGc">Percentage of time spent in GC.</param>
/// <param name="ThreadPoolQueueLength">Number of work items queued for ThreadPool.</param>
/// <param name="MonitorLockContentionCount">Lock contentions per interval.</param>
/// <param name="AllocRate">Allocation rate in bytes/second.</param>
/// <param name="Gen2GcCount">Gen2 GC count in last interval.</param>
/// <param name="GcHeapSize">Current GC heap size in bytes.</param>
/// <param name="ExceptionCount">Exceptions per interval.</param>
/// <param name="RequestDurationP95">HTTP request duration p95 in seconds (null if not ASP.NET Core).</param>
public sealed record TriageEvidence(
    double? CpuUsage,
    double? TimeInGc,
    double? ThreadPoolQueueLength,
    double? MonitorLockContentionCount,
    double? AllocRate,
    double? Gen2GcCount,
    double? GcHeapSize,
    double? ExceptionCount,
    double? RequestDurationP95);
