using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

        // Note: no NextActionHint pointing at attach_to_pod yet — that tool lands in P3b
        // (issue #20). Until then, list_pods is read-only and the LLM should fall back to
        // the sidecar transport for the actual diagnostic work.
        return DiagnosticResult.Ok(
            page,
            summary,
            page.NextCursor is not null
                ? new NextActionHint(
                    "list_pods",
                    "More candidates available — pass nextCursor to fetch the next page.",
                    new Dictionary<string, object?> { ["cursor"] = page.NextCursor })
                : new NextActionHint(
                    "list_pods",
                    "Narrow the result further with labelSelector / containerName, or set preparedOnly=false to also see heuristic candidates."));
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

    [McpServerTool(
        Name = "attach_to_pod",
        Title = "Attach a diagnostic ephemeral container to a Pod",
        Destructive = true,
        ReadOnly = false,
        Idempotent = false,
        UseStructuredContent = true)]
    [Description(
        "Injects a diagnostics ephemeral container into the named Pod, joins the target container's PID namespace, " +
        "and returns an opaque investigation handle that future tool calls will be able to route through (the " +
        "transport proxy lands in the next orchestrator slice). The Pod must already be in phase=Running and — " +
        "by default — must opt in via the prepared label (diagnostics.dotnet.io/prepared=true). " +
        "Reuses an existing investigation for the same (namespace, pod, container) when one is already attached. " +
        "Side-effect: ephemeral containers cannot be removed once added; the diagnostics container will remain " +
        "on the Pod's spec until the Pod is recreated.")]
    public static async Task<DiagnosticResult<AttachSession>> AttachToPod(
        IPodAttachOrchestrator orchestrator,
        OrchestratorOptions options,
        IInvestigationSessionBinder sessionBinder,
        McpServer server,
        ILoggerFactory? loggerFactory = null,
        [Description("Pod namespace. Falls back to the orchestrator's DefaultNamespace when omitted.")]
        string? @namespace = null,
        [Description("Pod name. Required.")]
        string? podName = null,
        [Description("Container name inside the Pod. Defaults to the first container in the Pod's spec.")]
        string? containerName = null,
        [Description("Per-investigation TTL in seconds. Defaults to Orchestrator:DefaultInvestigationTtlSeconds (1800).")]
        int? ttlSeconds = null,
        [Description("When true (default), refuses to attach to Pods that don't carry the prepared opt-in label.")]
        bool requirePreparedTarget = true,
        [Description("When true (default), returns an existing investigation for the same target instead of patching a second ephemeral container.")]
        bool allowReuseExistingSession = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(podName))
        {
            var ex = new OrchestratorException(OrchestratorErrorKinds.InvalidArgument, "podName is required.");
            return DiagnosticResult.Fail<AttachSession>(
                $"attach_to_pod failed: {ex.Message}",
                new DiagnosticError(ex.ErrorKind, ex.Message),
                BuildAttachRecoveryHint(ex.ErrorKind));
        }

        var request = new AttachRequest(
            Namespace: @namespace ?? string.Empty,
            PodName: podName!,
            ContainerName: containerName,
            TtlSeconds: ttlSeconds,
            RequirePreparedTarget: requirePreparedTarget,
            AllowReuseExistingSession: allowReuseExistingSession);

        InvestigationHandle handle;
        try
        {
            handle = await orchestrator.AttachAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OrchestratorException ex)
        {
            return DiagnosticResult.Fail<AttachSession>(
                $"attach_to_pod failed: {ex.Message}",
                new DiagnosticError(ex.ErrorKind, ex.Message),
                BuildAttachRecoveryHint(ex.ErrorKind));
        }

        // Project to the client-safe shape so PodLocalBearerToken never leaves the server.
        // The proxy URL is a relative path the client appends to its MCP base; the
        // orchestrator's reverse proxy at /proxy/{handleId}/... will swap the bearer for
        // the per-attach Pod-local secret before forwarding.
        var proxyUrl = handle.State == InvestigationState.Active
            ? options.ProxyBasePath.TrimEnd('/') + "/" + handle.HandleId
            : null;
        var session = AttachSession.FromHandle(handle, proxyUrl);

        // Bind the MCP session to the handle so future server-side machinery (P3b-4 /
        // intercept slice) can route subsequent tool calls through the proxy without the
        // client having to rewrite URLs. We only bind on Active handles — Attaching /
        // Failed states are not yet usable and would mislead any session-aware resolver.
        if (handle.State == InvestigationState.Active)
        {
            var sessionId = TryGetServerSessionId(server);
            if (!string.IsNullOrEmpty(sessionId))
            {
                sessionBinder.Bind(sessionId, handle.HandleId);
            }
            else
            {
                (loggerFactory ?? NullLoggerFactory.Instance)
                    .CreateLogger(typeof(OrchestratorTools).FullName!)
                    .LogDebug(
                        "attach_to_pod: McpServer.SessionId unavailable; skipping investigation session binding for handle {HandleId}.",
                        handle.HandleId);
            }
        }

        var summary = $"Investigation {session.HandleId} {(session.State == InvestigationState.Active ? "attached" : session.State.ToString().ToLowerInvariant())} " +
                      $"to {session.Namespace}/{session.PodName} container={session.TargetContainerName} " +
                      $"(ephemeral={session.EphemeralContainerName}; expires at {session.ExpiresAt:O}).";

        return DiagnosticResult.Ok(
            session,
            summary,
            new NextActionHint(
                "attach_to_pod",
                proxyUrl is null
                    ? "Investigation is not yet Active. Re-poll the handle or retry attach_to_pod."
                    : $"Route subsequent diagnostic tool calls to '{proxyUrl}/...' on this orchestrator. " +
                      "Continue presenting the normal orchestrator bearer token — the proxy strips it and injects the per-attach Pod-local bearer upstream automatically."));
    }

    private static NextActionHint BuildAttachRecoveryHint(string errorKind) => errorKind switch
    {
        OrchestratorErrorKinds.InvalidArgument => new NextActionHint(
            "attach_to_pod",
            "Pass podName (and namespace if no DefaultNamespace is configured)."),
        OrchestratorErrorKinds.NamespaceNotAllowed => new NextActionHint(
            "attach_to_pod",
            "Pass a namespace that is configured in Orchestrator:NamespaceAllowlist."),
        OrchestratorErrorKinds.PodNotFound => new NextActionHint(
            "list_pods",
            "Run list_pods in the same namespace to discover the correct pod name."),
        OrchestratorErrorKinds.ContainerNotFound => new NextActionHint(
            "list_pods",
            "Run list_pods to inspect the target Pod's containers and re-run with the right containerName."),
        OrchestratorErrorKinds.PodNotRunning => new NextActionHint(
            "list_pods",
            "Wait for the Pod to reach phase=Running, or pick a different Pod from list_pods."),
        OrchestratorErrorKinds.PodNotPrepared => new NextActionHint(
            "attach_to_pod",
            "Add the prepared opt-in label (and a shared /tmp emptyDir + matching UID) to the Pod template, " +
            "or set requirePreparedTarget=false (and Orchestrator:RequirePreparedLabel=false) to override."),
        OrchestratorErrorKinds.AttachAlreadyInProgress => new NextActionHint(
            "attach_to_pod",
            "Another attach is in flight for this Pod. Retry with allowReuseExistingSession=true."),
        OrchestratorErrorKinds.AttachTimeout => new NextActionHint(
            "attach_to_pod",
            "The ephemeral container did not become Running in time. Check image-pull errors on the Pod, " +
            "increase Orchestrator:AttachReadinessTimeoutSeconds, then retry."),
        OrchestratorErrorKinds.AttachFailed => new NextActionHint(
            "attach_to_pod",
            "Check the orchestrator logs and the Pod's ephemeralContainerStatuses, then retry."),
        OrchestratorErrorKinds.PermissionDenied => new NextActionHint(
            "attach_to_pod",
            "Grant the orchestrator ServiceAccount 'pods/ephemeralcontainers' patch in the namespace."),
        OrchestratorErrorKinds.KubeApiUnavailable => new NextActionHint(
            "attach_to_pod",
            "Verify the orchestrator has an in-cluster ServiceAccount projection or a reachable kubeconfig, then retry."),
        _ => new NextActionHint(
            "attach_to_pod",
            "Re-run with corrected arguments."),
    };

    // Mirror of DiagnosticTools.TryGetServerSessionId. The MCP SDK exposes SessionId only
    // as an internal-ish property; reflecting against the McpServer instance is the same
    // pattern the diagnostic-tools side uses for MCP-task correlation (see
    // DiagnosticTools.cs ~L2683). Returns null when no session is bound to the call
    // (e.g. stdio transport or unit tests that synthesize an McpServer without one).
    private static string? TryGetServerSessionId(McpServer server)
        => server?.GetType()
                  .GetProperty("SessionId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                  ?.GetValue(server) as string;
}
