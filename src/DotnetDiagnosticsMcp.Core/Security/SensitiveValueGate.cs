namespace DotnetDiagnosticsMcp.Core.Security;

/// <summary>
/// Server-wide switch deciding whether the heap drilldowns may surface raw string content
/// and field value previews on caller request. Defaults to <c>false</c>; operators flip
/// it on via <c>Diagnostics__AllowSensitiveHeapValues=true</c>. This is the B4 minimum
/// gate (issue #165); the per-tool scope mechanism from B5 (issue #166) will replace
/// the bool with a richer authorization surface in a follow-up.
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
    /// Resolves the effective "emit sensitive values" decision for a single tool call.
    /// Returns true only when both the server-side gate is open <em>and</em> the caller
    /// explicitly asked for it.
    /// </summary>
    public bool ShouldEmit(bool callerOptedIn) => _allowed && callerOptedIn;
}
