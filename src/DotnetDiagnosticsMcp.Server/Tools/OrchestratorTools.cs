using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Server.Observability;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;
using DotnetDiagnosticsMcp.Server.Security;
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
    [RequireScope("orchestrator-list")]
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
            "list_orchestrator",
                    "Loosen the filters (preparedOnly=false, includeNotReady=true) or widen the labelSelector.",
                    new Dictionary<string, object?> { ["kind"] = "pods", ["preparedOnly"] = false, ["includeNotReady"] = true }));
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
            "list_orchestrator",
                    "More candidates available — pass nextCursor to fetch the next page.",
                    new Dictionary<string, object?> { ["kind"] = "pods", ["cursor"] = page.NextCursor })
                : new NextActionHint(
            "list_orchestrator",
                    "Narrow the result further with labelSelector / containerName, or set preparedOnly=false to also see heuristic candidates."));
    }

    private static NextActionHint BuildRecoveryHint(string errorKind) => errorKind switch
    {
        OrchestratorErrorKinds.NamespaceNotAllowed => new NextActionHint(
            "list_orchestrator",
            "Pass a namespace that is configured in Orchestrator:NamespaceAllowlist."),
        OrchestratorErrorKinds.SelectorRejected => new NextActionHint(
            "list_orchestrator",
            "Drop the rejected label key, or extend Orchestrator:LabelKeyAllowlist on the server."),
        OrchestratorErrorKinds.PermissionDenied => new NextActionHint(
            "list_orchestrator",
            "Grant the orchestrator ServiceAccount 'pods' get/list/watch in the requested namespace."),
        OrchestratorErrorKinds.KubeApiUnavailable => new NextActionHint(
            "list_orchestrator",
            "Verify the orchestrator has an in-cluster ServiceAccount projection or a reachable kubeconfig, then retry."),
        OrchestratorErrorKinds.KubeconfigHandleNotFound => new NextActionHint(
            "discover_azure",
            "Re-run discover_azure(kind=aksclusters, includeKubeconfig=true) to mint a fresh handle, then retry list_orchestrator with the new value.",
            new Dictionary<string, object?> { ["kind"] = "aksclusters", ["includeKubeconfig"] = true }),
        _ => new NextActionHint(
            "list_orchestrator",
            "Re-run with corrected arguments."),
    };

    [RequireScope("orchestrator-attach")]
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
        IInvestigationStore store,
        IPrincipalAccessor principalAccessor,
        OrchestratorObservability observability,
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
        var resolvedNamespace = @namespace ?? options.DefaultNamespace ?? string.Empty;
        var stopwatch = Stopwatch.StartNew();
        using var activity = observability.StartAttachActivity(resolvedNamespace, podName ?? string.Empty, containerName);

        if (string.IsNullOrWhiteSpace(podName))
        {
            const string reason = OrchestratorErrorKinds.InvalidArgument;
            activity?.SetTag("event.outcome", "failure");
            activity?.SetTag("error.type", reason);
            observability.RecordAttach(principalAccessor.Current, resolvedNamespace, string.Empty, containerName, null, "failure", reason, stopwatch.Elapsed);
            var ex = new OrchestratorException(reason, "podName is required.");
            return DiagnosticResult.Fail<AttachSession>(
                $"attach_to_pod failed: {ex.Message}",
                new DiagnosticError(ex.ErrorKind, ex.Message),
                BuildAttachRecoveryHint(ex.ErrorKind));
        }

        var request = new AttachRequest(
            Namespace: resolvedNamespace,
            PodName: podName!,
            ContainerName: containerName,
            TtlSeconds: ttlSeconds,
            RequirePreparedTarget: requirePreparedTarget,
            AllowReuseExistingSession: allowReuseExistingSession,
            OwnerSessionId: TryGetServerSessionId(server));

        InvestigationHandle handle;
        try
        {
            handle = await orchestrator.AttachAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OrchestratorException ex)
        {
            activity?.SetTag("event.outcome", "failure");
            activity?.SetTag("error.type", ex.ErrorKind);
            observability.RecordAttach(principalAccessor.Current, resolvedNamespace, podName!, containerName, null, "failure", ex.ErrorKind, stopwatch.Elapsed);
            return DiagnosticResult.Fail<AttachSession>(
                $"attach_to_pod failed: {ex.Message}",
                new DiagnosticError(ex.ErrorKind, ex.Message),
                BuildAttachRecoveryHint(ex.ErrorKind));
        }

        var proxyUrl = handle.State == InvestigationState.Active
            ? options.ProxyBasePath.TrimEnd('/') + "/" + handle.HandleId
            : null;

        InvestigationHandle observedHandle = handle;
        if (handle.State == InvestigationState.Active)
        {
            observedHandle = store.GetById(handle.HandleId) ?? handle;
            if (observedHandle.State != InvestigationState.Active)
            {
                var msg = $"Investigation {handle.HandleId} was closed during attach (observed {observedHandle.State}).";
                activity?.SetTag("event.outcome", "failure");
                activity?.SetTag("error.type", OrchestratorErrorKinds.AttachFailed);
                observability.RecordAttach(principalAccessor.Current, resolvedNamespace, podName!, containerName, handle.HandleId, "failure", OrchestratorErrorKinds.AttachFailed, stopwatch.Elapsed);
                return DiagnosticResult.Fail<AttachSession>(
                    $"attach_to_pod failed: {msg}",
                    new DiagnosticError(OrchestratorErrorKinds.AttachFailed, msg),
                    BuildAttachRecoveryHint(OrchestratorErrorKinds.AttachFailed));
            }
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

        var session = AttachSession.FromHandle(observedHandle, proxyUrl);
        observability.RecordAttach(principalAccessor.Current, session.Namespace, session.PodName, session.TargetContainerName, session.HandleId, "success", "none", stopwatch.Elapsed);
        activity?.SetTag("event.outcome", "success");
        activity?.SetTag("mcp.investigation.handle", session.HandleId);

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
            "list_orchestrator",
            "Run list_orchestrator(kind=\"pods\") in the same namespace to discover the correct pod name."),
        OrchestratorErrorKinds.ContainerNotFound => new NextActionHint(
            "list_orchestrator",
            "Run list_orchestrator(kind=\"pods\") to inspect the target Pod's containers and re-run with the right containerName."),
        OrchestratorErrorKinds.PodNotRunning => new NextActionHint(
            "list_orchestrator",
            "Wait for the Pod to reach phase=Running, or pick a different Pod from list_orchestrator(kind=\"pods\")."),
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

    [RequireScope("orchestrator-attach")]
    [McpServerTool(
        Name = "detach_from_pod",
        Title = "Close an active investigation handle",
        Destructive = true,
        ReadOnly = false,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Closes an investigation produced by attach_to_pod: tears down the cached MCP client, " +
        "stops the port-forward, unbinds every MCP session still pointed at the handle, and marks " +
        "the handle as Closed so subsequent tool calls fall back to local execution. " +
        "Idempotent — calling on a missing/already-terminal handle is a no-op and returns Ok. " +
        "NOTE: the ephemeral diagnostics container CANNOT be removed (Kubernetes constraint); " +
        "it remains on the Pod's spec until the Pod is recreated. Detach therefore only releases " +
        "the orchestrator-side transport, it does not roll the Pod back to its original state.")]
    public static async Task<DiagnosticResult<DetachResult>> DetachFromPod(
        InvestigationCloser closer,
        IInvestigationSessionBinder sessionBinder,
        IInvestigationStore store,
        OrchestratorOptions options,
        IPrincipalAccessor principalAccessor,
        OrchestratorObservability observability,
        McpServer server,
        ILoggerFactory? loggerFactory = null,
        [Description("Investigation handle id returned by attach_to_pod. When omitted, defaults to the handle currently bound to this MCP session.")]
        string? handleId = null,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken; // Close pipeline is best-effort and must finish even if the caller bailed.

        var sessionId = TryGetServerSessionId(server);
        var resolvedHandleId = handleId;
        if (string.IsNullOrWhiteSpace(resolvedHandleId))
        {
            resolvedHandleId = sessionBinder.TryGetHandleId(sessionId);
        }

        if (string.IsNullOrWhiteSpace(resolvedHandleId))
        {
            var result = new DetachResult(
                HandleId: string.Empty,
                Found: false,
                AlreadyTerminal: false,
                PreviousState: null,
                NewState: null,
                UnboundSessionIds: System.Array.Empty<string>());
            observability.RecordDetach(principalAccessor.Current, string.Empty, "manual", "success");
            return DiagnosticResult.Ok(
                result,
                "detach_from_pod: no handleId was supplied and this MCP session has no investigation binding. Nothing to detach.",
                new NextActionHint(
                    "list_orchestrator",
                    "Call list_orchestrator(kind=\"investigations\") to enumerate known handles, then re-run detach_from_pod with an explicit handleId."));
        }

        // B3 (issue #164): require owner-session match before closing — without this any
        // authenticated caller who learns a handle id could DoS another session's
        // investigation. B5.2 (RFC 0001 §2.7) adds an additive escape hatch: bearer
        // principals holding 'orchestrator-admin' bypass the owner check, mirroring the
        // deployment-wide AllowCrossSessionAdmin flag but scoped per-bearer. B5.3
        // (issue #184) routes both checks through the shared policy helper so the
        // legacy flag also emits a one-shot deprecation warning.
        var bypassLogger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger("DotnetDiagnosticsMcp.Server.Security.OrchestratorAdminBypassPolicy");
        var adminBypass = OrchestratorAdminBypassPolicy.IsBypassAllowed(principalAccessor.Current, options, bypassLogger);
        var existing = store.GetById(resolvedHandleId);
        if (existing is not null &&
            existing.OwnerSessionId is not null &&
            !string.Equals(existing.OwnerSessionId, sessionId, StringComparison.Ordinal) &&
            !adminBypass)
        {
            observability.RecordDetach(principalAccessor.Current, resolvedHandleId, "manual", "failure");
            return DiagnosticResult.Fail<DetachResult>(
                summary: $"detach_from_pod: handle '{resolvedHandleId}' is owned by a different MCP session.",
                error: new DiagnosticError(
                    Kind: "PermissionDenied",
                    Message: "Cannot close another MCP session's investigation handle.",
                    Detail: "Re-attach to the pod in this session, or wait for the TTL reaper to retire the handle."));
        }

        var outcome = await closer.CloseAsync(
            resolvedHandleId,
            InvestigationState.Closed).ConfigureAwait(false);

        var detach = new DetachResult(
            HandleId: outcome.HandleId,
            Found: outcome.Found,
            AlreadyTerminal: outcome.AlreadyTerminal,
            PreviousState: outcome.PreviousState,
            NewState: outcome.NewState,
            UnboundSessionIds: outcome.UnboundSessionIds);

        string summary;
        if (!outcome.Found)
        {
            summary = $"detach_from_pod: handle '{resolvedHandleId}' is unknown — no-op (already evicted, never minted, or wrong id).";
        }
        else if (outcome.AlreadyTerminal)
        {
            summary = $"detach_from_pod: handle '{resolvedHandleId}' was already {outcome.PreviousState?.ToString().ToLowerInvariant()}; drained {outcome.UnboundSessionIds.Count} residual session binding(s).";
        }
        else
        {
            summary = $"detach_from_pod: handle '{resolvedHandleId}' transitioned {outcome.PreviousState?.ToString().ToLowerInvariant()}→closed; unbound {outcome.UnboundSessionIds.Count} MCP session(s); ephemeral container remains on the Pod spec.";
        }

        observability.RecordDetach(principalAccessor.Current, outcome.HandleId, "manual", "success");

        return DiagnosticResult.Ok(
            detach,
            summary,
            new NextActionHint(
                "attach_to_pod",
                "Subsequent diagnostic tool calls on this MCP session now resolve locally on the orchestrator host. " +
                "Re-attach with attach_to_pod if you need to continue investigating the same Pod."));
    }

    [RequireScope("orchestrator-attach")]
    [Description(
        "Returns every investigation handle the orchestrator has minted on behalf of this MCP session. " +
        "By default only Active/Attaching handles are returned — pass includeTerminal=true to also see " +
        "Closed/Expired/Failed entries for diagnostic forensics. Other sessions' handles are NEVER " +
        "returned unless the orchestrator is configured with Orchestrator:AllowCrossSessionAdmin=true " +
        "AND the caller passes includeAllSessions=true (operator-audit escape hatch). " +
        "Bearer tokens are never included; the shape matches attach_to_pod's response so the same " +
        "envelope works for both tools.")]
    public static Task<DiagnosticResult<InvestigationListPage>> ListActiveInvestigations(
        IInvestigationStore store,
        OrchestratorOptions options,
        IPrincipalAccessor principalAccessor,
        McpServer? server = null,
        ILoggerFactory? loggerFactory = null,
        [Description("When true, includes handles in terminal states (Closed/Expired/Failed). Default false — only Active/Attaching.")]
        bool includeTerminal = false,
        [Description("Operator-only opt-in (requires Orchestrator:AllowCrossSessionAdmin=true). When true, returns handles minted by other MCP sessions. Default false — every caller sees only its own handles.")]
        bool includeAllSessions = false,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var snapshot = store.Snapshot();

        // H6 (issue #164): determine the caller's MCP session id once. Handles
        // owned by other sessions are filtered out below unless the admin
        // escape hatch is active. A null sessionId (stdio / unit test) is
        // treated as "system caller" and matches only handles that were minted
        // without an owner (OwnerSessionId == null) — those are exactly the
        // handles created on the same un-scoped transport, so the secure
        // behavior degrades sensibly.
        var callerSessionId = TryGetServerSessionId(server!);
        // B5.2 (RFC 0001 §2.7) + B5.3 (issue #184): admin listing requires EITHER the legacy
        // AllowCrossSessionAdmin deployment flag OR the per-bearer 'orchestrator-admin'
        // modifier scope. Both are operator-grade. The shared policy helper logs a one-shot
        // deprecation warning the first time the legacy flag is the bypass path.
        var bypassLogger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger("DotnetDiagnosticsMcp.Server.Security.OrchestratorAdminBypassPolicy");
        var adminBypass = OrchestratorAdminBypassPolicy.IsBypassAllowed(principalAccessor.Current, options, bypassLogger);
        var adminListing = includeAllSessions && adminBypass;

        // B3 review (issue #164): when not in admin mode, counts and TotalKnown
        // must be computed over the *visible* set only. Returning a global
        // TotalKnown / state counts would re-introduce the enumeration side
        // channel that H6 closes: an attacker could attach a tiny probe handle
        // and watch the counts move as other sessions attached / detached.
        int active = 0, attaching = 0, closed = 0, expired = 0, failed = 0;
        int visibleTotal = 0;
        foreach (var h in snapshot)
        {
            if (!adminListing && !IsOwnedByCaller(h, callerSessionId)) continue;
            visibleTotal++;
            switch (h.State)
            {
                case InvestigationState.Active: active++; break;
                case InvestigationState.Attaching: attaching++; break;
                case InvestigationState.Closed: closed++; break;
                case InvestigationState.Expired: expired++; break;
                case InvestigationState.Failed: failed++; break;
            }
        }

        var proxyPrefix = options.ProxyBasePath.TrimEnd('/');
        var items = new List<AttachSession>(snapshot.Count);
        var redactedCount = 0;
        foreach (var h in snapshot)
        {
            if (!includeTerminal && IsTerminalState(h.State)) continue;
            if (!adminListing && !IsOwnedByCaller(h, callerSessionId))
            {
                redactedCount++;
                continue;
            }
            var proxyUrl = h.State == InvestigationState.Active
                ? proxyPrefix + "/" + h.HandleId
                : null;
            items.Add(AttachSession.FromHandle(h, proxyUrl));
        }

        items.Sort(static (a, b) => b.AttachedAt.CompareTo(a.AttachedAt));

        var page = new InvestigationListPage(
            Items: items,
            TotalKnown: visibleTotal,
            ActiveCount: active,
            AttachingCount: attaching,
            ClosedCount: closed,
            ExpiredCount: expired,
            FailedCount: failed);

        string summary;
        if (items.Count == 0)
        {
            summary = includeTerminal
                ? "list_active_investigations: no investigations owned by this MCP session."
                : "list_active_investigations: no Active/Attaching investigations owned by this MCP session. Pass includeTerminal=true to inspect Closed/Expired/Failed history.";
        }
        else
        {
            summary = $"list_active_investigations: {items.Count} returned (active={active}, attaching={attaching}" +
                      (includeTerminal ? $", closed={closed}, expired={expired}, failed={failed}" : "") +
                      $"; totalKnown={visibleTotal}" +
                      (adminListing ? "; admin listing (includeAllSessions=true)" : string.Empty) +
                      ").";
        }

        // redactedCount is intentionally consumed internally for logging only.
        // We do NOT include it in the user-visible summary because that would
        // still leak the existence of other sessions' handles.
        _ = redactedCount;

        return Task.FromResult(DiagnosticResult.Ok(
            page,
            summary,
            new NextActionHint(
                "detach_from_pod",
                items.Count == 0
                    ? "Call attach_to_pod to open a new investigation."
                    : "Pass an item's handleId to detach_from_pod to close it explicitly, or wait for the TTL reaper.")));
    }

    private static bool IsOwnedByCaller(InvestigationHandle handle, string? callerSessionId)
    {
        // Handles minted without an owner (stdio attach, framework calls that had
        // no session id) are reachable by every authenticated caller — this is
        // intentional to preserve dev-time stdio ergonomics, where there is only
        // ever one human driving the orchestrator. In HTTP deployments every
        // attach happens through a session-bearing transport, so the owner is
        // always populated and per-session isolation applies.
        if (handle.OwnerSessionId is null) return true;
        return string.Equals(handle.OwnerSessionId, callerSessionId, StringComparison.Ordinal);
    }

    private static bool IsTerminalState(InvestigationState state)
        => state is InvestigationState.Closed or InvestigationState.Expired or InvestigationState.Failed;
}

/// <summary>
/// Client-safe result of <see cref="OrchestratorTools.DetachFromPod"/>. Mirrors the
/// internal <c>InvestigationCloseOutcome</c> minus log strings.
/// </summary>
public sealed record DetachResult(
    string HandleId,
    bool Found,
    bool AlreadyTerminal,
    InvestigationState? PreviousState,
    InvestigationState? NewState,
    IReadOnlyCollection<string> UnboundSessionIds);
