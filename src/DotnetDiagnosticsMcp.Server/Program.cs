using DotnetDiagnosticsMcp.Core.Symbols;
using DotnetDiagnosticsMcp.Server.Auth;
using DotnetDiagnosticsMcp.Server.Hosting;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

// --health-check (issue #27): probe-only client mode. Used by supervisor units
// (systemd ExecStartPre, Scheduled Task pre-check, container HEALTHCHECK,
// K8s readiness probe) to confirm a running instance answers /health. Exits 0
// when reachable + 200, 1 on any failure. Honours --urls (first value) to know
// which scheme/host/port to hit, defaulting to http://127.0.0.1:8787 — the
// canonical local default documented in consumer-install.md.
if (args.Contains("--health-check"))
{
    return await DotnetDiagnosticsMcp.Server.HealthCheckCommand.RunAsync(args).ConfigureAwait(false);
}

// --stdio (issue #74): per-session subprocess mode for local-dev MCP clients
// (Copilot CLI, Claude Desktop, Cursor, ...). The client spawns + owns the
// process, so every `dotnet tool update` + client reload picks up the fresh
// binary automatically — no orphan HTTP daemon, no bearer-token bookkeeping.
// The HTTP transport remains the default for sidecar / K8s scenarios where
// multiple clients share one long-running server.
if (args.Contains("--stdio"))
{
    return await RunStdioAsync(args).ConfigureAwait(false);
}

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(o =>
{
    o.IncludeScopes = true;
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss.fff ";
});

var configuredSymbolPath = Environment.GetEnvironmentVariable(SymbolPathBuilder.McpSymbolPathEnvironmentVariable);
builder.Services.AddDiagnosticCoreServices(configuredSymbolPath);
builder.Services.AddHostedService<DotnetDiagnosticsMcp.Server.Hosting.StaleBinaryWatcher>();
var orchestratorEnabled = builder.Services.AddOrchestratorServices(builder.Configuration);

// M5 (issue #164): per-IP fixed-window rate limit applied to /mcp and the proxy
// endpoints. Budget defaults come from OrchestratorOptions but the policy is
// always registered so /mcp gets the same protection even when the orchestrator
// is disabled. Excess requests are short-circuited with 429; the response body
// carries a structured envelope and a Retry-After hint.
var rateLimitOptions = new OrchestratorOptions();
builder.Configuration.GetSection("Orchestrator").Bind(rateLimitOptions);
builder.Services.AddRateLimiter(rateLimiter =>
{
    rateLimiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rateLimiter.OnRejected = static async (ctx, ct) =>
    {
        ctx.HttpContext.Response.ContentType = "application/problem+json";
        if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            ctx.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        await ctx.HttpContext.Response.WriteAsync(
            "{\"status\":429,\"kind\":\"RateLimited\",\"detail\":\"Too many requests. Slow down and retry after the Retry-After interval.\"}",
            ct).ConfigureAwait(false);
    };
    rateLimiter.AddPolicy(InvestigationProxyEndpoints.RateLimiterPolicyName, httpContext =>
    {
        var partitionKey = RateLimitPartitionKey(httpContext);
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimitOptions.RateLimitPermitsPerWindow,
                Window = TimeSpan.FromSeconds(rateLimitOptions.RateLimitWindowSeconds),
                QueueLimit = rateLimitOptions.RateLimitQueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            });
    });
});

// Hold the resolved ILoggerFactory once the app is built so the CallTool filter (configured
// before Build()) can obtain a logger lazily without sharing state with WebApplication.
ILoggerFactory? loggerFactoryHolder = null;
IServiceProvider? servicesHolder = null;

builder.Services
    .AddDiagnosticMcpServer(
        () => loggerFactoryHolder,
        enableOrchestratorTools: orchestratorEnabled,
        servicesAccessor: () => servicesHolder)
    .WithHttpTransport();

var app = builder.Build();
loggerFactoryHolder = app.Services.GetRequiredService<ILoggerFactory>();
servicesHolder = app.Services;

// H9 (issue #162): when bound to a non-loopback address, fail startup unless an
// operator-supplied bearer token is present. Generating an ephemeral token for a
// network-exposed listener leaks credentials into logs and accepts those tokens
// for the lifetime of the process. Loopback (127.0.0.1/::1/localhost) and stdio
// keep the existing ephemeral fallback for developer ergonomics.
var hasOperatorToken = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MCP_BEARER_TOKEN"));
var boundToNonLoopback = HasNonLoopbackBinding(app, builder.Configuration);
if (boundToNonLoopback && !hasOperatorToken)
{
    app.Logger.LogCritical(
        "Refusing to start: server is configured to bind to a non-loopback address but MCP_BEARER_TOKEN is not set. " +
        "Set MCP_BEARER_TOKEN to an operator-managed secret before exposing the MCP endpoint, " +
        "or restrict --urls / ASPNETCORE_URLS to loopback (http://127.0.0.1:<port>) for local development.");
    return 1;
}

