using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using DotnetDiagnosticsMcp.Server.Tools;

namespace DotnetDiagnosticsMcp.Server.Hosting;

/// <summary>
/// Single source of truth for every <c>[McpServerToolType]</c> class the server registers.
/// The MCP <c>WithTools&lt;&gt;()</c> chain, the per-tool scope registry, the deprecation
/// registry and the orchestrator proxy allowlist all read from this list so that adding
/// a new tool surface is a one-line change and the four registration sites cannot drift.
/// </summary>
/// <remarks>
/// <para>
/// Until RFC 0002 Wave 2 each new tool surface required four parallel edits (the WithTools
/// chain, two surfaceTypes arrays inside <c>DiagnosticServiceRegistration</c>, and the
/// orchestrator proxy allowlist). Parallel PRs hit DIRTY conflicts on every cascade. This
/// helper collapses all four sites into a single addition here.
/// </para>
/// <para>
/// <see cref="Always"/> hosts pod-local diagnostic surfaces (always registered).
/// <see cref="OrchestratorOnly"/> hosts the orchestrator-only management surfaces
/// (registered when <c>Diagnostics:Orchestrator:Enabled</c> is true).
/// <see cref="Proxyable"/> is the subset reachable through the orchestrator reverse proxy —
/// currently equal to <see cref="Always"/> by design: orchestrator-management tools live on
/// a separate type and the pod-local sidecar wouldn't understand them anyway.
/// </para>
/// </remarks>
internal static class PodLocalToolSurfaces
{
    /// <summary>Tool-surface classes registered on every server (no orchestrator dependency).</summary>
    /// <remarks>
    /// Exposed as <see cref="ImmutableArray{T}"/> rather than <see cref="IReadOnlyList{T}"/> so consumers
    /// cannot cast back to <c>Type[]</c> and mutate the canonical list (would propagate to every site —
    /// including the lazy-built <c>InvestigationProxyToolAllowlist</c> set).
    /// </remarks>
    public static ImmutableArray<Type> Always { get; } = ImmutableArray.Create(new[]
    {
        typeof(DiagnosticTools),
        typeof(CollectEventsTool),
        typeof(CollectSampleTool),
        typeof(GetBytesTool),
        typeof(InspectProcessTool),
        typeof(InspectHeapTool),
        typeof(QuerySnapshotTool),
    });

    /// <summary>Tool-surface classes registered only when orchestrator features are enabled.</summary>
    public static ImmutableArray<Type> OrchestratorOnly { get; } = ImmutableArray.Create(new[]
    {
        typeof(OrchestratorTools),
        typeof(ListOrchestratorTool),
    });

    /// <summary>Tool-surface classes registered only when AzureDiscovery is enabled (#232).</summary>
    public static ImmutableArray<Type> AzureDiscoveryOnly { get; } = ImmutableArray.Create(new[]
    {
        typeof(DiscoverAzureTool),
    });

    /// <summary>
    /// Subset of tool-surface classes that the orchestrator reverse proxy is allowed to forward
    /// to the pod-local sidecar. By contract this excludes <see cref="OrchestratorOnly"/>
    /// (the pod-local server doesn't host those tools).
    /// </summary>
    public static ImmutableArray<Type> Proxyable => Always;

    /// <summary>Returns the full set of surfaces to register given the orchestrator
    /// and Azure-discovery toggles.</summary>
    public static Type[] GetSurfaceTypes(
        bool enableOrchestratorTools,
        bool enableAzureDiscoveryTools = false)
    {
        var size = Always.Length
            + (enableOrchestratorTools ? OrchestratorOnly.Length : 0)
            + (enableAzureDiscoveryTools ? AzureDiscoveryOnly.Length : 0);

        var combined = new Type[size];
        var i = 0;
        for (var k = 0; k < Always.Length; k++) combined[i++] = Always[k];
        if (enableOrchestratorTools)
        {
            for (var k = 0; k < OrchestratorOnly.Length; k++) combined[i++] = OrchestratorOnly[k];
        }
        if (enableAzureDiscoveryTools)
        {
            for (var k = 0; k < AzureDiscoveryOnly.Length; k++) combined[i++] = AzureDiscoveryOnly[k];
        }
        return combined;
    }
}
