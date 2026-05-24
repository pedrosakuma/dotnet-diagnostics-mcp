using System;
using System.Collections.Generic;
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
    public static IReadOnlyList<Type> Always { get; } = new[]
    {
        typeof(DiagnosticTools),
        typeof(CollectEventsTool),
        typeof(GetBytesTool),
        typeof(InspectProcessTool),
        typeof(InspectHeapTool),
    };

    /// <summary>Tool-surface classes registered only when orchestrator features are enabled.</summary>
    public static IReadOnlyList<Type> OrchestratorOnly { get; } = new[]
    {
        typeof(OrchestratorTools),
        typeof(ListOrchestratorTool),
    };

    /// <summary>
    /// Subset of tool-surface classes that the orchestrator reverse proxy is allowed to forward
    /// to the pod-local sidecar. By contract this excludes <see cref="OrchestratorOnly"/>
    /// (the pod-local server doesn't host those tools).
    /// </summary>
    public static IReadOnlyList<Type> Proxyable => Always;

    /// <summary>Returns the full set of surfaces to register given the orchestrator toggle.</summary>
    public static Type[] GetSurfaceTypes(bool enableOrchestratorTools)
    {
        if (!enableOrchestratorTools)
        {
            var copy = new Type[Always.Count];
            for (var i = 0; i < Always.Count; i++) copy[i] = Always[i];
            return copy;
        }

        var combined = new Type[Always.Count + OrchestratorOnly.Count];
        for (var i = 0; i < Always.Count; i++) combined[i] = Always[i];
        for (var i = 0; i < OrchestratorOnly.Count; i++) combined[Always.Count + i] = OrchestratorOnly[i];
        return combined;
    }
}
