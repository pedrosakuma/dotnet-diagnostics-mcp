using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace DotnetDiagnosticsMcp.Server.Hosting;

/// <summary>
/// H9/B1 startup helpers. Centralised so the non-loopback bind detection used by the
/// bearer-auth bind guard (RFC 0001 §5 + Program.cs) is unit-testable and lives in
/// one place.
/// </summary>
internal static class BindingInspector
{
    private static readonly string[] UrlConfigKeys =
        { "urls", "ASPNETCORE_URLS", "DOTNET_URLS" };

    // Port-only env keys — Kestrel binds these to wildcard interfaces (every NIC) by
    // definition. Any non-empty value means a non-loopback listener; we cannot run
    // them through IsNonLoopbackUrl because Uri.TryCreate("http://*:port") fails to
    // parse the wildcard host and would otherwise silently slip past the guard.
    private static readonly string[] PortOnlyConfigKeys =
    {
        "HTTP_PORTS", "HTTPS_PORTS",
        "ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS",
        "DOTNET_HTTP_PORTS", "DOTNET_HTTPS_PORTS",
    };

    /// <summary>Returns <c>true</c> when the host is configured to bind to any
    /// non-loopback address via <c>app.Urls</c>, the <c>urls</c> / <c>ASPNETCORE_URLS</c>
    /// / <c>DOTNET_URLS</c> keys, the port-only env keys (<c>HTTP_PORTS</c> family —
    /// always wildcard), or <c>Kestrel:Endpoints:*:Url</c>.</summary>
    public static bool HasNonLoopbackBinding(WebApplication app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(app);
        return HasNonLoopbackBinding(app.Urls, configuration);
    }

    /// <summary>Overload that takes the <c>app.Urls</c> collection directly, for unit
    /// tests that cannot construct a real <see cref="WebApplication"/>.</summary>
    public static bool HasNonLoopbackBinding(ICollection<string> appUrls, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(appUrls);
        ArgumentNullException.ThrowIfNull(configuration);

        var candidates = new List<string>(capacity: 8);

        if (appUrls.Count > 0)
        {
            candidates.AddRange(appUrls);
        }

        foreach (var key in UrlConfigKeys)
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
            {
                candidates.AddRange(value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        foreach (var key in PortOnlyConfigKeys)
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return true;
            }
        }

        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            var url = endpoint["Url"];
            if (!string.IsNullOrWhiteSpace(url))
            {
                candidates.Add(url);
            }
        }

        foreach (var raw in candidates)
        {
            if (IsNonLoopbackUrl(raw))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsNonLoopbackUrl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            return !System.Net.IPAddress.IsLoopback(ip);
        }

        // Hostname that doesn't resolve at parse time (e.g. DNS name) — treat as non-loopback.
        return true;
    }
}