var token = BearerTokenOptions.LoadOrGenerate(app.Logger, allowEphemeralFallback: !boundToNonLoopback);
app.UseMiddleware<BearerTokenMiddleware>(token);

// M5: rate limiter middleware runs after bearer-auth so 401-bound traffic still
// short-circuits cheaply and only authenticated traffic counts against the policy.
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
if (orchestratorEnabled)
{
    app.MapInvestigationProxy();
}
app.MapMcp("/mcp");

app.Run();
return 0;

static string RateLimitPartitionKey(HttpContext httpContext)
{
    // Defense-in-depth: do NOT honor X-Forwarded-For unless the host is also
    // wired up with UseForwardedHeaders + a trusted-proxy allowlist. Otherwise
    // an authenticated client can rotate the header to mint a fresh bucket
    // per request and bypass the limiter entirely. RemoteIpAddress is the
    // immediate peer that already passed any reverse-proxy hop.
    var remote = httpContext.Connection.RemoteIpAddress?.ToString();
    return string.IsNullOrEmpty(remote) ? "ip:unknown" : "ip:" + remote;
}

static async Task<int> RunStdioAsync(string[] args)
{
    var hostBuilder = Host.CreateApplicationBuilder(args);

    // Stdio uses stdout as the JSON-RPC channel — emit logs on stderr only and disable
    // all console formatting that would interleave ANSI/scope text into the wire stream.
    hostBuilder.Logging.ClearProviders();
    hostBuilder.Logging.AddSimpleConsole(o =>
    {
        o.IncludeScopes = false;
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss.fff ";
    });
    hostBuilder.Logging.Services.Configure<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>(o =>
    {
        // Route every log level to stderr so MCP JSON-RPC on stdout stays clean.
        o.LogToStandardErrorThreshold = LogLevel.Trace;
    });

    var configuredSymbolPath = Environment.GetEnvironmentVariable(SymbolPathBuilder.McpSymbolPathEnvironmentVariable);
    hostBuilder.Services.AddDiagnosticCoreServices(configuredSymbolPath);
    var orchestratorEnabled = hostBuilder.Services.AddOrchestratorServices(hostBuilder.Configuration);

    ILoggerFactory? stdioLoggerFactoryHolder = null;
    IServiceProvider? stdioServicesHolder = null;
    hostBuilder.Services
        .AddDiagnosticMcpServer(
            () => stdioLoggerFactoryHolder,
            enableOrchestratorTools: orchestratorEnabled,
            servicesAccessor: () => stdioServicesHolder)
        .WithStdioServerTransport();

    var host = hostBuilder.Build();
    stdioLoggerFactoryHolder = host.Services.GetRequiredService<ILoggerFactory>();
    stdioServicesHolder = host.Services;

    await host.RunAsync().ConfigureAwait(false);
    return 0;
}

// H9 (issue #162): inspect every place ASP.NET Core picks up a Kestrel binding —
// CLI args (--urls), env vars (ASPNETCORE_URLS / DOTNET_URLS), IConfiguration
// ("urls" key, including appsettings.json), and app.Urls (populated by launch
// profiles / explicit code) — and return true if any of them resolves to a
// non-loopback host. Returning false means the listener is either empty (test
// host / TestServer) or strictly loopback.
static bool HasNonLoopbackBinding(WebApplication app, IConfiguration configuration)
{
    var candidates = new List<string>(capacity: 8);

    if (app.Urls.Count > 0)
    {
        candidates.AddRange(app.Urls);
    }

    foreach (var key in new[] { "urls", "ASPNETCORE_URLS", "DOTNET_URLS" })
    {
        var value = configuration[key];
        if (!string.IsNullOrWhiteSpace(value))
        {
            candidates.AddRange(value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
    }

    // Kestrel:Endpoints:<name>:Url takes precedence over `urls` / ASPNETCORE_URLS,
    // so a configuration that binds to 0.0.0.0 there would otherwise sneak past the
    // loopback check. Enumerate every endpoint and add its Url to the candidate set.
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

static bool IsNonLoopbackUrl(string raw)
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

namespace DotnetDiagnosticsMcp.Server
{
    public partial class Program;
}
