using DotnetDiagnosticsMcp.Core.Symbols;
using DotnetDiagnosticsMcp.Server.Auth;
using DotnetDiagnosticsMcp.Server.Hosting;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using DotnetDiagnosticsMcp.Server.Security;
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
var oidcJwtAuth = builder.AddOidcJwtAuth();

builder.Logging.AddSimpleConsole(o =>
{
    o.IncludeScopes = true;
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss.fff ";
});

var configuredSymbolPath = Environment.GetEnvironmentVariable(SymbolPathBuilder.McpSymbolPathEnvironmentVariable);
builder.Services.AddDiagnosticCoreServices(configuredSymbolPath, builder.Configuration);
builder.Services.AddHostedService<DotnetDiagnosticsMcp.Server.Hosting.StaleBinaryWatcher>();
var orchestratorEnabled = builder.Services.AddOrchestratorServices(builder.Configuration);
builder.AddOrchestratorObservability(orchestratorEnabled);

// B5.2 (RFC 0001 §5): the [RequireScope] filter reads the bearer principal off
// HttpContext.Items. Register the typed accessor so the filter is decoupled from
// IHttpContextAccessor directly, easing the stdio fallback and unit-test isolation.
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<DotnetDiagnosticsMcp.Server.Security.IPrincipalAccessor,
    DotnetDiagnosticsMcp.Server.Security.HttpContextPrincipalAccessor>();

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

// B5.1 / RFC 0001 §5 + §7: scoped bearer auth replaces the previous single-bearer
// path. The registry is constructed before the app starts handling requests so any
// validation error (duplicate token, empty scope set, missing legacy token on a
// non-loopback bind) surfaces as a startup failure with a clear log line — never a
// per-request 500. Loopback / stdio keep their ephemeral-fallback ergonomics; the
// H9/B1 non-loopback bind guard moves inside BearerTokenRegistry.Build so it stays
// authoritative for both shapes (scoped + legacy).
var hasScopedTokens = builder.Configuration.GetSection("Auth:BearerTokens").Exists();
var hasLegacyToken = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MCP_BEARER_TOKEN"));
var boundToNonLoopback = BindingInspector.HasNonLoopbackBinding(app, builder.Configuration);

if (boundToNonLoopback && !hasScopedTokens && !hasLegacyToken && !oidcJwtAuth.IsEnabled)
{
    app.Logger.LogCritical(
        "Refusing to start: server is configured to bind to a non-loopback address but " +
        "neither Auth:BearerTokens, MCP_BEARER_TOKEN, nor MCP_OIDC_ISSUER/MCP_OIDC_AUDIENCE is set. " +
        "Configure Auth:BearerTokens (preferred for opaque tokens), set MCP_BEARER_TOKEN, or enable OIDC/JWT validation " +
        "with MCP_OIDC_ISSUER + MCP_OIDC_AUDIENCE before exposing the MCP endpoint, " +
        "or restrict --urls / ASPNETCORE_URLS to loopback (http://127.0.0.1:<port>) for local development.");
    return 1;
}

if (boundToNonLoopback && !hasScopedTokens && hasLegacyToken)
{
    // RFC 0001 §7.1 v1 transition: legacy var is still accepted on non-loopback binds
    // but operators are nudged toward Auth:BearerTokens before v2 removes the fallback.
    app.Logger.LogWarning(
        "MCP_BEARER_TOKEN is set without Auth:BearerTokens; the legacy variable resolves to root scope " +
        "and is deprecated for non-loopback deployments. See RFC 0001 (docs/rfcs/0001-per-tool-authorization-scopes.md).");
}

BearerTokenRegistry registry;
if (oidcJwtAuth.IsEnabled && !hasScopedTokens && !hasLegacyToken)
{
    registry = BearerTokenRegistry.Empty;
    app.Logger.LogInformation(
        "OIDC/JWT auth enabled without any opaque bearer tokens; JWT validation is active and opaque bearer values will be rejected.");
}
else
{
    try
    {
        registry = BearerTokenRegistry.Build(
            builder.Configuration,
            app.Logger,
            allowEphemeralFallback: !boundToNonLoopback);
    }
    catch (InvalidOperationException ex)
    {
        app.Logger.LogCritical(ex, "Bearer auth registry failed to initialise.");
        return 1;
    }
}

// Singleton resolver — keeps the JWT/OIDC swap path (RFC 0001 §3.3) a one-line DI
// change. We pass it positionally to UseMiddleware because the registry is built
// post-app.Build (it needs the resolved logger + final config) and DI is locked by
// then; UseMiddleware<T>(args) matches the registry by constructor parameter type.
app.UseMiddleware<BearerTokenMiddleware>((IPrincipalResolver)registry);

// M5: rate limiter middleware runs after bearer-auth so 401-bound traffic still
// short-circuits cheaply and only authenticated traffic counts against the policy.
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapOrchestratorObservability();
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
    hostBuilder.Services.AddDiagnosticCoreServices(configuredSymbolPath, hostBuilder.Configuration);
    var orchestratorEnabled = hostBuilder.Services.AddOrchestratorServices(hostBuilder.Configuration);
    hostBuilder.AddOrchestratorObservability(orchestratorEnabled);

    // B5.2 / RFC 0001 §5: stdio has no HTTP context — the local MCP client owns the
    // process so authorization degrades to root scope. Registering the stdio accessor
    // here keeps the [RequireScope] filter graceful across transports without each
    // tool body branching on transport kind.
    hostBuilder.Services.AddSingleton<DotnetDiagnosticsMcp.Server.Security.IPrincipalAccessor>(
        DotnetDiagnosticsMcp.Server.Security.StdioRootPrincipalAccessor.Instance);

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

// H9 (issue #162) bind detection lives in DotnetDiagnosticsMcp.Server.Hosting.BindingInspector
// (factored out for unit-test coverage of the port-only env keys).

namespace DotnetDiagnosticsMcp.Server
{
    public partial class Program;
}
