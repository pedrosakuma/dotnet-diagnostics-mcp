namespace DotnetDiagnosticsMcp.Core.OffCpu;

/// <summary>
/// Shared aggregation pipeline for off-CPU spans coming from any backend
/// (<see cref="PerfSchedOffCpuSampler"/> on Linux, <see cref="EtwOffCpuSampler"/> on Windows).
/// Keeping the per-stack / per-thread rollup, censored-span accounting and Notes wiring in one
/// place ensures the <c>OffCpuSnapshotArtifact</c> shape returned to the LLM (and queried via
/// <c>query_off_cpu_snapshot</c>) is platform-agnostic — only the raw spans and the
/// <c>SymbolSource</c> tag change between backends.
/// </summary>
internal static class OffCpuAggregator
{
    /// <summary>
    /// Aggregates a flat sequence of <see cref="OffCpuSpan"/> records into the lightweight
    /// summary plus the drill-down artifact. Identical aggregation rules across backends so the
    /// LLM never has to special-case Linux vs Windows results.
    /// </summary>
    /// <param name="processId">Target pid.</param>
    /// <param name="startedAt">Wall-clock start of the sampling window.</param>
    /// <param name="duration">Configured (not measured) window length.</param>
    /// <param name="spans">Raw closed off-CPU spans (and optionally censored ones with <see cref="OffCpuSpan.IsCensored"/>=true).</param>
    /// <param name="schedSwitches">Total context switches attributed to the target (capture-density signal).</param>
    /// <param name="topN">Max blocking stacks returned in the summary; the artifact retains all.</param>
    /// <param name="symbolSource">Backend tag — "perf-sched-dwarf" or "etw-cswitch-pdb".</param>
    /// <param name="notes">Best-effort warnings collected by the backend (size caps, late-attribution, etc.).</param>
    public static OffCpuSampleResult Aggregate(
        int processId,
        DateTimeOffset startedAt,
        TimeSpan duration,
        IReadOnlyList<OffCpuSpan> spans,
        long schedSwitches,
        int topN,
        string symbolSource,
        IReadOnlyList<string>? notes = null)
    {
        var byStack = new Dictionary<string, (long Micros, long Count, Dictionary<string, long> States, List<OffCpuFrame> Frames)>(StringComparer.Ordinal);
        var byThread = new Dictionary<int, (string Comm, long Micros, long Switches, Dictionary<string, long> LeafCounts)>();
        long totalMicros = 0;
        long censoredCount = 0;
        long censoredMicros = 0;

        foreach (var span in spans)
        {
            totalMicros += span.DurationMicros;
            if (span.IsCensored)
            {
                censoredCount++;
                censoredMicros += span.DurationMicros;
            }

            // perf prints leaf→root; reverse for human-friendly root→leaf in the rollup.
            var frames = new List<OffCpuFrame>(span.BlockingStack.Count);
            for (var i = span.BlockingStack.Count - 1; i >= 0; i--) frames.Add(span.BlockingStack[i]);
            var leaf = frames.Count > 0 ? frames[^1] : new OffCpuFrame("", "[no-stack]");
            var key = string.Join('|', frames.Select(f => string.IsNullOrEmpty(f.Module) ? f.Method : $"{f.Module}!{f.Method}"));

            if (!byStack.TryGetValue(key, out var agg))
            {
                agg = (0, 0, new Dictionary<string, long>(StringComparer.Ordinal), frames);
            }
            agg.Micros += span.DurationMicros;
            agg.Count += 1;
            agg.States[span.PrevState] = agg.States.GetValueOrDefault(span.PrevState) + 1;
            byStack[key] = agg;

            if (!byThread.TryGetValue(span.Tid, out var tagg))
            {
                tagg = (span.Comm, 0, 0, new Dictionary<string, long>(StringComparer.Ordinal));
            }
            tagg.Micros += span.DurationMicros;
            tagg.Switches += 1;
            var leafKey = string.IsNullOrEmpty(leaf.Module) ? leaf.Method : $"{leaf.Module}!{leaf.Method}";
            tagg.LeafCounts[leafKey] = tagg.LeafCounts.GetValueOrDefault(leafKey) + 1;
            byThread[span.Tid] = tagg;
        }

        var stacks = byStack
            .Select(kv =>
            {
                var dominant = kv.Value.States.OrderByDescending(s => s.Value).FirstOrDefault().Key ?? "?";
                var leaf = kv.Value.Frames.Count > 0 ? kv.Value.Frames[^1] : new OffCpuFrame("", "[no-stack]");
                return new OffCpuStackHotspot(
                    LeafFrame: string.IsNullOrEmpty(leaf.Module) ? leaf.Method : $"{leaf.Module}!{leaf.Method}",
                    OffCpuMicros: kv.Value.Micros,
                    OccurrenceCount: kv.Value.Count,
                    DominantState: dominant,
                    Stack: kv.Value.Frames);
            })
            .OrderByDescending(s => s.OffCpuMicros)
            .ToList();

        var threads = byThread
            .Select(kv =>
            {
                var topLeaf = kv.Value.LeafCounts.OrderByDescending(p => p.Value).FirstOrDefault().Key ?? "[no-stack]";
                return new OffCpuThreadView(
                    Tid: kv.Key,
                    ThreadName: kv.Value.Comm,
                    OffCpuMicros: kv.Value.Micros,
                    SwitchCount: kv.Value.Switches,
                    TopBlockingLeaf: topLeaf);
            })
            .OrderByDescending(t => t.OffCpuMicros)
            .ToList();

        var notesList = notes is { Count: > 0 } ? notes : null;
        if (censoredCount > 0)
        {
            var n = new List<string>(notesList ?? Array.Empty<string>());
            n.Add($"{censoredCount} span(s) ({censoredMicros} µs) were censored: the thread was still blocked when the capture window ended, so the duration is a lower bound.");
            notesList = n;
        }

        var summary = new OffCpuSnapshot(
            ProcessId: processId,
            StartedAt: startedAt,
            Duration: duration,
            TotalOffCpuMicros: totalMicros,
            DistinctThreads: byThread.Count,
            TopBlockingStacks: stacks.Take(topN).ToList(),
            SchedSwitches: schedSwitches,
            SymbolSource: symbolSource,
            CensoredSpans: censoredCount,
            CensoredOffCpuMicros: censoredMicros,
            Notes: notesList);

        var artifact = new OffCpuSnapshotArtifact(
            ProcessId: processId,
            StartedAt: startedAt,
            Duration: duration,
            TotalOffCpuMicros: totalMicros,
            SchedSwitches: schedSwitches,
            Stacks: stacks,
            Threads: threads,
            SymbolSource: symbolSource,
            CensoredSpans: censoredCount,
            CensoredOffCpuMicros: censoredMicros,
            Notes: notesList);

        return new OffCpuSampleResult(summary, artifact);
    }
}
