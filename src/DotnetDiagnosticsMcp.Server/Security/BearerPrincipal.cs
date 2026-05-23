using System.Collections.Immutable;

namespace DotnetDiagnosticsMcp.Server.Security;

/// <summary>
/// Identity stamped on <see cref="Microsoft.AspNetCore.Http.HttpContext"/> after a
/// successful bearer-token authentication. Carries only the human-readable token
/// <see cref="Name"/> (safe to log) and the granted <see cref="Scopes"/> — never the
/// presented bearer value.
/// </summary>
/// <remarks>
/// Foundational type for RFC 0001 (per-tool authorization scopes). B5.2 will consume
/// this via <c>[RequireScope]</c>; this PR (B5.1) only ensures the principal is
/// available so downstream filters have something to call <see cref="HasScope"/> on.
/// The root <see cref="RootScope"/> wildcard is honoured here so consumers never need
/// to special-case it.
/// </remarks>
public sealed class BearerPrincipal
{
    /// <summary>The wildcard scope: a principal granted this single scope is treated as
    /// holding every scope. Both <c>"root"</c> (task-spec spelling used by the legacy
    /// synthesized principal) and <c>"*"</c> (RFC 0001 §2.13 spelling used in
    /// <c>Auth:BearerTokens</c> config examples) are accepted as wildcards via
    /// <see cref="IsWildcard"/>.</summary>
    public const string RootScope = "root";

    /// <summary>Alternative spelling of the wildcard scope per RFC 0001 §2.13.</summary>
    public const string RootScopeAlt = "*";

    /// <summary>Synthetic token name attached to the legacy <c>MCP_BEARER_TOKEN</c> path
    /// and to the loopback ephemeral fallback. Documented so audit logs are
    /// self-explanatory.</summary>
    public const string LegacyRootName = "legacy-root";

    private static bool IsWildcard(string scope) =>
        scope.Equals(RootScope, StringComparison.Ordinal) ||
        scope.Equals(RootScopeAlt, StringComparison.Ordinal);

    public BearerPrincipal(string name, ImmutableHashSet<string> scopes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(scopes);
        if (scopes.Count == 0)
        {
            throw new ArgumentException("Bearer principal must carry at least one scope.", nameof(scopes));
        }

        Name = name;
        Scopes = scopes;
        _hasWildcard = scopes.Any(IsWildcard);
    }

    private readonly bool _hasWildcard;

    public string Name { get; }

    public ImmutableHashSet<string> Scopes { get; }

    /// <summary>Returns <c>true</c> when the principal holds <paramref name="scope"/> or
    /// any wildcard (<c>"root"</c> / <c>"*"</c>). B5.2 callers should always go through
    /// this method rather than poking <see cref="Scopes"/> directly so the wildcard
    /// semantic stays in one place.</summary>
    public bool HasScope(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return _hasWildcard || Scopes.Contains(scope);
    }

    /// <summary>Like <see cref="HasScope"/> but does NOT honour the root/wildcard
    /// shortcut — only literal membership in <see cref="Scopes"/> counts. Used for
    /// the modifier scopes in RFC 0001 §2.3-§2.7 (<c>sensitive-heap-read</c>,
    /// <c>eventsource-any</c>, <c>symbols-remote</c>, <c>orchestrator-admin</c>),
    /// which are deliberately additive — operators must explicitly mint a bearer
    /// with the modifier scope rather than getting it for free from a root token.
    /// This preserves the principle of least surprise for the SSRF / sensitive-data
    /// allowlists: a "do everything" token still respects the deployment-wide
    /// gates unless the operator deliberately layers the modifier scope on top.</summary>
    public bool HasExplicitScope(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return Scopes.Contains(scope);
    }
}
