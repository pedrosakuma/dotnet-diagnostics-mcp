using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.Memory;

namespace DotnetDiagnosticsMcp.Core.Drilldown;

public sealed record MethodDiffKey(SymbolRef Symbol, MethodIdentity? Identity = null);

public sealed record CpuDiffMetric(
    long ExclusiveSamples,
    long InclusiveSamples,
    double ExclusivePercent);

public sealed record HeapDiffMetric(
    long TotalBytes,
    long InstanceCount);

public sealed record AllocationDiffMetric(
    long TotalBytes,
    long AllocCount,
    double BytesPerSecond,
    double AllocCountPerSecond,
    double DurationSeconds);

public sealed record DiffRow<TKey, TMetric>(
    TKey Key,
    TMetric? Baseline,
    TMetric? Current,
    double DeltaAbs,
    double DeltaPct,
    string Direction);

public sealed record SampleDiff<TKey, TMetric>(
    string Kind,
    string BaselineHandle,
    string CurrentHandle,
    double MinDeltaPct,
    int TotalAdded,
    int TotalRemoved,
    int TotalChanged,
    IReadOnlyList<DiffRow<TKey, TMetric>> Added,
    IReadOnlyList<DiffRow<TKey, TMetric>> Removed,
    IReadOnlyList<DiffRow<TKey, TMetric>> Changed,
    string Verdict)
{
    public IReadOnlyList<string>? Notes { get; init; }
}

public static class SampleDiffer
{
    public static SampleDiff<MethodDiffKey, CpuDiffMetric> Compare(
        CpuSampleTraceArtifact baseline,
        string baselineHandle,
        CpuSampleTraceArtifact current,
        string currentHandle,
        double minDeltaPct,
        int topN)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        ValidateThresholds(minDeltaPct, topN);

        var notes = new List<string>();
        if (baseline.ProcessId != current.ProcessId)
        {
            notes.Add($"Comparison spans different runs/processes: baseline pid {baseline.ProcessId}, current pid {current.ProcessId}.");
        }

        var diff = BuildDiff(
            kind: "cpu-sample",
            baselineHandle,
            currentHandle,
            minDeltaPct,
            topN,
            baseline: ProjectCpu(baseline),
            current: ProjectCpu(current),
            primaryMetric: static metric => metric.ExclusivePercent,
            notes);

