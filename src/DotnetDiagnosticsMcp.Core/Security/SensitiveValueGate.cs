namespace DotnetDiagnosticsMcp.Core.Security;

/// <summary>
/// Server-wide switch deciding whether the heap drilldowns may surface raw string content
/// and field value previews on caller request. Defaults to <c>false</c>; operators flip
/// it on via <c>Diagnostics__AllowSensitiveHeapValues=true</c>. This is the B4 minimum
/// gate (issue #165); the per-tool scope mechanism from B5 (issue #166) layers a
/// principal-side opt-in on top via <see cref="ShouldEmit(bool, bool)"/>. B5.4 will
/// deprecate the bool entirely.
/// </summary>
public sealed class SensitiveValueGate
{
    private readonly bool _allowed;

    public SensitiveValueGate(SecurityOptions? options)
    {
        _allowed = options?.AllowSensitiveHeapValues ?? false;
    }

    /// <summary>True when the deployment has explicitly enabled sensitive-value emission.</summary>
    public bool IsAllowedByServer => _allowed;

    /// <summary>
    /// Legacy single-arg overload preserved for back-compat with B4 call sites; equivalent
    /// to <c>ShouldEmit(callerOptedIn, principalHasScope: false)</c>.
    /// </summary>
    public bool ShouldEmit(bool callerOptedIn) => _allowed && callerOptedIn;

    /// <summary>
    /// Resolves the effective "emit sensitive values" decision for a single tool call.
    /// Returns true when the caller explicitly asked for it AND either the server-side
    /// gate is open OR the bearer principal holds the <c>sensitive-heap-read</c>
    /// modifier scope (RFC 0001 §2.4). The principal path is additive — the legacy flag
    /// keeps working byte-for-byte until B5.4 retires it.
    /// </summary>
    public bool ShouldEmit(bool callerOptedIn, bool principalHasScope)
        => callerOptedIn && (_allowed || principalHasScope);
}
