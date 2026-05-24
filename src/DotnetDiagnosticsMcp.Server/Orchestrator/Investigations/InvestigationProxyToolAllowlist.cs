using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DotnetDiagnosticsMcp.Server.Tools;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;

/// <summary>
/// H7 (issue #164) — defense-in-depth allowlist of MCP tool names that the
/// orchestrator may forward through the per-handle reverse proxy to the
/// in-Pod diagnostics MCP.
/// </summary>
/// <remarks>
/// <para>
/// Discovered once via reflection over <see cref="DiagnosticTools"/> and
/// <see cref="GetBytesTool"/>: every method decorated with
/// <see cref="McpServerToolAttribute"/> contributes its tool name.
/// Orchestrator-management tools live on a separate type (<c>OrchestratorTools</c>)
/// and never appear in this allowlist; the pod-local sidecar would not understand
/// them anyway. New endpoints / tools added to the pod-local surface therefore do
/// NOT become automatically reachable through the proxy — they must be added
/// deliberately to one of the scanned surface types here.
/// </para>
/// <para>
/// Enforced at TWO layers (filter + pod-local client) so that a bug in one layer —
/// or a future code path that bypasses the filter — does not allow an attacker to
/// drive a non-diagnostic endpoint with the auto-injected pod-local bearer.
/// </para>
/// </remarks>
internal static class InvestigationProxyToolAllowlist
{
    private static readonly Lazy<HashSet<string>> AllowedNames = new(BuildSet);

    /// <summary>Returns true when <paramref name="toolName"/> is permitted to traverse the proxy.</summary>
    public static bool IsAllowed(string? toolName)
        => !string.IsNullOrEmpty(toolName) && AllowedNames.Value.Contains(toolName);

    /// <summary>Snapshot of every allowed tool name. Useful for logging / diagnostics.</summary>
    public static IReadOnlyCollection<string> AllowedToolNames => AllowedNames.Value.ToArray();

    private static HashSet<string> BuildSet()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        // Pod-local diagnostic surface — DiagnosticTools (legacy + non-RFC-0002 tools) plus
        // every type that hosts an additional [McpServerToolType] reachable through the
        // pod-local sidecar. Keep this list in lock-step with DiagnosticServiceRegistration's
        // WithTools<>() chain so a new tool-surface class added there cannot be silently
        // unreachable through the orchestrator proxy.
        foreach (var surface in new[] { typeof(DiagnosticTools), typeof(GetBytesTool), typeof(InspectProcessTool) })
        {
            foreach (var method in surface.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>(inherit: false);
                if (attr is null) continue;
                var name = attr.Name;
                if (!string.IsNullOrEmpty(name))
                {
                    set.Add(name);
                }
            }
        }
        return set;
    }
}
