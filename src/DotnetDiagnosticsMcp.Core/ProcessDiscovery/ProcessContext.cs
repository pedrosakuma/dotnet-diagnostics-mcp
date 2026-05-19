namespace DotnetDiagnosticsMcp.Core.ProcessDiscovery;

using DotnetDiagnosticsMcp.Core.Capabilities;

/// <summary>
/// Compact per-process digest attached to every successful diagnostic response so the LLM
/// can chain follow-up calls without re-running <c>list_dotnet_processes</c> or
/// <c>get_diagnostic_capabilities</c>. Cached for a short TTL — see
/// <see cref="IProcessContextResolver"/>.
/// </summary>
/// <param name="ProcessId">Resolved process id (after auto-resolve when the caller omitted it).</param>
/// <param name="Runtime">Runtime flavour as detected by <see cref="ICapabilityDetector"/> (e.g. CoreClr, NativeAot, Unknown).</param>
/// <param name="RuntimeVersion">CLR product version string when available.</param>
/// <param name="CanSampleCpu">True when CPU sampling is reachable (CoreCLR SampleProfiler, or NativeAOT with perf/ETW).</param>
/// <param name="CanCollectGcDump">True when ETW/EventPipe gcdump can be requested (CoreCLR only).</param>
/// <param name="AutoResolved">True when the caller omitted <c>processId</c> and the server resolved it from a single-match list.</param>
public sealed record ProcessContext(
    int ProcessId,
    RuntimeFlavor Runtime,
    string? RuntimeVersion,
    bool CanSampleCpu,
    bool CanCollectGcDump,
    bool AutoResolved);
