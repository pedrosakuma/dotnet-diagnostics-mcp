using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.EventSources;
using DotnetDiagnosticsMcp.Core.Exceptions;
using DotnetDiagnosticsMcp.Core.Gc;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using DotnetDiagnosticsMcp.Core.Symbols;
using DotnetDiagnosticsMcp.Server.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Hosting;

/// <summary>
/// Shared DI + MCP-server registrations used by both transports (HTTP, see
/// <c>Program.cs</c>'s WebApplication path; and stdio, see #74 — invoked when the binary
/// is launched with <c>--stdio</c>, e.g. when an MCP client like Copilot CLI spawns the
/// server as a per-session subprocess). Keeping the registrations in one place ensures
/// every tool, prompt, and resource works identically across transports.
/// </summary>
internal static class DiagnosticServiceRegistration
{
    /// <summary>
    /// Registers every Core collector / planner / store the tool layer depends on. Idempotent
    /// per IServiceCollection; safe to call from both WebApplicationBuilder and HostApplicationBuilder.
    /// </summary>
    public static IServiceCollection AddDiagnosticCoreServices(this IServiceCollection services, string? configuredSymbolPath = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(new SymbolPathBuilder(configuredSymbolPath));
        services.AddSingleton<IProcessDiscovery, LocalProcessDiscovery>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Container.IContainerSignalsCollector, DotnetDiagnosticsMcp.Core.Container.CgroupV2SignalsCollector>();
        services.AddSingleton<ICapabilityDetector, CapabilityDetector>();
        services.AddSingleton<IProcessContextResolver, ProcessContextResolver>();
        services.AddSingleton<ICounterCollector, EventPipeCounterCollector>();
        services.AddSingleton<ClrMdMethodInstantiationEnricher>();
        services.AddSingleton<EventPipeCpuSampler>();
        services.AddSingleton<EventPipeAllocationSampler>();
        services.AddSingleton<PerfNativeAotCpuSampler>();
        services.AddSingleton<EtwNativeAotCpuSampler>();
        services.AddSingleton<ICpuSampler, RoutingCpuSampler>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.OffCpu.PerfSchedOffCpuSampler>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.OffCpu.EtwOffCpuSampler>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.OffCpu.IOffCpuSampler, DotnetDiagnosticsMcp.Core.OffCpu.RoutingOffCpuSampler>();
        services.AddSingleton<IExceptionCollector, EventPipeExceptionCollector>();
        services.AddSingleton<IGcCollector, EventPipeGcCollector>();
        services.AddSingleton<IEventSourceCollector, EventPipeEventSourceCollector>();
        services.AddSingleton<IProcessDumper, DiagnosticsClientDumper>();
        services.AddSingleton<IDumpInspector, ClrMdDumpInspector>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Threads.ClrMdThreadSnapshotInspector>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Threads.LinuxNativeThreadSnapshotInspector>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Threads.PerfReplayThreadSnapshotInspector>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Threads.EtwNativeThreadSnapshotInspector>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Threads.IThreadSnapshotBackend, DotnetDiagnosticsMcp.Core.Threads.ClrMdThreadSnapshotBackend>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Threads.IThreadSnapshotBackend, DotnetDiagnosticsMcp.Core.Threads.LinuxNativeThreadSnapshotBackend>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Threads.IThreadSnapshotBackend, DotnetDiagnosticsMcp.Core.Threads.EtwNativeThreadSnapshotBackend>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Threads.IThreadSnapshotBackend, DotnetDiagnosticsMcp.Core.Threads.PerfReplayThreadSnapshotBackend>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Threads.IThreadSnapshotInspector, DotnetDiagnosticsMcp.Core.Threads.RoutingThreadSnapshotInspector>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.JitCapture.IJitMethodCapturer, DotnetDiagnosticsMcp.Core.JitCapture.ClrMdJitMethodCapturer>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Investigation.IInvestigationPlanner>(_ =>
            new DotnetDiagnosticsMcp.Core.Investigation.InvestigationPlanner());
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Memory.IProvenanceCollector, DotnetDiagnosticsMcp.Core.Memory.EnvironmentProvenanceCollector>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Memory.IInvestigationSummaryExporter, DotnetDiagnosticsMcp.Core.Memory.InvestigationSummaryExporter>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Memory.ISummaryComparer, DotnetDiagnosticsMcp.Core.Memory.SummaryComparer>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Memory.IMemoryTrendCollector, DotnetDiagnosticsMcp.Core.Memory.MemoryTrendCollector>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Drilldown.IDiagnosticHandleStore>(_ =>
            new DotnetDiagnosticsMcp.Core.Drilldown.MemoryDiagnosticHandleStore(maxEntries: 32));
        services.AddSingleton<ModelContextProtocol.IMcpTaskStore>(_ =>
            new ModelContextProtocol.InMemoryMcpTaskStore(
                defaultTtl: System.TimeSpan.FromMinutes(10),
                pollInterval: System.TimeSpan.FromSeconds(1),
                maxTasks: 32,
                maxTasksPerSession: 32));
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Jobs.ICollectionJobRunner, DotnetDiagnosticsMcp.Core.Jobs.CollectionJobRunner>();
        services.AddHostedService<HandleEvictionBackgroundService>();

        return services;
    }

    /// <summary>
    /// Registers <c>AddMcpServer</c> with the tools/prompts/resources surface and the
    /// shared ToolErrorSurfaceFilter. <paramref name="loggerFactoryAccessor"/> is held by
    /// closure and read lazily after the host is built, mirroring the original Program.cs
    /// pattern (the filter cannot resolve services itself).
    /// </summary>
    public static IMcpServerBuilder AddDiagnosticMcpServer(
        this IServiceCollection services,
        Func<ILoggerFactory?> loggerFactoryAccessor)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(loggerFactoryAccessor);

        return services
            .AddMcpServer(options =>
            {
                options.Filters.Request.CallToolFilters.Add(
                    ToolErrorSurfaceFilter.Create(
                        () => loggerFactoryAccessor()?.CreateLogger(typeof(ToolErrorSurfaceFilter).FullName!)));

                options.ProtocolVersion = "2025-11-25";

                options.ServerInfo = new Implementation
                {
                    Name = "dotnet-diagnostics-mcp",
                    Title = ".NET Diagnostics",
                    Version = typeof(DiagnosticServiceRegistration).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                    Description =
                        "On-demand performance diagnostics for running .NET applications " +
                        "(CoreCLR and NativeAOT) over the runtime diagnostic IPC socket. " +
                        "No target-side instrumentation required. Designed for K8s sidecar deployments.",
                    WebsiteUrl = "https://github.com/pedrosakuma/dotnet-diagnostics-mcp",
                };

                options.ServerInstructions = ServerInstructionsText;
            })
            .WithTools<DiagnosticTools>()
            .WithPrompts<Prompts.DiagnosticPrompts>()
            .WithResources<Resources.InvestigationGuideResources>()
            .WithResources<Resources.TraceSessionResources>()
            .WithResources<Resources.HeapSnapshotResources>()
            .WithResources<Resources.ThreadSnapshotResources>();
    }

    private const string ServerInstructionsText =
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
}
