using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Server.Security;

/// <summary>
/// Emits a once-per-process <c>Warning</c>-level log the first time each of the legacy
/// B4 diagnostics gates is the mechanism that unlocks a sensitive operation while the
/// matching RFC 0001 modifier scope is absent on the bearer principal. Lets operators
/// see — without spam — that they are still depending on a deployment-wide flag /
/// allowlist for caller-level distinction that is now better expressed with a scoped
/// bearer token.
/// </summary>
/// <remarks>
/// <para>
/// Scope-first semantics (B5.4 / RFC 0001 §7.3): the predicate the call site evaluates
/// is now <c>principal.HasExplicitScope("&lt;scope&gt;") OR &lt;legacy-flag-or-allowlist-allows&gt;</c>.
/// Functionally identical to the B5.2 "either path is sufficient" behaviour; this class
/// only adds the deprecation telemetry on the legacy branch.
/// </para>
/// <para>
/// The <c>AllowSensitiveHeapValues</c> boolean is the only flag truly going away in a
/// future release. The <c>EventSourceAllowlist</c> and <c>SymbolServerAllowlist</c>
/// policies are <b>retained</b> as fallback value-shaping policies for tokens lacking
/// the modifier scope (RFC 0001 §2.3 / §2.5). What the warnings deprecate is the
/// pattern of relying on a single deployment-wide setting to mean "every caller can
/// do X" — that responsibility moves to scoped bearer tokens.
/// </para>
/// </remarks>
public sealed class LegacyDiagnosticsFlagDeprecation
{
    /// <summary>Public for assertions; kept verbatim in tests so any wording drift is caught.</summary>
    public const string SensitiveHeapValuesWarning =
        "Diagnostics:AllowSensitiveHeapValues is deprecated. Grant the 'sensitive-heap-read' scope to the operator token instead. The flag will be removed in a future release.";

    /// <summary>Public for assertions; kept verbatim in tests so any wording drift is caught.</summary>
    public const string EventSourceAllowlistWarning =
        "Diagnostics:EventSourceAllowlist is bypassed by the 'eventsource-any' scope; configure scoped tokens instead of relying on the allowlist alone for caller-level distinction. The allowlist policy itself is retained.";

    /// <summary>Public for assertions; kept verbatim in tests so any wording drift is caught.</summary>
    public const string SymbolServerAllowlistWarning =
        "Diagnostics:SymbolServerAllowlist is bypassed by the 'symbols-remote' scope; configure scoped tokens instead of relying on the allowlist alone for caller-level distinction. The allowlist policy itself is retained.";

    private readonly ILogger<LegacyDiagnosticsFlagDeprecation> _logger;
    private int _sensitiveHeapWarned;
    private int _eventSourceAllowlistWarned;
    private int _symbolServerAllowlistWarned;

    public LegacyDiagnosticsFlagDeprecation(ILogger<LegacyDiagnosticsFlagDeprecation>? logger = null)
    {
        _logger = logger ?? NullLogger<LegacyDiagnosticsFlagDeprecation>.Instance;
    }

    /// <summary>
    /// Records that the <c>Diagnostics:AllowSensitiveHeapValues</c> flag was the path
    /// that unlocked a sensitive emission (heap drilldown value preview or
    /// <c>collect_event_source unsafeProvider</c>) for a principal that did NOT hold
    /// the <c>sensitive-heap-read</c> scope. Emits the deprecation warning exactly
    /// once per process.
    /// </summary>
    public void NotifySensitiveHeapValuesFlagBypass()
    {
        if (Interlocked.Exchange(ref _sensitiveHeapWarned, 1) == 0)
        {
            _logger.LogWarning(SensitiveHeapValuesWarning);
        }
    }

    /// <summary>
    /// Records that <c>collect_event_source</c> accepted a provider name via the
    /// curated allowlist (default or configured) for a principal that did NOT hold
    /// the <c>eventsource-any</c> scope. Emits the deprecation warning exactly once
    /// per process. The allowlist policy itself is retained.
    /// </summary>
    public void NotifyEventSourceAllowlistBypass()
    {
        if (Interlocked.Exchange(ref _eventSourceAllowlistWarned, 1) == 0)
        {
            _logger.LogWarning(EventSourceAllowlistWarning);
        }
    }

    /// <summary>
    /// Records that a caller-supplied <c>symbolPath</c> with a remote
    /// <c>srv*http(s)://…</c> segment was accepted via the
    /// <c>Diagnostics:SymbolServerAllowlist</c> host allowlist for a principal that
    /// did NOT hold the <c>symbols-remote</c> scope. Emits the deprecation warning
    /// exactly once per process. The allowlist policy itself is retained.
    /// </summary>
    public void NotifySymbolServerAllowlistBypass()
    {
        if (Interlocked.Exchange(ref _symbolServerAllowlistWarned, 1) == 0)
        {
            _logger.LogWarning(SymbolServerAllowlistWarning);
        }
    }
}
