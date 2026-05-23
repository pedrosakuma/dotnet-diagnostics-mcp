using DotnetDiagnosticsMcp.Server.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace DotnetDiagnosticsMcp.Server.Auth;

/// <summary>
/// HTTP auth middleware for both opaque bearer tokens and OIDC-backed JWTs. Opaque
/// tokens continue to resolve through <see cref="IPrincipalResolver"/>; JWT-shaped
/// bearer values are validated through ASP.NET Core's <c>JwtBearerHandler</c> when
/// OIDC is configured.
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
    private readonly OidcJwtAuthOptions _oidcJwtAuthOptions;
    private readonly ILogger<BearerTokenMiddleware> _logger;
    private readonly DotnetDiagnosticsMcp.Server.Hosting.OrchestratorObservabilityOptions _observabilityOptions;

    public BearerTokenMiddleware(
        RequestDelegate next,
        IPrincipalResolver resolver,
        OidcJwtAuthOptions oidcJwtAuthOptions,
        ILogger<BearerTokenMiddleware> logger,
        DotnetDiagnosticsMcp.Server.Hosting.OrchestratorObservabilityOptions observabilityOptions)
    {
        _next = next;
        _resolver = resolver;
        _oidcJwtAuthOptions = oidcJwtAuthOptions;
        _logger = logger;
        _observabilityOptions = observabilityOptions;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/health") ||
            path.StartsWithSegments("/metrics") && (_observabilityOptions.MetricsOpen || !_observabilityOptions.MetricsEnabled))
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

        if (_oidcJwtAuthOptions.IsEnabled && LooksLikeJwt(presented))
        {
            var authenticationService = context.RequestServices.GetRequiredService<IAuthenticationService>();
            var authResult = await authenticationService
                .AuthenticateAsync(context, OidcJwtAuthExtensions.JwtScheme)
                .ConfigureAwait(false);

            if (!authResult.Succeeded || authResult.Principal is null)
            {
                _logger.LogWarning(
                    "Bearer auth denied: JWT validation failed. remoteIp={RemoteIp} missingHeader=false",
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                await WriteUnauthorizedAsync(context).ConfigureAwait(false);
                return;
            }

            context.User = authResult.Principal;
            var jwtPrincipal = context.GetBearerPrincipal();
            if (jwtPrincipal is null)
            {
                _logger.LogWarning(
                    "Bearer auth denied: JWT validated but principal mapping was unavailable. remoteIp={RemoteIp} missingHeader=false",
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                await WriteUnauthorizedAsync(context).ConfigureAwait(false);
                return;
            }

            _logger.LogInformation("Bearer auth allowed for principal {TokenName}.", jwtPrincipal.Name);
            await _next(context).ConfigureAwait(false);
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

    private static bool LooksLikeJwt(string token)
    {
        var firstDot = token.IndexOf('.');
        if (firstDot <= 0)
        {
            return false;
        }

        var secondDot = token.IndexOf('.', firstDot + 1);
        if (secondDot <= firstDot + 1 || secondDot >= token.Length - 1)
        {
            return false;
        }

        return IsJwtSegment(token.AsSpan(0, firstDot)) &&
            IsJwtSegment(token.AsSpan(firstDot + 1, secondDot - firstDot - 1)) &&
            IsJwtSegment(token.AsSpan(secondDot + 1));
    }

    private static bool IsJwtSegment(ReadOnlySpan<char> segment)
    {
        foreach (var c in segment)
        {
            if ((c >= 'A' && c <= 'Z') ||
                (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') ||
                c == '-' ||
                c == '_')
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
