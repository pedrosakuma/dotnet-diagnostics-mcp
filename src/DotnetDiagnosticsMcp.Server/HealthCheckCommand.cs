using System.Net;

namespace DotnetDiagnosticsMcp.Server;

/// <summary>
/// Probe-only CLI mode for <c>dotnet-diagnostics-mcp --health-check</c> (issue #27).
/// Exits 0 on a 2xx response from <c>/health</c>, 1 on any failure (connection refused,
/// non-2xx, timeout). Designed to be wired into supervisor units (systemd, Scheduled
/// Task, K8s readiness, container HEALTHCHECK) without dragging in <c>curl</c>.
/// </summary>
public static class HealthCheckCommand
{
    private const string DefaultUrl = "http://127.0.0.1:8787";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static Task<int> RunAsync(string[] args)
        => RunAsync(args, Console.Out, Console.Error);

    internal static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var baseUrl = ResolveBaseUrl(args);
        var url = baseUrl.TrimEnd('/') + "/health";

        using var client = new HttpClient { Timeout = Timeout };

        // The bearer middleware allow-lists /health (no Authorization required) but we
        // forward the token when present so a tightened setup that requires auth on every
        // path still succeeds. The env var matches what the server itself reads at startup.
        var bearer = Environment.GetEnvironmentVariable("MCP_BEARER_TOKEN");
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }

        try
        {
            using var response = await client.GetAsync(url).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK || (int)response.StatusCode is >= 200 and < 300)
            {
                await stdout.WriteLineAsync($"OK {url} ({(int)response.StatusCode})").ConfigureAwait(false);
                return 0;
            }
            await stderr.WriteLineAsync($"FAIL {url} (HTTP {(int)response.StatusCode})").ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync($"FAIL {url} ({ex.GetType().Name}: {ex.Message})").ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>Honours <c>--urls</c> (first value), then <c>ASPNETCORE_URLS</c>, else the
    /// canonical local default <c>http://127.0.0.1:8787</c>. If multiple URLs are configured
    /// (semicolon-separated) the first one is probed — health is a per-binding signal but the
    /// server answers <c>/health</c> on every binding so any reachable one is sufficient.</summary>
    public static string ResolveBaseUrl(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--urls", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return FirstUrl(args[i + 1]);
            }
            if (args[i].StartsWith("--urls=", StringComparison.OrdinalIgnoreCase))
            {
                return FirstUrl(args[i].Substring("--urls=".Length));
            }
        }
        var envUrl = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (!string.IsNullOrWhiteSpace(envUrl)) return FirstUrl(envUrl);
        return DefaultUrl;
    }

    private static string FirstUrl(string raw)
    {
        var first = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first)) return DefaultUrl;
        // Replace wildcard hosts with loopback for probing — the server listens on every
        // address but we only need to reach it from localhost.
        return first.Replace("://*", "://127.0.0.1", StringComparison.Ordinal)
                    .Replace("://+", "://127.0.0.1", StringComparison.Ordinal)
                    .Replace("://0.0.0.0", "://127.0.0.1", StringComparison.Ordinal);
    }
}
