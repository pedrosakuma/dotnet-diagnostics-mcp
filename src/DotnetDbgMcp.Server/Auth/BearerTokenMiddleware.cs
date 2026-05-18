using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace DotnetDbgMcp.Server.Auth;

internal sealed class BearerTokenOptions
{
    public required string Token { get; init; }

    public static BearerTokenOptions LoadOrGenerate(ILogger logger)
    {
        var token = Environment.GetEnvironmentVariable("MCP_BEARER_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            token = Convert.ToHexString(bytes).ToLowerInvariant();
            logger.LogWarning(
                "MCP_BEARER_TOKEN not set. Generated ephemeral token for this run: {Token}",
                token);
        }
        else
        {
            logger.LogInformation("Bearer token loaded from MCP_BEARER_TOKEN environment variable.");
        }

        return new BearerTokenOptions { Token = token };
    }
}

internal sealed class BearerTokenMiddleware
{
    private readonly RequestDelegate _next;
    private readonly BearerTokenOptions _options;

    public BearerTokenMiddleware(RequestDelegate next, BearerTokenOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/health"))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("Authorization", out StringValues header) ||
            header.Count == 0 ||
            !TryExtractToken(header[0], out var presented) ||
            !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(presented),
                System.Text.Encoding.UTF8.GetBytes(_options.Token)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        await _next(context);
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
