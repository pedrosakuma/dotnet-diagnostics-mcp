using Microsoft.AspNetCore.Http;

namespace DotnetDiagnosticsMcp.Server.Security;

/// <summary>
/// Typed accessor for the <see cref="BearerPrincipal"/> stamped onto
/// <see cref="HttpContext.Items"/> by <c>ScopedAuthMiddleware</c>. Centralised so the
/// item key — and the contract that a missing principal means "no auth ran on this
/// pipeline" — lives in one place.
/// </summary>
internal static class BearerPrincipalContext
{
    internal const string ItemKey = "BearerPrincipal";

    public static void SetBearerPrincipal(this HttpContext context, BearerPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(principal);
        context.Items[ItemKey] = principal;
    }

    /// <summary>Returns the authenticated principal or <c>null</c> when the request did
    /// not go through the bearer middleware (e.g. <c>/health</c>).</summary>
    public static BearerPrincipal? GetBearerPrincipal(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ItemKey, out var raw) ? raw as BearerPrincipal : null;
    }
}
