using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Tools;

/// <summary>
/// MCP tools that expose the central Kubernetes orchestrator (issue #20).
/// </summary>
/// <remarks>
/// Registered only when <c>OrchestratorOptions.Enabled</c> is true. When disabled, the
/// type is not added to the MCP tool surface so the LLM never sees the tools and the
/// server keeps its sidecar-only behavior.
///
/// Phase P3a ships only <c>list_pods</c>. Attach / detach / list_active_investigations
/// land in P3b and P4 once the proxy plumbing is in place.
/// </remarks>
[McpServerToolType]
public sealed class OrchestratorTools
{
    [McpServerTool(
        Name = "list_pods",
        Title = "List candidate Pods for diagnostic attach",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Enumerates Pods in the orchestrator's allowed namespaces that are candidates for diagnostic attach. " +
        "By default returns only Pods that opt in via the prepared label (diagnostics.dotnet.io/prepared=true) " +
        "and are Ready. Pass preparedOnly=false to surface heuristic candidates, or includeNotReady=true to see " +
        "Pods that have not yet reported Ready. Each row carries enough metadata (namespace, name, container, image, " +
        "owner, labels, preparedness verdict) for the LLM to pick a single target without a second lookup. " +
        "This tool is read-only — it does NOT inject any ephemeral container; that happens at attach_to_pod time.")]
    public static async Task<DiagnosticResult<PodCandidatePage>> ListPods(
        IPodInventory inventory,
        [Description("Kubernetes namespace to list Pods from. When omitted, the orchestrator's DefaultNamespace is used.")]
        string? @namespace = null,
        [Description("Optional Kubernetes label selector (e.g. 'app=api,env=prod'). Keys are checked against the orchestrator's allowlist when configured.")]
        string? labelSelector = null,
        [Description("Optional Kubernetes field selector (e.g. 'status.phase=Running'). Forwarded to the API as-is.")]
        string? fieldSelector = null,
        [Description("Optional container name to target. Defaults to the first container in each Pod's spec.")]
        string? containerName = null,
        [Description("When true (default), only Pods that are diagnostically prepared are returned.")]
        bool preparedOnly = true,
        [Description("When false (default), Pods that are not Ready are filtered out.")]
        bool includeNotReady = false,
        [Description("Max rows per page (default 100, clamped to the orchestrator's MaxListLimit).")]
        int limit = 100,
        [Description("Opaque continuation cursor from a prior call's nextCursor. Null for the first page.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var request = new ListPodsRequest(
            Namespace: @namespace,
            LabelSelector: labelSelector,
            FieldSelector: fieldSelector,
            ContainerName: containerName,
            PreparedOnly: preparedOnly,
            IncludeNotReady: includeNotReady,
            Limit: limit,
            Cursor: cursor);

        PodCandidatePage page;
        try
        {
            page = await inventory.ListPodsAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OrchestratorException ex)
        {
            return DiagnosticResult.Fail<PodCandidatePage>(
                $"list_pods failed: {ex.Message}",
                new DiagnosticError(ex.ErrorKind, ex.Message),
                BuildRecoveryHint(ex.ErrorKind));
        }

        if (page.Items.Count == 0)
        {
            return DiagnosticResult.Ok(
                page,
                preparedOnly
                    ? "No prepared Pods found in scope. Try preparedOnly=false to see heuristic candidates."
                    : "No Pods matched the supplied filters.",
                new NextActionHint(
                    "list_pods",
                    "Loosen the filters (preparedOnly=false, includeNotReady=true) or widen the labelSelector.",
                    new Dictionary<string, object?> { ["preparedOnly"] = false, ["includeNotReady"] = true }));
        }

        var first = page.Items[0];
        var summary = $"Found {page.Items.Count} candidate Pod(s){(page.NextCursor is not null ? " (more available — use nextCursor)" : "")}. " +
                      $"First: {first.Namespace}/{first.Name} container={first.ContainerName} prepared={first.DiagnosticsPrepared} ({first.PreparationReason}).";

        return DiagnosticResult.Ok(
            page,
            summary,
            new NextActionHint(
                "attach_to_pod",
                "Attach an ephemeral diagnostics container to a chosen Pod. Pass the namespace and pod name from this list.",
                new Dictionary<string, object?>
                {
                    ["namespace"] = first.Namespace,
                    ["pod"] = first.Name,
                    ["container"] = first.ContainerName,
                }));
    }

    private static NextActionHint BuildRecoveryHint(string errorKind) => errorKind switch
    {
        OrchestratorErrorKinds.NamespaceNotAllowed => new NextActionHint(
            "list_pods",
            "Pass a namespace that is configured in Orchestrator:NamespaceAllowlist."),
        OrchestratorErrorKinds.SelectorRejected => new NextActionHint(
            "list_pods",
            "Drop the rejected label key, or extend Orchestrator:LabelKeyAllowlist on the server."),
        OrchestratorErrorKinds.PermissionDenied => new NextActionHint(
            "list_pods",
            "Grant the orchestrator ServiceAccount 'pods' get/list/watch in the requested namespace."),
        OrchestratorErrorKinds.KubeApiUnavailable => new NextActionHint(
            "list_pods",
            "Verify the orchestrator has an in-cluster ServiceAccount projection or a reachable kubeconfig, then retry."),
        _ => new NextActionHint(
            "list_pods",
            "Re-run with corrected arguments."),
    };
}
