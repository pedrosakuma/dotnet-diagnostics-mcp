using Microsoft.AspNetCore.Http;

namespace DotnetDiagnosticsMcp.Server.Security;

/// <summary>
/// Resolves the <see cref="BearerPrincipal"/> active for the current call, abstracting
/// over the HTTP transport (where the principal is stamped on
/// <see cref="HttpContext.Items"/> by <c>BearerTokenMiddleware</c>) and the stdio
/// transport (which has no HTTP context — the local client owns the process, so
/// authorization degrades to "root scope" per RFC 0001 §5).
/// </summary>
public interface IPrincipalAccessor
{
    /// <summary>The principal for the current call, or <c>null</c> when no principal
    /// can be resolved. Implementations must never log or echo bearer values.</summary>
    BearerPrincipal? Current { get; }
}

/// <summary>HTTP-transport implementation: reads the principal stamped by
/// <c>BearerTokenMiddleware</c> off <see cref="HttpContext.Items"/>.</summary>
internal sealed class HttpContextPrincipalAccessor : IPrincipalAccessor
{
    private readonly IHttpContextAccessor _accessor;

    public HttpContextPrincipalAccessor(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    public BearerPrincipal? Current => _accessor.HttpContext?.GetBearerPrincipal();
}

/// <summary>Stdio-transport implementation: returns a synthetic root principal so every
/// <c>[RequireScope]</c>-gated tool remains callable. The local MCP client owns the
/// process lifecycle — there is no transport-level identity to project (RFC 0001 §5).</summary>
internal sealed class StdioRootPrincipalAccessor : IPrincipalAccessor
{
    public static readonly StdioRootPrincipalAccessor Instance = new();

    private static readonly BearerPrincipal RootPrincipal = new(
        name: "stdio-root",
        scopes: System.Collections.Immutable.ImmutableHashSet.Create(BearerPrincipal.RootScope));

    public BearerPrincipal? Current => RootPrincipal;
}
