using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.EventSources;
using DotnetDiagnosticsMcp.Core.Exceptions;
using DotnetDiagnosticsMcp.Core.Gc;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using DotnetDiagnosticsMcp.Server.Auth;
using DotnetDiagnosticsMcp.Server.Tools;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(o =>
{
    o.IncludeScopes = true;
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss.fff ";
});

builder.Services.AddSingleton<IProcessDiscovery, LocalProcessDiscovery>();
builder.Services.AddSingleton<ICapabilityDetector, CapabilityDetector>();
builder.Services.AddSingleton<ICounterCollector, EventPipeCounterCollector>();
builder.Services.AddSingleton<ICpuSampler, EventPipeCpuSampler>();
builder.Services.AddSingleton<IExceptionCollector, EventPipeExceptionCollector>();
builder.Services.AddSingleton<IGcCollector, EventPipeGcCollector>();
builder.Services.AddSingleton<IEventSourceCollector, EventPipeEventSourceCollector>();
builder.Services.AddSingleton<IProcessDumper, DiagnosticsClientDumper>();

builder.Services
    .AddMcpServer(options =>
    {
        // Advertise the latest spec version we have validated against.
        // SDK 1.3.0 supports negotiation back to 2024-11-05 if the client is older.
        options.ProtocolVersion = "2025-11-25";

        options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
        {
            Name = "dotnet-diagnostics-mcp",
            Title = ".NET Diagnostics",
            Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            Description =
                "On-demand performance diagnostics for running .NET applications " +
                "(CoreCLR and NativeAOT) over the runtime diagnostic IPC socket. " +
                "No target-side instrumentation required. Designed for K8s sidecar deployments.",
            WebsiteUrl = "https://github.com/pedrosakuma/dotnet-diagnostics-mcp",
        };

        // Instructions are surfaced verbatim by most MCP clients (Claude Desktop, Claude Code,
        // Copilot CLI, Cursor) to the model on session start. Keep this short, action-oriented,
        // and biased toward telling the model HOW to drive an investigation, not just what exists.
        options.ServerInstructions =
            """
            This server attaches to running .NET processes (locally or in a K8s sidecar) to
            collect performance diagnostics on demand. No code changes to the target are
            required.

            Recommended call order for a fresh investigation:

              1. `list_dotnet_processes` — discover attachable .NET processes by pid.
              2. `get_diagnostic_capabilities` — confirms CoreCLR vs NativeAOT and which
                 collectors are supported (NativeAOT lacks CPU sampling and gcdump).
              3. `snapshot_counters` — cheap first signal: CPU, working set, GC pressure,
                 thread pool, requests/sec. Use this before reaching for sampling/dumps.
              4. From the symptom narrow down: high CPU → `collect_cpu_sample`; allocations
                 or GC pauses → `collect_gc_events`; errors → `collect_exceptions`;
                 framework-specific signals → `collect_event_source` with the right provider.
              5. `collect_process_dump` is the heavyweight last resort (Mini < Triage <
                 WithHeap < Full). Use only when live collectors are insufficient.

            Always prefer the shortest collection window that answers the question
            (`durationSeconds`) and bound result lists (`topN`, `maxRecent`, `maxEvents`)
            to keep responses small. Tools are read-only except `collect_process_dump`,
            which writes a dump file to disk and is marked Destructive.
            """;
    })
    .WithHttpTransport()
    .WithTools<DiagnosticTools>()
    .WithResources<DotnetDiagnosticsMcp.Server.Resources.InvestigationGuideResources>();

var app = builder.Build();

var token = BearerTokenOptions.LoadOrGenerate(app.Logger);
app.UseMiddleware<BearerTokenMiddleware>(token);

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapMcp("/mcp");

app.Run();

namespace DotnetDiagnosticsMcp.Server
{
    public partial class Program;
}
