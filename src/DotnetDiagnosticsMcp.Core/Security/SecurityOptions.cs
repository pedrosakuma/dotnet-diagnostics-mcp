namespace DotnetDiagnosticsMcp.Core.Security;

/// <summary>
/// Strongly-typed view of the <c>Diagnostics</c> configuration section that drives the
/// B4 data-exposure gates (issue #165). The gates are intentionally minimal — the broader
/// per-tool scope mechanism is tracked in issue #166 (B5) and will retrofit these flags
/// into a richer authorization surface later.
/// </summary>
/// <remarks>
/// Configuration keys (all under the <c>Diagnostics</c> root section, e.g. environment
/// variables prefixed <c>Diagnostics__</c>):
/// <list type="bullet">
///   <item><description><c>AllowSensitiveHeapValues</c> — bool. When false (default),
///   string previews and field value previews returned by <c>query_heap_snapshot</c>
///   are replaced with metadata-only placeholders. When true, callers may opt in per
///   call with <c>includeSensitiveValues=true</c>; values still pass through the
///   <see cref="SensitiveDataRedactor"/> pattern set.</description></item>
///   <item><description><c>EventSourceAllowlist</c> — string[]. Provider names that
///   <c>collect_event_source</c> will subscribe to without an explicit opt-in.</description></item>
///   <item><description><c>SymbolServerAllowlist</c> — string[]. Hosts that may appear
///   inside a <c>srv*https://…</c> segment of a caller-supplied <c>symbolPath</c>.</description></item>
///   <item><description><c>RedactionPatterns</c> — string[]. Additional regex patterns
///   appended to the default redaction set used by the heap drilldowns.</description></item>
/// </list>
/// </remarks>
public sealed class SecurityOptions
{
    public const string SectionName = "Diagnostics";

    /// <summary>Master switch for the <c>includeSensitiveValues=true</c> opt-in on heap
    /// drilldowns. Defaults to false so deployments inherit the safe behaviour.</summary>
    public bool AllowSensitiveHeapValues { get; set; }

    /// <summary>Allowlisted EventSource provider names. Comparisons are case-insensitive.</summary>
    public List<string> EventSourceAllowlist { get; set; } = new();

    /// <summary>Allowlisted symbol-server hosts (DNS labels, no scheme/port). Comparisons
    /// are case-insensitive.</summary>
    public List<string> SymbolServerAllowlist { get; set; } = new();

    /// <summary>Extra .NET regex patterns appended to the default redaction set. Used in
    /// addition to <see cref="SensitiveDataRedactor.DefaultPatterns"/>.</summary>
    public List<string> RedactionPatterns { get; set; } = new();
}
