using System.Globalization;

namespace DotnetDiagnosticsMcp.Core.Investigation;

/// <summary>
/// Default <see cref="IInvestigationPlanner"/>. Deterministic, dependency-free, fully unit-testable.
/// Encodes the playbook tree from troubleshooting-research-2026-05.md (sections G31-G33, P1-P5).
/// </summary>
public sealed class InvestigationPlanner : IInvestigationPlanner
{
    private readonly TimeProvider _clock;
    private readonly Func<string> _idFactory;

    public InvestigationPlanner(TimeProvider? clock = null, Func<string>? idFactory = null)
    {
        _clock = clock ?? TimeProvider.System;
        _idFactory = idFactory ?? (() => $"inv-{Guid.NewGuid():N}");
    }

    public InvestigationPlan Plan(InvestigationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "ProcessId must be a positive OS pid.");
        }

        var constraints = request.Constraints ?? new InvestigationConstraints();
        var mode = ResolveMode(request);
        var (steps, terminals, earlyStops, alternates, comparisons) = BuildPlan(mode, request, constraints);
        var next = steps.First(s => s.Status == StepStatus.Pending);

        return new InvestigationPlan(
            InvestigationId: _idFactory(),
            CreatedAt: _clock.GetUtcNow(),
            Mode: mode,
            ProcessId: request.ProcessId,
            Symptom: request.Symptom,
            Hypothesis: request.Hypothesis,
            Baseline: request.Baseline,
            NextStep: next,
            AllSteps: steps,
            Terminals: terminals,
            EarlyStopConditions: earlyStops,
            AlternateBranches: alternates,
            Constraints: constraints,
            BaselineComparisons: comparisons);
    }

    private static InvestigationMode ResolveMode(InvestigationRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Hypothesis)) return InvestigationMode.Hypothesis;
        if (request.Baseline is not null) return InvestigationMode.Warm;
        return InvestigationMode.Cold;
    }

    private static PlanTuple BuildPlan(InvestigationMode mode, InvestigationRequest request, InvestigationConstraints constraints)
        => mode switch
        {
            InvestigationMode.Hypothesis => BuildHypothesisPlan(request, constraints),
            InvestigationMode.Warm => BuildWarmPlan(request, constraints),
            _ => BuildColdPlan(request, constraints),
        };

    // ContentionKeyword on Microsoft-Windows-DotNETRuntime. Numeric so the JSON schema for
    // collect_event_source (long keywords) accepts the value verbatim from the plan.
    private const long ContentionKeyword = 0x4000L;

    // ───────────────────────────── COLD ─────────────────────────────

    private static PlanTuple BuildColdPlan(InvestigationRequest request, InvestigationConstraints constraints)
    {
        var pid = request.ProcessId;
        var steps = new List<InvestigationStep>
        {
            new(
                StepNumber: 1,
                StepId: "vitals",
                ToolName: "collect_events",
                ToolParams: new Dictionary<string, object?> { ["kind"] = "counters", ["processId"] = pid, ["durationSeconds"] = 5 },
                Rationale: "USE-style vitals first. collect_events(kind=\"counters\") in 5s covers ~80% of first-triage diagnostics and produces the branching evidence for every other step.",
                Branches: new[]
                {
                    new DecisionBranch("cpu_pct > 70", "cpu-sample", "Hot CPU → sample the process."),
                    new DecisionBranch("gc_time_pct > 20", "gc-events", "GC-bound → collect GC events."),
                    new DecisionBranch("threadpool_thread_count growing", "threadpool", "ThreadPool starvation pattern."),
                    new DecisionBranch("exceptions_per_sec > 10", "exceptions", "Exception storm."),
                    new DecisionBranch("default", "http", "No obvious signal → outbound HTTP."),
                }),
            new(
                StepNumber: 2,
                StepId: "cpu-sample",
                ToolName: "collect_sample",
                ToolParams: new Dictionary<string, object?> { ["kind"] = "cpu", ["processId"] = pid, ["durationSeconds"] = 20, ["topN"] = 25 },
                Rationale: "If vitals show CPU pressure, sample to identify hot methods. Drill with query_snapshot(handle, view=\"call-tree\").",
                Branches: new[]
                {
                    new DecisionBranch("top exclusive_pct > 30% AND is_user_code", "report-cpu", "Found a user-code hotspot."),
                    new DecisionBranch("top exclusive frame in System.Threading.Monitor", "lock-events", "Confirm contention with ContentionKeyword."),
                }),
            new(
                StepNumber: 3,
                StepId: "gc-events",
                ToolName: "collect_events",
                ToolParams: new Dictionary<string, object?> { ["kind"] = "gc", ["processId"] = pid, ["durationSeconds"] = 30 },
                Rationale: "If GC time is high, attribute gen0/1/2 collection cost and reasons.",
                Branches: new[]
                {
                    new DecisionBranch("gen2_count > 0 AND survival_rate > 60%", "dump-heap", "Likely memory leak — request dump (approval-gated)."),
                    new DecisionBranch("loh_alloc_pct > 30%", "report-loh", "LOH pressure → report and recommend pooling."),
                }),
            new(
                StepNumber: 4,
                StepId: "threadpool",
                ToolName: "collect_events",
                ToolParams: new Dictionary<string, object?>
                {
                    ["kind"] = "event_source",
                    ["processId"] = pid,
                    ["providerName"] = "System.Threading.Tasks.TplEventSource",
                    ["durationSeconds"] = 30,
                },
                Rationale: "TplEventSource exposes work-item enqueue/dequeue lag and thread injection — the canonical starvation signal.",
                Branches: new[]
                {
                    new DecisionBranch("ThreadPoolWorkerThreadAdjustment dominates", "report-tp-starvation", "Confirmed starvation → report sync-over-async or blocking I/O."),
                }),
            new(
                StepNumber: 5,
                StepId: "exceptions",
                ToolName: "collect_events",
                ToolParams: new Dictionary<string, object?> { ["kind"] = "exceptions", ["processId"] = pid, ["durationSeconds"] = 15 },
                Rationale: "Cheap and high signal when exceptions/sec is elevated — surfaces top exception types and throw sites.",
                Branches: new[]
                {
                    new DecisionBranch("single exception type > 80% of throws", "report-exceptions", "Single failure mode → report and recommend guard rails."),
                }),
            new(
                StepNumber: 6,
                StepId: "http",
                ToolName: "collect_events",
                ToolParams: new Dictionary<string, object?>
                {
                    ["kind"] = "event_source",
                    ["processId"] = pid,
                    ["providerName"] = "System.Net.Http",
                    ["durationSeconds"] = 30,
                },
                Rationale: "When vitals are flat, latency is often outbound. System.Net.Http surfaces per-request timing without changing the app.",
                Branches: new[]
                {
                    new DecisionBranch("outbound P95 > 500ms", "report-http", "Downstream dependency is the bottleneck."),
                }),
            new(
                StepNumber: 7,
                StepId: "lock-events",
                ToolName: "collect_events",
                ToolParams: new Dictionary<string, object?>
                {
                    ["kind"] = "event_source",
                    ["processId"] = pid,
                    ["providerName"] = "Microsoft-Windows-DotNETRuntime",
                    ["keywords"] = ContentionKeyword,
                    ["durationSeconds"] = 20,
                },
                Rationale: "ContentionKeyword surfaces Monitor lock contention with contending thread and duration.",
                Branches: new[]
                {
                    new DecisionBranch("contention rate > 100/s on a single lock", "report-lock", "Report the lock-holder."),
                }),
        };

        var terminals = new List<InvestigationTerminal>
        {
            ReportTerminal("report-cpu", "CPU hotspot identified", "High CPU in {frame}: {exclusivePct}% exclusive. Optimize or parallelize."),
            ReportTerminal("report-loh", "LOH allocation pressure identified", "LOH allocations {pct}%: recommend ArrayPool / chunking."),
            ReportTerminal("report-tp-starvation", "ThreadPool starvation confirmed", "Worker injection storm: locate sync-over-async or blocking I/O."),
            ReportTerminal("report-exceptions", "Exception storm identified", "{exceptionType} accounts for {pct}% of throws: add guard rails / fix root cause."),
            ReportTerminal("report-http", "Downstream HTTP bottleneck", "Outbound dependency P95 > 500ms on {host}."),
            ReportTerminal("report-lock", "Lock contention identified", "Contention on {lockSite}: serialize less work or restructure."),
            DumpTerminal("dump-heap", pid, constraints, "WithHeap dump required to walk the heap for retention paths."),
        };

        var earlyStops = new[]
        {
            new EarlyStopCondition(
                ConditionId: "user-hotspot-conclusive",
                Description: "A user-code frame holds > 30% exclusive samples AND matches the symptom description.",
                Action: "stop_and_report_root_cause",
                ReportTemplate: "CPU hotspot in {frame}: {exclusivePct}% exclusive. Optimize or parallelize."),
            new EarlyStopCondition(
                ConditionId: "max-tool-calls-reached",
                Description: "MaxToolCalls reached without conclusive evidence — stop and report what was learned.",
                Action: "stop_and_summarize"),
        };

        var alternates = new[]
        {
            new AlternateBranch("memory-leak", "working-set grows > 10% in 60s mid-investigation", "collect_events",
                "Switch to the memory-leak path even if vitals were CPU-shaped initially (kind=\"gc\")."),
            new AlternateBranch("hung-app", "collect_events(kind=\"counters\") returns no live counters in 5s", "collect_process_dump",
                "Process may be hung; request Mini dump (approval-gated)."),
        };

        return new PlanTuple(steps, terminals, earlyStops, alternates, null);
    }

    // ───────────────────────────── WARM ─────────────────────────────

    private static PlanTuple BuildWarmPlan(InvestigationRequest request, InvestigationConstraints constraints)
    {
        var pid = request.ProcessId;
        var baseline = request.Baseline!;
        var steps = new List<InvestigationStep>
        {
            new(
                StepNumber: 1,
                StepId: "vitals-delta",
                ToolName: "collect_events",
                ToolParams: new Dictionary<string, object?> { ["kind"] = "counters", ["processId"] = pid, ["durationSeconds"] = 5 },
                Rationale: "Re-collect the same counters as baseline so we can diff. Anything within ±10% of baseline is noise; flag the rest.",
                Branches: new[]
                {
                    new DecisionBranch("cpu_pct regressed > 20% vs baseline", "cpu-sample-delta", "CPU regression → resample and compare hotspots."),
                    new DecisionBranch("gen2 collections regressed > 50% vs baseline", "gc-events-delta", "GC regression → collect GC events."),
                    new DecisionBranch("all metrics within ±10% of baseline", "report-regression-gone", "No regression detected."),
                }),
            new(
                StepNumber: 2,
                StepId: "cpu-sample-delta",
                ToolName: "collect_sample",
                ToolParams: new Dictionary<string, object?> { ["kind"] = "cpu", ["processId"] = pid, ["durationSeconds"] = 20, ["topN"] = 25 },
                Rationale: "Compare top hotspots against baseline.cpuHotspots. New frames in the top-N are the regression suspects.",
                Branches: new[]
                {
                    new DecisionBranch("new frame appears in top-5 vs baseline", "report-cpu-regression", "Identified the regressing frame."),
                }),
            new(
                StepNumber: 3,
                StepId: "gc-events-delta",
                ToolName: "collect_events",
                ToolParams: new Dictionary<string, object?> { ["kind"] = "gc", ["processId"] = pid, ["durationSeconds"] = 30 },
                Rationale: "Quantify the GC regression: gen2 frequency, pause time, LOH allocations.",
                Branches: new[]
                {
                    new DecisionBranch("default", "report-gc-regression", "Emit a GC delta report."),
                }),
        };

        var terminals = new List<InvestigationTerminal>
        {
            ReportTerminal("report-regression-gone", "Baseline state confirmed", "All metrics within ±10% of baseline {baselineId}: regression is no longer reproducible."),
            ReportTerminal("report-cpu-regression", "CPU regression identified", "New top frame {frame} not present in baseline: investigate recent commits."),
            ReportTerminal("report-gc-regression", "GC regression identified", "Gen2 frequency / LOH delta: {evidence}."),
        };

        var earlyStops = new[]
        {
            new EarlyStopCondition(
                ConditionId: "regression-gone",
                Description: "All baseline metrics match within ±10% — the regression is no longer reproducible.",
                Action: "stop_and_report_resolved",
                ReportTemplate: "Baseline state confirmed at {timestamp}. No regression vs investigation {baselineId}."),
            new EarlyStopCondition(
                ConditionId: "max-tool-calls-reached",
                Description: "MaxToolCalls reached — stop and emit a delta report regardless.",
                Action: "stop_and_summarize"),
        };

        var alternates = new[]
        {
            new AlternateBranch("new-symptom",
                "Counter outside baseline scope shows pressure (e.g. new ThreadPool growth not in baseline.keyMetrics)",
                "start_investigation",
                "Treat as a fresh cold investigation; baseline doesn't cover this signal."),
        };

        var comparisons = baseline.KeyMetrics
            .Select(kv => new MetricComparison(kv.Key, kv.Value, Unit: null, RegressionThresholdPercent: 10.0))
            .ToArray();

        return new PlanTuple(steps, terminals, earlyStops, alternates, comparisons);
    }

    // ───────────────────────────── HYPOTHESIS ─────────────────────────────

    private static PlanTuple BuildHypothesisPlan(InvestigationRequest request, InvestigationConstraints constraints)
    {
        var pid = request.ProcessId;
        var hypothesis = request.Hypothesis!;
        var route = RouteHypothesis(hypothesis);

        var (steps, terminals) = route switch
        {
            HypothesisRoute.Lock => LockSteps(pid),
            HypothesisRoute.Cpu => CpuSteps(pid),
            HypothesisRoute.Memory => MemorySteps(pid, constraints),
            HypothesisRoute.ThreadPool => ThreadPoolSteps(pid),
            HypothesisRoute.Exceptions => ExceptionSteps(pid),
            HypothesisRoute.Startup => StartupSteps(pid),
            _ => FallbackSteps(pid),
        };

        var earlyStops = new[]
        {
            new EarlyStopCondition(
                ConditionId: "hypothesis-confirmed",
                Description: $"Evidence consistent with hypothesis: \"{hypothesis}\". E.g. signature metric ≥ 3x typical or matching hotspot.",
                Action: "stop_and_report_root_cause",
                ReportTemplate: "Hypothesis CONFIRMED: {hypothesis}. Evidence: {evidence}."),
            new EarlyStopCondition(
                ConditionId: "hypothesis-refuted",
                Description: $"Signature metric is at typical levels AND no matching hotspot for: \"{hypothesis}\".",
                Action: "revert_to_cold",
                ReportTemplate: "Hypothesis REFUTED: {hypothesis}. Falling back to symptom-driven cold investigation."),
            new EarlyStopCondition(
                ConditionId: "max-tool-calls-reached",
                Description: "MaxToolCalls reached — stop and summarize whether the hypothesis is supported or not.",
                Action: "stop_and_summarize"),
        };

        var alternates = new[]
        {
            new AlternateBranch("revert-to-cold",
                "Hypothesis refuted but symptom persists",
                "start_investigation",
                "Re-enter cold mode with the original symptom."),
        };

        return new PlanTuple(steps, terminals, earlyStops, alternates, null);
    }

    private enum HypothesisRoute { Lock, Cpu, Memory, ThreadPool, Exceptions, Startup, Unknown }

    private static HypothesisRoute RouteHypothesis(string hypothesis)
    {
        var lower = hypothesis.ToLower(CultureInfo.InvariantCulture);
        if (ContainsAny(lower, "lock", "contention", "monitor", "mutex", "deadlock")) return HypothesisRoute.Lock;
        if (ContainsAny(lower, "memory", "leak", "heap", "gc", "gen2", "loh", "allocation")) return HypothesisRoute.Memory;
        if (ContainsAny(lower, "threadpool", "thread pool", "starvation", "sync over async", "sync-over-async")) return HypothesisRoute.ThreadPool;
        if (ContainsAny(lower, "exception", "throw", "first-chance", "error rate")) return HypothesisRoute.Exceptions;
        if (ContainsAny(lower, "startup", "cold start", "jit", "warm-up")) return HypothesisRoute.Startup;
        if (ContainsAny(lower, "cpu", "hot path", "loop", "regex", "serialization", "json")) return HypothesisRoute.Cpu;
        return HypothesisRoute.Unknown;
    }

    private static bool ContainsAny(string haystack, params string[] needles)
        => needles.Any(n => haystack.Contains(n, StringComparison.Ordinal));

    // ───────────────────────────── hypothesis sub-plans ─────────────────────────────

    private static (InvestigationStep[], InvestigationTerminal[]) LockSteps(int pid)
    {
        var steps = new InvestigationStep[]
        {
            new(1, "lock-events", "collect_events",
                new Dictionary<string, object?>
                {
                    ["kind"] = "event_source",
                    ["processId"] = pid,
                    ["providerName"] = "Microsoft-Windows-DotNETRuntime",
                    ["keywords"] = ContentionKeyword,
                    ["durationSeconds"] = 20,
                },
                "ContentionKeyword surfaces Monitor lock contention events with contending thread and duration — direct evidence for the hypothesis.",
                new[] { new DecisionBranch("contention rate > 100/s on a single lock", "lock-sample", "Confirmed; sample CPU to locate the lock-holder method.") }),
            new(2, "lock-sample", "collect_sample",
                new Dictionary<string, object?> { ["kind"] = "cpu", ["processId"] = pid, ["durationSeconds"] = 20, ["topN"] = 25 },
                "Correlate the contention events with hot frames in Monitor.* / SpinWait to name the offending method.",
                new[] { new DecisionBranch("Monitor.* or SpinWait in top exclusive frames", "report-lock", "Report the lock-holder.") }),
        };
        var terminals = new[]
        {
            ReportTerminal("report-lock", "Lock contention identified", "Contention on {lockSite}: serialize less or restructure."),
        };
        return (steps, terminals);
    }

    private static (InvestigationStep[], InvestigationTerminal[]) CpuSteps(int pid)
    {
        var steps = new InvestigationStep[]
        {
            new(1, "cpu-vitals", "collect_events",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 5 },
                "Confirm CPU is actually pressured before sampling.",
                new[] { new DecisionBranch("cpu_pct > 50", "cpu-sample", "Move to sampling.") }),
            new(2, "cpu-sample", "collect_sample",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 20, ["topN"] = 25 },
                "Direct evidence for a CPU hypothesis. Drill the handle with get_call_tree.",
                new[] { new DecisionBranch("hypothesized frame in top-10", "report-cpu", "Confirmed.") }),
        };
        var terminals = new[]
        {
            ReportTerminal("report-cpu", "CPU hotspot confirmed", "Hot frame {frame} matches hypothesis: optimize or parallelize."),
        };
        return (steps, terminals);
    }

    private static (InvestigationStep[], InvestigationTerminal[]) MemorySteps(int pid, InvestigationConstraints constraints)
    {
        var steps = new InvestigationStep[]
        {
            new(1, "memory-vitals", "collect_events",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 15 },
                "15s captures multiple GC ticks so we can see whether heap is growing or steady.",
                new[] { new DecisionBranch("gen2-size growing AND working-set growing", "gc-events", "Likely leak.") }),
            new(2, "gc-events", "collect_events",
                new Dictionary<string, object?> { ["kind"] = "gc", ["processId"] = pid, ["durationSeconds"] = 30 },
                "Attribute survival rate per generation and identify allocation-heavy phases.",
                new[] { new DecisionBranch("survival_rate > 60% on gen2", "dump-heap", "Confirm with a Mini dump (approval-gated).") }),
        };
        var terminals = new[]
        {
            DumpTerminal("dump-heap", pid, constraints, "Confirm leak with a heap dump for retention paths."),
        };
        return (steps, terminals);
    }

    private static (InvestigationStep[], InvestigationTerminal[]) ThreadPoolSteps(int pid)
    {
        var steps = new InvestigationStep[]
        {
            new(1, "tp-vitals", "collect_events",
                new Dictionary<string, object?> { ["kind"] = "counters", ["processId"] = pid, ["durationSeconds"] = 30, ["intervalSeconds"] = 2 },
                "30s @ 2s intervals shows thread.count slope — the canonical starvation fingerprint.",
                new[] { new DecisionBranch("thread.count grows 1-2/s for 20s+", "tp-events", "Confirm with TplEventSource.") }),
            new(2, "tp-events", "collect_events",
                new Dictionary<string, object?>
                {
                    ["kind"] = "event_source",
                    ["processId"] = pid,
                    ["providerName"] = "System.Threading.Tasks.TplEventSource",
                    ["durationSeconds"] = 30,
                },
                "Worker-thread adjustment events confirm injection storms typical of sync-over-async.",
                new[] { new DecisionBranch("ThreadPoolWorkerThreadAdjustment dominates", "report-tp-starvation", "Confirmed starvation.") }),
        };
        var terminals = new[]
        {
            ReportTerminal("report-tp-starvation", "ThreadPool starvation confirmed", "Worker injection storm: locate sync-over-async or blocking I/O."),
        };
        return (steps, terminals);
    }

    private static (InvestigationStep[], InvestigationTerminal[]) ExceptionSteps(int pid)
    {
        var steps = new InvestigationStep[]
        {
            new(1, "exception-collect", "collect_events",
                new Dictionary<string, object?> { ["kind"] = "exceptions", ["processId"] = pid, ["durationSeconds"] = 15 },
                "Direct top-N exception type aggregation for the hypothesis.",
                new[] { new DecisionBranch("hypothesized exception type dominates", "report-exceptions", "Confirmed.") }),
        };
        var terminals = new[]
        {
            ReportTerminal("report-exceptions", "Exception storm confirmed", "{exceptionType} dominates throws: add guard rails."),
        };
        return (steps, terminals);
    }

    private static (InvestigationStep[], InvestigationTerminal[]) StartupSteps(int pid)
    {
        var steps = new InvestigationStep[]
        {
            new(1, "startup-vitals", "collect_events",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 10 },
                "jit.compilation.time and loader counters tell whether JIT churn or assembly loads dominate startup.",
                new[] { new DecisionBranch("jit.compilation.time > 1s after warm-up", "report-jit-churn", "JIT churn → recommend R2R/AOT.") }),
        };
        var terminals = new[]
        {
            ReportTerminal("report-jit-churn", "JIT churn at startup", "Excess jit.compilation.time: enable R2R or NativeAOT for cold path."),
        };
        return (steps, terminals);
    }

    private static (InvestigationStep[], InvestigationTerminal[]) FallbackSteps(int pid)
    {
        var steps = new InvestigationStep[]
        {
            new(1, "vitals", "collect_events",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 5 },
                "Hypothesis didn't match any known route — fall back to cold-mode vitals.",
                new[] { new DecisionBranch("default", "report-revert-to-cold", "Re-run start_investigation without the hypothesis field.") }),
        };
        var terminals = new[]
        {
            new InvestigationTerminal(
                TerminalId: "report-revert-to-cold",
                Action: "revert_to_cold",
                Description: "Hypothesis unrecognized — restart in cold mode.",
                ReportTemplate: "Could not route hypothesis; re-running as cold investigation."),
        };
        return (steps, terminals);
    }

    // ───────────────────────────── terminal builders ─────────────────────────────

    private static InvestigationTerminal ReportTerminal(string id, string description, string template)
        => new(TerminalId: id, Action: "stop_and_report_root_cause", Description: description, ReportTemplate: template);

    private static InvestigationTerminal DumpTerminal(string id, int pid, InvestigationConstraints constraints, string description)
        => new(
            TerminalId: id,
            Action: "request_dump",
            Description: description,
            ToolName: "collect_process_dump",
            ToolParams: new Dictionary<string, object?>
            {
                ["processId"] = pid,
                ["dumpType"] = constraints.MaxDumpType,
            },
            RequiresApproval: true); // dumps are always approval-gated regardless of constraints

    private sealed record PlanTuple(
        IReadOnlyList<InvestigationStep> Steps,
        IReadOnlyList<InvestigationTerminal> Terminals,
        IReadOnlyList<EarlyStopCondition> EarlyStops,
        IReadOnlyList<AlternateBranch> Alternates,
        IReadOnlyList<MetricComparison>? Comparisons);
}
