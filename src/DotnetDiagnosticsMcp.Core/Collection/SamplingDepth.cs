namespace DotnetDiagnosticsMcp.Core.Collection;

/// <summary>
/// Tiered verbosity selector for collection tools, used as the uniform <c>depth</c> parameter
/// across every collector (Issue #41, slice 2c). Default is <see cref="Summary"/>: the LLM
/// gets the smallest payload that still answers "what should I do next?", drills in with
/// <see cref="Detail"/> once it has a hypothesis, and reaches for <see cref="Raw"/> only when
/// it needs everything inline (typically right before exporting an investigation summary).
/// </summary>
/// <remarks>
/// Concrete semantics per tool live in <see cref="SamplingDepthExtensions"/> and the tool
/// methods themselves. The general rule is:
/// <list type="bullet">
///   <item><description><see cref="Summary"/> — headline-only payload: enough for the LLM to
///     decide the next step (typically &lt; 1 KB JSON). Per-frame / per-event detail is omitted
///     or collapsed; the response still carries the handle so a follow-up can fetch the full
///     artifact without re-running the collection.</description></item>
///   <item><description><see cref="Detail"/> — the historical default response shape
///     (bounded top-N hotspots / recent events with full structure). Matches what consumers
///     received before this parameter existed; provided as the "no surprises" middle ground.</description></item>
///   <item><description><see cref="Raw"/> — inlines material that would otherwise require a
///     drilldown query: full stacks, per-thread rollups, per-event records. Caps still apply
///     (the handle store is the only unbounded surface) but they are 2–4× larger than
///     <see cref="Detail"/>.</description></item>
/// </list>
/// </remarks>
public enum SamplingDepth
{
    /// <summary>Headline-only payload. The LLM's default first step.</summary>
    Summary = 0,
    /// <summary>Top-N bounded detail. Matches pre-#41 default response shape.</summary>
    Detail = 1,
    /// <summary>Inline everything the handle store would otherwise hold, within safety caps.</summary>
    Raw = 2,
}

/// <summary>Helpers for translating <see cref="SamplingDepth"/> into per-tool bounds.</summary>
public static class SamplingDepthExtensions
{
    /// <summary>
    /// Returns the effective top-N for a ranked list (hotspots, blocking stacks, recent events)
    /// at the given depth. An explicit user-supplied <paramref name="explicitTopN"/> always wins
    /// (the LLM knows what it asked for); when omitted, the depth's default applies.
    /// </summary>
    /// <param name="depth">Requested depth.</param>
    /// <param name="explicitTopN">User-supplied <c>topN</c>, or <c>null</c>/<c>&lt;=0</c> when the caller didn't supply one.</param>
    /// <param name="summaryDefault">Top-N for <see cref="SamplingDepth.Summary"/> (e.g. 3 hotspots).</param>
    /// <param name="detailDefault">Top-N for <see cref="SamplingDepth.Detail"/> (historical default).</param>
    /// <param name="rawDefault">Top-N for <see cref="SamplingDepth.Raw"/>. Hard ceiling on the artifact-size axis.</param>
    public static int EffectiveTopN(this SamplingDepth depth, int? explicitTopN, int summaryDefault, int detailDefault, int rawDefault)
    {
        if (explicitTopN is > 0) return explicitTopN.Value;
        return depth switch
        {
            SamplingDepth.Summary => summaryDefault,
            SamplingDepth.Detail => detailDefault,
            SamplingDepth.Raw => rawDefault,
            _ => detailDefault,
        };
    }

    /// <summary>True when the LLM asked for at least <see cref="SamplingDepth.Detail"/> verbosity.</summary>
    public static bool IncludesDetail(this SamplingDepth depth) => depth >= SamplingDepth.Detail;

    /// <summary>True when the LLM asked for <see cref="SamplingDepth.Raw"/> verbosity — collectors should inline data normally reachable only via a drilldown handle.</summary>
    public static bool IncludesRaw(this SamplingDepth depth) => depth == SamplingDepth.Raw;
}
