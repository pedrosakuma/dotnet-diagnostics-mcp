using DotnetDiagnosticsMcp.Server.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace DotnetDiagnosticsMcp.Server.Auth;

/// <summary>
/// Scoped bearer-token auth middleware (RFC 0001 §3 / §5). Replaces the previous
/// single-bearer string-compare with an <see cref="IPrincipalResolver"/> lookup and
/// stamps the resolved <see cref="BearerPrincipal"/> on
/// <see cref="HttpContext.Items"/> so the upcoming <c>[RequireScope]</c> filter
/// (B5.2) can authorize per-tool.
/// </summary>
/// <remarks>
/// <para><c>/health</c> is allow-listed (same as before) so K8s/Docker probes do not
/// need a token.</para>
/// <para>The 401 response body is a structured JSON envelope (per task spec). The
/// presented bearer value is never echoed back, logged, or included in any audit
/// record — only the remote IP and whether the header was missing.</para>
/// </remarks>
internal sealed class BearerTokenMiddleware
{
    private const string UnauthenticatedEnvelope =
        "{\"error\":{\"kind\":\"unauthenticated\",\"message\":\"invalid bearer token\"}}";

    private readonly RequestDelegate _next;
    private readonly IPrincipalResolver _resolver;
    private readonly ILogger<BearerTokenMiddleware> _logger;

    public BearerTokenMiddleware(
        RequestDelegate next,
        IPrincipalResolver resolver,
        ILogger<BearerTokenMiddleware> logger)
    {
        _next = next;
        _resolver = resolver;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/health"))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (!context.Request.Headers.TryGetValue("Authorization", out StringValues header) ||
            header.Count == 0 ||
            !TryExtractToken(header[0], out var presented))
        {
            _logger.LogWarning(
                "Bearer auth denied: missing or malformed Authorization header. remoteIp={RemoteIp} missingHeader=true",
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            await WriteUnauthorizedAsync(context).ConfigureAwait(false);
            return;
        }

        var principal = _resolver.TryResolve(presented);
        if (principal is null)
        {
            _logger.LogWarning(
                "Bearer auth denied: presented token did not match any registered entry. remoteIp={RemoteIp} missingHeader=false",
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            await WriteUnauthorizedAsync(context).ConfigureAwait(false);
            return;
        }

        context.SetBearerPrincipal(principal);
        _logger.LogInformation("Bearer auth allowed for principal {TokenName}.", principal.Name);

        await _next(context).ConfigureAwait(false);
    }

    private static async Task WriteUnauthorizedAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(UnauthenticatedEnvelope).ConfigureAwait(false);
    }

    private static bool TryExtractToken(string? header, out string token)
    {
        token = string.Empty;
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = header[prefix.Length..].Trim();
        return token.Length > 0;
    }
}
