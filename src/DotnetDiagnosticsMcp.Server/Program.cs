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

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(o =>
{
    o.IncludeScopes = true;
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss.fff ";
});

builder.Services.AddSingleton<IProcessDiscovery, LocalProcessDiscovery>();
builder.Services.AddSingleton<DotnetDiagnosticsMcp.Core.Container.IContainerSignalsCollector, DotnetDiagnosticsMcp.Core.Container.CgroupV2SignalsCollector>();
builder.Services.AddSingleton<ICapabilityDetector, CapabilityDetector>();
builder.Services.AddSingleton<IProcessContextResolver, ProcessContextResolver>();
builder.Services.AddSingleton<ICounterCollector, EventPipeCounterCollector>();
builder.Services.AddSingleton<EventPipeCpuSampler>();
builder.Services.AddSingleton<PerfNativeAotCpuSampler>();
builder.Services.AddSingleton<EtwNativeAotCpuSampler>();
builder.Services.AddSingleton<ICpuSampler, RoutingCpuSampler>();
builder.Services.AddSingleton<DotnetDiagnosticsMcp.Core.OffCpu.PerfSchedOffCpuSampler>();
builder.Services.AddSingleton<DotnetDiagnosticsMcp.Core.OffCpu.EtwOffCpuSampler>();
builder.Services.AddSingleton<DotnetDiagnosticsMcp.Core.OffCpu.IOffCpuSampler, DotnetDiagnosticsMcp.Core.OffCpu.RoutingOffCpuSampler>();
builder.Services.AddSingleton<IExceptionCollector, EventPipeExceptionCollector>();
builder.Services.AddSingleton<IGcCollector, EventPipeGcCollector>();
builder.Services.AddSingleton<IEventSourceCollector, EventPipeEventSourceCollector>();
builder.Services.AddSingleton<IProcessDumper, DiagnosticsClientDumper>();
builder.Services.AddSingleton<IDumpInspector, ClrMdDumpInspector>();
builder.Services.AddSingleton<DotnetDiagnosticsMcp.Core.Threads.IThreadSnapshotInspector, DotnetDiagnosticsMcp.Core.Threads.ClrMdThreadSnapshotInspector>();
builder.Services.AddSingleton<DotnetDiagnosticsMcp.Core.Investigation.IInvestigationPlanner>(_ =>
    new DotnetDiagnosticsMcp.Core.Investigation.InvestigationPlanner());
builder.Services.AddSingleton<DotnetDiagnosticsMcp.Core.Memory.IProvenanceCollector, DotnetDiagnosticsMcp.Core.Memory.EnvironmentProvenanceCollector>();
builder.Services.AddSingleton<DotnetDiagnosticsMcp.Core.Memory.IInvestigationSummaryExporter, DotnetDiagnosticsMcp.Core.Memory.InvestigationSummaryExporter>();
builder.Services.AddSingleton<DotnetDiagnosticsMcp.Core.Memory.ISummaryComparer, DotnetDiagnosticsMcp.Core.Memory.SummaryComparer>();
builder.Services.AddSingleton<DotnetDiagnosticsMcp.Core.Drilldown.IDiagnosticHandleStore>(_ =>
    new DotnetDiagnosticsMcp.Core.Drilldown.MemoryDiagnosticHandleStore(maxEntries: 32));
builder.Services.AddSingleton<DotnetDiagnosticsMcp.Core.Jobs.ICollectionJobRunner, DotnetDiagnosticsMcp.Core.Jobs.CollectionJobRunner>();
builder.Services.AddHostedService<DotnetDiagnosticsMcp.Server.Hosting.HandleEvictionBackgroundService>();

// Hold the resolved ILoggerFactory once the app is built so the CallTool filter (configured
// before Build()) can obtain a logger lazily without sharing state with WebApplication.
ILoggerFactory? loggerFactoryHolder = null;

builder.Services
    .AddMcpServer(options =>
    {
        // Surface every tool exception as a structured CallToolResult so the LLM sees the
        // real failure (PTRACE denied, FileNotFound, ClrMD version mismatch, ...) instead
        // of the SDK's generic "An error occurred invoking 'X'.". See issues #62, #63 and
        // DotnetDiagnosticsMcp.Server.Tools.ToolErrorSurfaceFilter for the rationale.
        options.Filters.Request.CallToolFilters.Add(
            DotnetDiagnosticsMcp.Server.Tools.ToolErrorSurfaceFilter.Create(
                () => loggerFactoryHolder?.CreateLogger("DotnetDiagnosticsMcp.Server.Tools.ToolErrorSurfaceFilter")));


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

              1. `snapshot_counters` — cheap first signal: CPU, working set, GC pressure,
                 thread pool, requests/sec. When exactly one .NET process is reachable the
                 server auto-selects it; `processId` is optional on every live-process tool.
              2. From the symptom narrow down: high CPU → `collect_cpu_sample`; allocations
                 or GC pauses → `collect_gc_events`; errors → `collect_exceptions`;
                 framework-specific signals → `collect_event_source` with the right provider.
              3. `collect_process_dump` is the heavyweight last resort (Mini < Triage <
                 WithHeap < Full). Use only when live collectors are insufficient.

            Use `list_dotnet_processes` only when auto-resolution fails (zero or multiple
            .NET processes visible — the error response will tell you). Use
            `get_diagnostic_capabilities` to confirm CoreCLR vs NativeAOT before reaching
            for NativeAOT-incompatible collectors (CPU sampling, gcdump).

            Always prefer the shortest collection window that answers the question
            (`durationSeconds`) and bound result lists (`topN`, `maxRecent`, `maxEvents`)
            to keep responses small. Tools are read-only except `collect_process_dump`,
            which writes a dump file to disk and is marked Destructive.

            This server never requests Elicitation: every tool ships with sensible
            defaults for every parameter. `processId` is optional — omit it to auto-select
            the lone reachable .NET process, or pass an explicit pid from
            `list_dotnet_processes` when several are visible. Pick a default and re-run
            with refined arguments if the first attempt is too noisy or too sparse — the
            response `hints` will tell you how.

            For a longer playbook (HTTP latency, exception storms, GC retention,
            NativeAOT caveats), read the `diag://guides/investigation` resource or
            invoke one of the Prompts (`diagnose-high-latency`, `diagnose-memory-growth`,
            `diagnose-5xx-errors`, `diagnose-slow-outbound-http`, `triage-nativeaot`,
            `diagnose-safely-in-prod`).
            """;
    })
    .WithHttpTransport()
    .WithTools<DiagnosticTools>()
    .WithPrompts<DotnetDiagnosticsMcp.Server.Prompts.DiagnosticPrompts>()
    .WithResources<DotnetDiagnosticsMcp.Server.Resources.InvestigationGuideResources>()
    .WithResources<DotnetDiagnosticsMcp.Server.Resources.TraceSessionResources>()
    .WithResources<DotnetDiagnosticsMcp.Server.Resources.HeapSnapshotResources>()
    .WithResources<DotnetDiagnosticsMcp.Server.Resources.ThreadSnapshotResources>();

var app = builder.Build();
loggerFactoryHolder = app.Services.GetRequiredService<ILoggerFactory>();

var token = BearerTokenOptions.LoadOrGenerate(app.Logger);
app.UseMiddleware<BearerTokenMiddleware>(token);

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapMcp("/mcp");

app.Run();
return 0;

namespace DotnetDiagnosticsMcp.Server
{
    public partial class Program;
}