        return diff;
    }

    public static SampleDiff<TypeIdentity, HeapDiffMetric> Compare(
        HeapSnapshotArtifact baseline,
        string baselineHandle,
        HeapSnapshotArtifact current,
        string currentHandle,
        double minDeltaPct,
        int topN)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        ValidateThresholds(minDeltaPct, topN);

        var notes = new List<string>();
        if (baseline.ProcessId != current.ProcessId)
        {
            notes.Add($"Comparison spans different runs/processes: baseline pid {baseline.ProcessId}, current pid {current.ProcessId}.");
        }

        return BuildDiff(
            kind: "heap-snapshot",
            baselineHandle,
            currentHandle,
            minDeltaPct,
            topN,
            baseline: ProjectHeap(baseline),
            current: ProjectHeap(current),
            primaryMetric: static metric => metric.TotalBytes,
            notes);
    }

    public static SampleDiff<TypeIdentity, AllocationDiffMetric> Compare(
        AllocationSample baseline,
        string baselineHandle,
        AllocationSample current,
        string currentHandle,
        double minDeltaPct,
        int topN)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        ValidateThresholds(minDeltaPct, topN);

        var notes = new List<string>();
        if (baseline.ProcessId != current.ProcessId)
        {
            notes.Add($"Comparison spans different runs/processes: baseline pid {baseline.ProcessId}, current pid {current.ProcessId}.");
        }
        if (baseline.Duration != current.Duration)
        {
            notes.Add($"Allocation diff normalized totals to per-second rates because durations differ ({baseline.Duration.TotalSeconds:F1}s → {current.Duration.TotalSeconds:F1}s).");
        }

        return BuildDiff(
            kind: "allocation-sample",
            baselineHandle,
            currentHandle,
            minDeltaPct,
            topN,
            baseline: ProjectAllocation(baseline),
            current: ProjectAllocation(current),
            primaryMetric: static metric => metric.BytesPerSecond,
            notes);
    }

    private static SampleDiff<TKey, TMetric> BuildDiff<TKey, TMetric>(
        string kind,
        string baselineHandle,
        string currentHandle,
        double minDeltaPct,
        int topN,
        IReadOnlyDictionary<TKey, TMetric> baseline,
        IReadOnlyDictionary<TKey, TMetric> current,
        Func<TMetric, double> primaryMetric,
        List<string> notes)
        where TKey : notnull
        where TMetric : class
    {
        var baselineMedian = Median(baseline.Values.Select(primaryMetric));
        var overlapCount = baseline.Keys.Count(current.ContainsKey);

        var addedAll = current
            .Where(kv => !baseline.ContainsKey(kv.Key))
            .Select(kv => new DiffRow<TKey, TMetric>(
                kv.Key,
                Baseline: default,
                Current: kv.Value,
                DeltaAbs: Math.Round(primaryMetric(kv.Value), 2),
                DeltaPct: 100,
                Direction: "added"))
            .OrderByDescending(row => MetricForSort(row.Current, primaryMetric))
            .ToArray();

        var removedAll = baseline
            .Where(kv => !current.ContainsKey(kv.Key))
            .Select(kv => new DiffRow<TKey, TMetric>(
                kv.Key,
                Baseline: kv.Value,
                Current: default,
                DeltaAbs: Math.Round(-primaryMetric(kv.Value), 2),
                DeltaPct: -100,
                Direction: "removed"))
            .OrderByDescending(row => MetricForSort(row.Baseline, primaryMetric))
            .ToArray();

        var changedAll = baseline
            .Where(kv => current.ContainsKey(kv.Key))
            .Select(kv =>
            {
                var baselineMetric = kv.Value;
                var currentMetric = current[kv.Key];
                var baselinePrimary = primaryMetric(baselineMetric);
                var currentPrimary = primaryMetric(currentMetric);
                var deltaAbs = Math.Round(currentPrimary - baselinePrimary, 2);
                var deltaPct = PercentDelta(baselinePrimary, currentPrimary);
                return new DiffRow<TKey, TMetric>(
                    kv.Key,
                    Baseline: baselineMetric,
                    Current: currentMetric,
                    DeltaAbs: deltaAbs,
                    DeltaPct: deltaPct,
                    Direction: deltaAbs >= 0 ? "up" : "down");
            })
            .Where(row => Math.Abs(row.DeltaPct) >= minDeltaPct)
            .OrderByDescending(row => Math.Abs(row.DeltaPct))
            .ThenByDescending(row => Math.Abs(row.DeltaAbs))
            .ToArray();

        if (overlapCount == 0)
        {
            notes.Add("No overlapping symbols/types between baseline and current handles; verdict forced to no_change.");
        }

        var regressionSignal = overlapCount > 0 && (
            changedAll.Any(row => row.Direction == "up") ||
            addedAll.Any(row => MetricForSort(row.Current, primaryMetric) > baselineMedian));

        var improvementSignal = overlapCount > 0 && (
            changedAll.Any(row => row.Direction == "down") ||
            removedAll.Any(row => MetricForSort(row.Baseline, primaryMetric) > baselineMedian));

        var verdict = overlapCount == 0
            ? "no_change"
            : regressionSignal && improvementSignal ? "mixed"
            : regressionSignal ? "regression"
            : improvementSignal ? "improvement"
            : "no_change";

        return new SampleDiff<TKey, TMetric>(
            Kind: kind,
            BaselineHandle: baselineHandle,
            CurrentHandle: currentHandle,
            MinDeltaPct: minDeltaPct,
            TotalAdded: addedAll.Length,
            TotalRemoved: removedAll.Length,
            TotalChanged: changedAll.Length,
            Added: addedAll.Take(topN).ToArray(),
            Removed: removedAll.Take(topN).ToArray(),
            Changed: changedAll.Take(topN).ToArray(),
            Verdict: verdict)
        {
            Notes = notes.Count > 0 ? notes : null,
        };
    }

    private static Dictionary<MethodDiffKey, CpuDiffMetric> ProjectCpu(CpuSampleTraceArtifact artifact)
    {
        var totals = new Dictionary<MethodDiffKey, (long Exclusive, long Inclusive)>(MethodDiffKeyComparer.Instance);
        foreach (var node in Flatten(artifact.Root))
        {
            if (string.Equals(node.Frame.Method, "<root>", StringComparison.Ordinal))
            {
                continue;
            }

            if (node.ExclusiveSamples <= 0 && node.InclusiveSamples <= 0)
            {
                continue;
            }

            var symbol = new SymbolRef(node.Frame.Module, node.Frame.Method);
            artifact.MethodIdentities.TryGetValue(symbol, out var identity);
            var key = new MethodDiffKey(symbol, identity);
            totals.TryGetValue(key, out var aggregate);
            aggregate.Exclusive += node.ExclusiveSamples;
            aggregate.Inclusive = Math.Max(aggregate.Inclusive, node.InclusiveSamples);
            totals[key] = aggregate;
        }

        var totalSamples = artifact.TotalSamples == 0 ? 1 : artifact.TotalSamples;
        return totals.ToDictionary(
            kv => kv.Key,
            kv => new CpuDiffMetric(
                ExclusiveSamples: kv.Value.Exclusive,
                InclusiveSamples: kv.Value.Inclusive,
                ExclusivePercent: Math.Round(100.0 * kv.Value.Exclusive / totalSamples, 2)),
            MethodDiffKeyComparer.Instance);
    }

    private static Dictionary<TypeIdentity, HeapDiffMetric> ProjectHeap(HeapSnapshotArtifact artifact)
    {
        var result = new Dictionary<TypeIdentity, HeapDiffMetric>(TypeIdentityComparer.Instance);
        foreach (var stat in artifact.TopTypesByBytes.Concat(artifact.TopTypesByInstances))
        {
            var key = stat.Identity ?? new TypeIdentity(stat.TypeFullName) { ModuleName = stat.ModuleName };
            if (result.TryGetValue(key, out var existing))
            {
                result[key] = new HeapDiffMetric(
                    TotalBytes: Math.Max(existing.TotalBytes, stat.TotalBytes),
                    InstanceCount: Math.Max(existing.InstanceCount, stat.InstanceCount));
                continue;
            }

            result[key] = new HeapDiffMetric(stat.TotalBytes, stat.InstanceCount);
        }

        return result;
    }

    private static Dictionary<TypeIdentity, AllocationDiffMetric> ProjectAllocation(AllocationSample sample)
    {
        var result = new Dictionary<TypeIdentity, AllocationDiffMetric>(TypeIdentityComparer.Instance);
        foreach (var stat in sample.TopByBytes.Concat(sample.TopByCount))
        {
            var key = stat.Identity ?? new TypeIdentity(stat.TypeName);
            var metric = new AllocationDiffMetric(
                TotalBytes: stat.TotalBytes,
                AllocCount: stat.EventCount,
                BytesPerSecond: PerSecond(stat.TotalBytes, sample.Duration),
                AllocCountPerSecond: PerSecond(stat.EventCount, sample.Duration),
                DurationSeconds: Math.Round(sample.Duration.TotalSeconds, 2));

            if (result.TryGetValue(key, out var existing))
            {
                result[key] = new AllocationDiffMetric(
                    TotalBytes: Math.Max(existing.TotalBytes, metric.TotalBytes),
                    AllocCount: Math.Max(existing.AllocCount, metric.AllocCount),
                    BytesPerSecond: Math.Max(existing.BytesPerSecond, metric.BytesPerSecond),
                    AllocCountPerSecond: Math.Max(existing.AllocCountPerSecond, metric.AllocCountPerSecond),
                    DurationSeconds: metric.DurationSeconds);
                continue;
            }

            result[key] = metric;
        }

        return result;
    }

    private static IEnumerable<CallTreeNode> Flatten(CallTreeNode root)
    {
        var stack = new Stack<CallTreeNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            foreach (var child in current.Children)
            {
                stack.Push(child);
            }
        }
    }

    private static double MetricForSort<TMetric>(TMetric? metric, Func<TMetric, double> primaryMetric)
        where TMetric : class
        => metric is null ? 0 : primaryMetric(metric);

    private static double PerSecond(long value, TimeSpan duration)
        => Math.Round(duration.TotalSeconds <= 0 ? 0 : value / duration.TotalSeconds, 2);

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(v => v).ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }

        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? Math.Round((ordered[middle - 1] + ordered[middle]) / 2, 2)
            : Math.Round(ordered[middle], 2);
    }

    private static double PercentDelta(double baseline, double current)
    {
        if (baseline == 0)
        {
            return current == 0 ? 0 : 100;
        }

        return Math.Round(((current - baseline) / Math.Abs(baseline)) * 100, 2);
    }

    private static void ValidateThresholds(double minDeltaPct, int topN)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minDeltaPct);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topN);
    }

    private sealed class MethodDiffKeyComparer : IEqualityComparer<MethodDiffKey>
    {
        public static MethodDiffKeyComparer Instance { get; } = new();

        public bool Equals(MethodDiffKey? x, MethodDiffKey? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }
            if (x is null || y is null)
            {
                return false;
            }

            var xHasKey = TryGetMethodKey(x.Identity, out var left);
            var yHasKey = TryGetMethodKey(y.Identity, out var right);
            if (xHasKey || yHasKey)
            {
                return xHasKey && yHasKey && left == right;
            }

            return EqualityComparer<SymbolRef>.Default.Equals(x.Symbol, y.Symbol);
        }

        public int GetHashCode(MethodDiffKey obj)
            => TryGetMethodKey(obj.Identity, out var key)
                ? HashCode.Combine(key.ModuleVersionId, key.MetadataToken)
                : obj.Symbol.GetHashCode();
    }

    private sealed class TypeIdentityComparer : IEqualityComparer<TypeIdentity>
    {
        public static TypeIdentityComparer Instance { get; } = new();

        public bool Equals(TypeIdentity? x, TypeIdentity? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }
            if (x is null || y is null)
            {
                return false;
            }

            var xHasKey = TryGetTypeKey(x, out var left);
            var yHasKey = TryGetTypeKey(y, out var right);
            if (xHasKey || yHasKey)
            {
                return xHasKey && yHasKey && left == right;
            }

            return string.Equals(x.TypeFullName, y.TypeFullName, StringComparison.Ordinal);
        }

        public int GetHashCode(TypeIdentity obj)
            => TryGetTypeKey(obj, out var key)
                ? HashCode.Combine(key.ModuleVersionId, key.MetadataToken)
                : StringComparer.Ordinal.GetHashCode(obj.TypeFullName);
    }

    private static bool TryGetMethodKey(MethodIdentity? identity, out (Guid ModuleVersionId, int MetadataToken) key)
    {
        if (identity is { ModuleVersionId: Guid mvid, MetadataToken: int token })
        {
            key = (mvid, token);
            return true;
        }

        key = default;
        return false;
    }

    private static bool TryGetTypeKey(TypeIdentity? identity, out (Guid ModuleVersionId, int MetadataToken) key)
    {
        if (identity is { ModuleVersionId: Guid mvid, MetadataToken: int token })
        {
            key = (mvid, token);
            return true;
        }

        key = default;
        return false;
    }
}
