using System.Collections.Immutable;

namespace DotnetDiagnosticsMcp.Server.Security;

/// <summary>
/// Declares the per-tool authorization scopes required to invoke a method decorated with
/// <c>[ModelContextProtocol.Server.McpServerTool]</c>. RFC 0001 §2 taxonomy is enforced by
/// the <see cref="ToolScopeAuthorizationFilter"/> at MCP CallTool dispatch time.
/// </summary>
/// <remarks>
/// <para>Semantics: <b>AND</b>. <c>[RequireScope("ptrace", "dump-write")]</c> means the
/// principal must hold <i>every</i> listed scope (any wildcard / root scope satisfies
/// every entry — see <see cref="BearerPrincipal.HasScope"/>).</para>
/// <para>Stacking example: <c>collect_process_dump</c> stacks <c>ptrace</c> and
/// <c>dump-write</c>; <c>inspect_live_heap</c> stacks <c>heap-read</c> and <c>ptrace</c>.</para>
/// <para>For tools whose handle can be minted under multiple originating scopes (e.g.
/// <c>query_collection</c> reads handles from both <c>read-counters</c> and
/// <c>eventpipe</c> collectors per RFC §2.12), use <see cref="RequireAnyScopeAttribute"/>
/// instead.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireScopeAttribute : Attribute
{
    public RequireScopeAttribute(params string[] scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        if (scopes.Length == 0)
        {
            throw new ArgumentException("At least one scope must be supplied.", nameof(scopes));
        }
        foreach (var s in scopes)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                throw new ArgumentException("Scope strings must be non-empty.", nameof(scopes));
            }
        }

        Scopes = ImmutableArray.Create(scopes);
    }

    /// <summary>Required scopes; the caller must hold every entry.</summary>
    public ImmutableArray<string> Scopes { get; }
}

/// <summary>
/// Like <see cref="RequireScopeAttribute"/> but with <b>OR</b> semantics: the principal
/// satisfies the gate iff it holds <i>at least one</i> listed scope. Used by drilldown
/// tools whose backing handle could have been minted by collectors with different
/// scopes (RFC §2.12 handle-ownership rule, simplified for B5.2 where the handle store
/// does not yet record per-handle <c>RequiredScopes</c>).
/// </summary>
/// <remarks>
/// Example: <c>query_collection</c> drills into a handle minted by either
/// <c>snapshot_counters</c> (<c>read-counters</c>) or any of the <c>collect_*</c>
/// EventPipe tools (<c>eventpipe</c>). The attribute lists both scopes; the call is
/// authorized when the principal holds either. A future PR (tracked under §2.12 of the
/// RFC) will tighten this to an exact per-handle <c>RequiredScopes</c> check.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireAnyScopeAttribute : Attribute
{
    public RequireAnyScopeAttribute(params string[] scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        if (scopes.Length == 0)
        {
            throw new ArgumentException("At least one scope must be supplied.", nameof(scopes));
        }
        foreach (var s in scopes)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                throw new ArgumentException("Scope strings must be non-empty.", nameof(scopes));
            }
        }

        Scopes = ImmutableArray.Create(scopes);
    }

    /// <summary>Candidate scopes; the caller must hold at least one entry.</summary>
    public ImmutableArray<string> Scopes { get; }
}
