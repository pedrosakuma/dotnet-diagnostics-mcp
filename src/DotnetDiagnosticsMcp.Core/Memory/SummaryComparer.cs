namespace DotnetDiagnosticsMcp.Core.Memory;

/// <summary>
/// Compares two <see cref="InvestigationSummary"/> instances and reports a structured diff
/// aware of symbol stability (module + methodFullName survives rebuilds) and provenance
/// changes (image jump, git sha change). Lets the LLM tell "regression" from "different deploy".
/// </summary>
public interface ISummaryComparer
{
    SummaryDiff Compare(InvestigationSummary baseline, InvestigationSummary current);
}

public sealed record SummaryDiff(
    string Verdict,
    ProvenanceDelta Provenance,
    IReadOnlyList<HotspotDelta> NewHotspots,
    IReadOnlyList<HotspotDelta> RemovedHotspots,
    IReadOnlyList<HotspotDelta> ChangedHotspots);

public sealed record ProvenanceDelta(
    bool ImageChanged,
    bool GitShaChanged,
    bool AssemblyVersionChanged,
    bool ContainerChanged,
    string Summary);

public sealed record HotspotDelta(
    SymbolRef Symbol,
    double? BaselineInclusivePercent,
    double? CurrentInclusivePercent,
    double? InclusiveDeltaPoints);

public sealed class SummaryComparer : ISummaryComparer
{
    private const double SignificantChangePoints = 2.0;

    public SummaryDiff Compare(InvestigationSummary baseline, InvestigationSummary current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        var provenance = CompareProvenance(baseline.Provenance, current.Provenance);

        var baselineMap = baseline.Findings.TopHotspots.ToDictionary(h => h.Symbol);
        var currentMap = current.Findings.TopHotspots.ToDictionary(h => h.Symbol);

        var added = currentMap.Values
            .Where(h => !baselineMap.ContainsKey(h.Symbol))
            .Select(h => new HotspotDelta(h.Symbol, null, h.InclusivePercent, h.InclusivePercent))
            .OrderByDescending(d => d.CurrentInclusivePercent ?? 0)
            .ToArray();

        var removed = baselineMap.Values
            .Where(h => !currentMap.ContainsKey(h.Symbol))
            .Select(h => new HotspotDelta(h.Symbol, h.InclusivePercent, null, -h.InclusivePercent))
            .OrderByDescending(d => d.BaselineInclusivePercent ?? 0)
            .ToArray();

        var changed = baselineMap
            .Where(kv => currentMap.ContainsKey(kv.Key))
            .Select(kv =>
            {
                var b = kv.Value.InclusivePercent;
                var c = currentMap[kv.Key].InclusivePercent;
                return new HotspotDelta(kv.Key, b, c, Math.Round(c - b, 2));
            })
            .Where(d => Math.Abs(d.InclusiveDeltaPoints!.Value) >= SignificantChangePoints)
            .OrderByDescending(d => Math.Abs(d.InclusiveDeltaPoints!.Value))
            .ToArray();

        var verdict = Verdict(provenance, added, removed, changed);
        return new SummaryDiff(verdict, provenance, added, removed, changed);
    }

    private static ProvenanceDelta CompareProvenance(InvestigationProvenance b, InvestigationProvenance c)
    {
        var imageChanged = !string.Equals(b.Container?.Image, c.Container?.Image, StringComparison.Ordinal);
        var gitShaChanged = !string.Equals(b.Build?.GitSha, c.Build?.GitSha, StringComparison.Ordinal);
        var asmVerChanged = !string.Equals(b.Build?.AssemblyVersion, c.Build?.AssemblyVersion, StringComparison.Ordinal);
        var containerChanged = imageChanged;

        var parts = new List<string>();
        if (imageChanged) parts.Add($"image {b.Container?.Image ?? "(none)"} → {c.Container?.Image ?? "(none)"}");
        if (gitShaChanged) parts.Add($"git {b.Build?.GitSha ?? "(none)"} → {c.Build?.GitSha ?? "(none)"}");
        if (asmVerChanged) parts.Add($"version {b.Build?.AssemblyVersion ?? "(none)"} → {c.Build?.AssemblyVersion ?? "(none)"}");
        var summary = parts.Count == 0 ? "Same build + container" : string.Join("; ", parts);

        return new ProvenanceDelta(imageChanged, gitShaChanged, asmVerChanged, containerChanged, summary);
    }

    private static string Verdict(
        ProvenanceDelta provenance,
        HotspotDelta[] added,
        HotspotDelta[] removed,
        HotspotDelta[] changed)
    {
        if (added.Length == 0 && removed.Length == 0 && changed.Length == 0)
        {
            return provenance.ImageChanged || provenance.GitShaChanged
                ? "no_regression_after_deploy"
                : "no_regression";
        }

        // Treat new hotspot symbols as the strongest regression signal — they didn't exist in the baseline at all.
        if (added.Length > 0) return "regression_new_hotspot";
        if (changed.Length > 0 && changed[0].InclusiveDeltaPoints > 0) return "regression_increased_hotspot";
        return "improvement";
    }
}
