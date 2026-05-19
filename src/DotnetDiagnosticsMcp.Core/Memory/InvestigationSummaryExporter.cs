using System.Text;
using System.Text.Json;
using DotnetDiagnosticsMcp.Core.CpuSampling;

namespace DotnetDiagnosticsMcp.Core.Memory;

public enum SummaryFormat { Json, Markdown }

/// <summary>
/// Builds an <see cref="InvestigationSummary"/> from a drill-down artifact (currently CPU sample)
/// and renders it as JSON or markdown for the LLM to paste into a PR/ADR/ticket.
/// </summary>
public interface IInvestigationSummaryExporter
{
    ExportedInvestigationSummary Export(ExportRequest request);
}

public sealed record ExportRequest(
    string Handle,
    CpuSampleTraceArtifact Artifact,
    int TopHotspots = 10,
    string? BuildAssemblyName = null,
    string? PreviousInvestigationId = null,
    InvestigationFixTarget? TargetsFix = null,
    string? Notes = null,
    SummaryFormat Format = SummaryFormat.Json);

public sealed record ExportedInvestigationSummary(
    InvestigationSummary Summary,
    SummaryFormat Format,
    string Rendered);

public sealed class InvestigationSummaryExporter : IInvestigationSummaryExporter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IProvenanceCollector _provenance;
    private readonly TimeProvider _clock;
    private readonly Func<string> _idFactory;

    public InvestigationSummaryExporter(
        IProvenanceCollector provenance,
        TimeProvider? clock = null,
        Func<string>? idFactory = null)
    {
        _provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        _clock = clock ?? TimeProvider.System;
        _idFactory = idFactory ?? (() => $"inv-{Guid.NewGuid():N}");
    }

    public ExportedInvestigationSummary Export(ExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Artifact);
        if (request.TopHotspots < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "TopHotspots must be >= 1.");
        }

        var artifact = request.Artifact;
        var total = artifact.TotalSamples == 0 ? 1 : artifact.TotalSamples; // guard div-by-zero
        var hotspots = FlattenTree(artifact.Root)
            .Where(n => !string.Equals(n.Frame.Method, "<root>", StringComparison.Ordinal))
            .Where(n => n.ExclusiveSamples > 0 || n.InclusiveSamples > 0)
            .GroupBy(n => new SymbolRef(n.Frame.Module, n.Frame.Method))
            .Select(g => new
            {
                Symbol = g.Key,
                Exclusive = g.Sum(n => n.ExclusiveSamples),
                Inclusive = g.Max(n => n.InclusiveSamples),
            })
            .OrderByDescending(g => g.Exclusive)
            .ThenByDescending(g => g.Inclusive)
            .Take(request.TopHotspots)
            .Select(g => new HotspotSummary(
                Symbol: g.Symbol,
                InclusiveSamples: g.Inclusive,
                ExclusiveSamples: g.Exclusive,
                InclusivePercent: Math.Round(100.0 * g.Inclusive / total, 2),
                ExclusivePercent: Math.Round(100.0 * g.Exclusive / total, 2)))
            .ToArray();

        var findings = new InvestigationFindings(
            TotalSamples: artifact.TotalSamples,
            StartedAt: artifact.StartedAt,
            Duration: artifact.Duration,
            TopHotspots: hotspots);

        var summary = new InvestigationSummary(
            Schema: InvestigationSummary.SchemaV1,
            InvestigationId: _idFactory(),
            CreatedAt: _clock.GetUtcNow(),
            ProcessId: artifact.ProcessId,
            Provenance: _provenance.Collect(artifact.ProcessId, request.BuildAssemblyName),
            Findings: findings,
            PreviousInvestigationId: request.PreviousInvestigationId,
            TargetsFix: request.TargetsFix,
            Notes: request.Notes);

        var rendered = request.Format switch
        {
            SummaryFormat.Markdown => RenderMarkdown(summary, request.Handle),
            _ => JsonSerializer.Serialize(summary, JsonOpts),
        };

        return new ExportedInvestigationSummary(summary, request.Format, rendered);
    }

    private static IEnumerable<CallTreeNode> FlattenTree(CallTreeNode root)
    {
        var stack = new Stack<CallTreeNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            foreach (var child in n.Children) stack.Push(child);
        }
    }

    private static string RenderMarkdown(InvestigationSummary s, string handle)
    {
        var sb = new StringBuilder();
        sb.Append("# Investigation `").Append(s.InvestigationId).AppendLine("`");
        sb.Append("- Created: `").Append(s.CreatedAt.ToString("u")).AppendLine("`");
        sb.Append("- PID: `").Append(s.ProcessId).Append("` · Source handle: `").Append(handle).AppendLine("`");
        if (s.PreviousInvestigationId is not null)
        {
            sb.Append("- Previous: `").Append(s.PreviousInvestigationId).AppendLine("`");
        }
        sb.AppendLine();

        sb.AppendLine("## Provenance");
        if (s.Provenance.Build is { } b)
        {
            sb.Append("- Build: `").Append(b.AssemblyName ?? "?").Append('`');
            if (b.InformationalVersion is not null) sb.Append(" · v`").Append(b.InformationalVersion).Append('`');
            if (b.GitSha is not null) sb.Append(" · git `").Append(b.GitSha).Append('`');
            sb.AppendLine();
        }
        if (s.Provenance.Container is { } c)
        {
            sb.Append("- Container: image=`").Append(c.Image ?? "?")
              .Append("` ns=`").Append(c.Namespace ?? "?")
              .Append("` pod=`").Append(c.PodName ?? "?")
              .Append("` node=`").Append(c.NodeName ?? "?").AppendLine("`");
        }
        if (s.Provenance.Hostname is not null) sb.Append("- Host: `").Append(s.Provenance.Hostname).AppendLine("`");
        sb.AppendLine();

        sb.AppendLine("## Findings");
        var f = s.Findings;
        sb.Append("- Samples: `").Append(f.TotalSamples).Append("` over `").Append(f.Duration.TotalSeconds).AppendLine("s`");
        sb.AppendLine();
        sb.AppendLine("| # | Method | Module | Incl % | Excl % |");
        sb.AppendLine("|---|---|---|---:|---:|");
        var i = 1;
        foreach (var h in f.TopHotspots)
        {
            sb.Append("| ").Append(i++).Append(" | `").Append(h.Symbol.MethodFullName)
              .Append("` | `").Append(h.Symbol.Module).Append("` | ")
              .Append(h.InclusivePercent).Append(" | ")
              .Append(h.ExclusivePercent).AppendLine(" |");
        }
        sb.AppendLine();

        if (s.TargetsFix is { } fix)
        {
            sb.AppendLine("## Targets Fix");
            if (fix.PullRequestUrl is not null) sb.Append("- PR: ").AppendLine(fix.PullRequestUrl);
            if (fix.CommitSha is not null) sb.Append("- Commit: `").Append(fix.CommitSha).AppendLine("`");
            if (fix.Description is not null) sb.AppendLine(fix.Description);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(s.Notes))
        {
            sb.AppendLine("## Notes").AppendLine(s.Notes);
        }

        return sb.ToString();
    }
}
