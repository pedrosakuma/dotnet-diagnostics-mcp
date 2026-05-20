using DotnetDiagnosticsMcp.Server.Auth;
using DotnetDiagnosticsMcp.Server.Hosting;

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

builder.Services.AddDiagnosticCoreServices();
builder.Services.AddHostedService<DotnetDiagnosticsMcp.Server.Hosting.StaleBinaryWatcher>();

// Hold the resolved ILoggerFactory once the app is built so the CallTool filter (configured
// before Build()) can obtain a logger lazily without sharing state with WebApplication.
ILoggerFactory? loggerFactoryHolder = null;

builder.Services
    .AddDiagnosticMcpServer(() => loggerFactoryHolder)
    .WithHttpTransport();

var app = builder.Build();
loggerFactoryHolder = app.Services.GetRequiredService<ILoggerFactory>();

var token = BearerTokenOptions.LoadOrGenerate(app.Logger);
app.UseMiddleware<BearerTokenMiddleware>(token);

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapMcp("/mcp");

app.Run();
return 0;

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

    hostBuilder.Services.AddDiagnosticCoreServices();

    ILoggerFactory? stdioLoggerFactoryHolder = null;
    hostBuilder.Services
        .AddDiagnosticMcpServer(() => stdioLoggerFactoryHolder)
        .WithStdioServerTransport();

    var host = hostBuilder.Build();
    stdioLoggerFactoryHolder = host.Services.GetRequiredService<ILoggerFactory>();

    await host.RunAsync().ConfigureAwait(false);
    return 0;
}

namespace DotnetDiagnosticsMcp.Server
{
    public partial class Program;
}
