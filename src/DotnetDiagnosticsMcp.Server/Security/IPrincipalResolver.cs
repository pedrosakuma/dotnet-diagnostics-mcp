namespace DotnetDiagnosticsMcp.Server.Security;

/// <summary>
/// Resolves a presented bearer token (the raw opaque string carried in
/// <c>Authorization: Bearer &lt;...&gt;</c>) to a <see cref="BearerPrincipal"/>, or
/// <c>null</c> when the token is unknown.
/// </summary>
/// <remarks>
/// The v1 implementation (<see cref="BearerTokenRegistry"/>) compares against a static
/// configuration table using constant-time comparison. The interface is promoted into
/// v1 — per the gpt-5.5 review of RFC 0001 — so a future JWT / OIDC variant is a
/// drop-in registration with no changes to consumers (the auth middleware in this PR,
/// the <c>[RequireScope]</c> filter in B5.2).
/// </remarks>
public interface IPrincipalResolver
{
    /// <summary>Returns the <see cref="BearerPrincipal"/> bound to
    /// <paramref name="presentedBearer"/>, or <c>null</c> when the token is unknown.
    /// Implementations must never include the presented value in log messages,
    /// exceptions, or any return value.</summary>
    BearerPrincipal? TryResolve(string presentedBearer);
}
