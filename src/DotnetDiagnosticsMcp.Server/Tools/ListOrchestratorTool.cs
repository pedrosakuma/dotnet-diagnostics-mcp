using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Tools.Dispatch;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;
using DotnetDiagnosticsMcp.Server.Security;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Tools;

/// <summary>
/// RFC 0002 §4.7 consolidation (issue #212): merges the two orchestrator listing
/// endpoints — <c>list_pods</c> and <c>list_active_investigations</c> — into a single
/// <c>list_orchestrator(kind=...)</c> tool. RFC 0002 §7.3 #9 / #213 — the legacy tools
/// have been deleted in the alias removal wave; this is now the sole listing entry-point.
/// </summary>
/// <remarks>
/// <para><c>attach_to_pod</c> / <c>detach_from_pod</c> are deliberately NOT merged —
/// the orchestrator design treats them as distinct side-effect boundaries (RFC §4.7).
/// </para>
/// <para>Authorization is split by <c>kind</c> per RFC §4.7: <c>pods</c> keeps the
/// <c>orchestrator-list</c> scope, while <c>investigations</c> requires the more
/// privileged <c>orchestrator-attach</c> scope. The MCP filter only sees the
/// declared <see cref="RequireAnyScopeAttribute"/> union; the per-kind tightening
/// is enforced inside the tool body so callers cannot use a <c>list</c>-only token
/// to enumerate investigation handles.</para>
/// </remarks>
[McpServerToolType]
public sealed class ListOrchestratorTool
{
    public const string KindPods = "pods";
    public const string KindInvestigations = "investigations";

    private static readonly IReadOnlyList<string> AllowedKinds = new[] { KindPods, KindInvestigations };

    [RequireAnyScope("orchestrator-list", "orchestrator-attach")]
    [McpServerTool(
        Name = "list_orchestrator",
        Title = "List orchestrator entities (Pods or active investigations)",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "RFC 0002 §4.7 consolidation of the orchestrator listing surface. Pass kind='pods' to enumerate " +
        "candidate Pods in allowed namespaces (replaces list_pods — preserves namespace/labelSelector/" +
        "fieldSelector/containerName/preparedOnly/includeNotReady/limit/cursor); pass kind='investigations' " +
        "to enumerate investigation handles minted on behalf of this MCP session (replaces " +
        "list_active_investigations — preserves includeTerminal/includeAllSessions). Read-only; never injects " +
        "an ephemeral container and never returns bearer tokens. attach_to_pod / detach_from_pod are " +
        "intentionally NOT folded in — they remain explicit per the orchestrator design.")]
    public static async Task<DiagnosticResult<ListOrchestratorResult>> ListOrchestrator(
        IPodInventory inventory,
        IInvestigationStore store,
        OrchestratorOptions options,
        IPrincipalAccessor principalAccessor,
        McpServer? server = null,
        ILoggerFactory? loggerFactory = null,
        [Description("Discriminator: 'pods' (candidate Pods for attach) or 'investigations' (handles minted by this session). Case-sensitive.")]
        string kind = KindPods,
        // ---- kind=pods ---------------------------------------------------------------
        [Description("kind=pods: Kubernetes namespace to list from. When omitted, the orchestrator's DefaultNamespace is used.")]
        string? @namespace = null,
        [Description("kind=pods: Optional Kubernetes label selector (e.g. 'app=api,env=prod').")]
        string? labelSelector = null,
        [Description("kind=pods: Optional Kubernetes field selector (e.g. 'status.phase=Running').")]
        string? fieldSelector = null,
        [Description("kind=pods: Optional container name. Defaults to the first container in each Pod's spec.")]
        string? containerName = null,
        [Description("kind=pods: When true (default), only Pods that are diagnostically prepared are returned.")]
        bool preparedOnly = true,
        [Description("kind=pods: When false (default), Pods that are not Ready are filtered out.")]
        bool includeNotReady = false,
        [Description("kind=pods: Max rows per page (default 100, clamped to the orchestrator's MaxListLimit).")]
        int limit = 100,
        [Description("kind=pods: Opaque continuation cursor from a prior call's nextCursor. Null for the first page.")]
        string? cursor = null,
        // ---- kind=investigations ----------------------------------------------------
        [Description("kind=investigations: When true, includes handles in terminal states (Closed/Expired/Failed). Default false — only Active/Attaching.")]
        bool includeTerminal = false,
        [Description("kind=investigations: Operator-only opt-in (requires Orchestrator:AllowCrossSessionAdmin=true OR the 'orchestrator-admin' scope). When true, returns handles minted by other MCP sessions.")]
        bool includeAllSessions = false,
        CancellationToken cancellationToken = default)
    {
        if (!DiscriminatorDispatch.TryValidate<ListOrchestratorResult>(
                kind, AllowedKinds, parameterName: "kind",
                out var canonicalKind, out var failure))
        {
            return failure!;
        }

        if (!options.Enabled)
        {
            // The orchestrator gate keeps the tool unregistered when disabled, but a host that
            // wires the type up manually (or a future per-request toggle) must still see a
            // structured failure rather than a partial answer.
            var msg = "list_orchestrator: orchestrator mode is disabled (Orchestrator:Enabled=false).";
            return DiagnosticResult.Fail<ListOrchestratorResult>(
                msg,
                new DiagnosticError(OrchestratorErrorKinds.OrchestratorDisabled, msg),
                new NextActionHint(
                    "list_orchestrator",
                    "Set Orchestrator:Enabled=true on the MCP server and re-deploy."));
        }

        // Per-kind scope tightening — see RFC §4.7. The [RequireAnyScope] filter at dispatch
        // accepts callers holding either listing scope; these guards make sure neither kind
        // becomes a back-door to the other's data by switching the discriminator.
        if (canonicalKind == KindInvestigations)
        {
            var principal = principalAccessor.Current;
            if (principal is not null && !principal.HasScope("orchestrator-attach"))
            {
                var msg = "list_orchestrator(kind=investigations) requires the 'orchestrator-attach' scope.";
                return DiagnosticResult.Fail<ListOrchestratorResult>(
                    msg,
                    new DiagnosticError(OrchestratorErrorKinds.PermissionDenied, msg),
                    new NextActionHint(
                        "list_orchestrator",
                        "Use kind='pods' (orchestrator-list scope) or grant the token 'orchestrator-attach'.",
                        new Dictionary<string, object?> { ["kind"] = KindPods }));
            }
        }
        else if (canonicalKind == KindPods)
        {
            var principal = principalAccessor.Current;
            if (principal is not null && !principal.HasScope("orchestrator-list"))
            {
                var msg = "list_orchestrator(kind=pods) requires the 'orchestrator-list' scope.";
                return DiagnosticResult.Fail<ListOrchestratorResult>(
                    msg,
                    new DiagnosticError(OrchestratorErrorKinds.PermissionDenied, msg),
                    new NextActionHint(
                        "list_orchestrator",
                        "Grant the token 'orchestrator-list', or use kind='investigations' (orchestrator-attach scope).",
                        new Dictionary<string, object?> { ["kind"] = KindInvestigations }));
            }
        }

        if (canonicalKind == KindPods)
        {
            var inner = await OrchestratorTools.ListPods(
                inventory,
                @namespace,
                labelSelector,
                fieldSelector,
                containerName,
                preparedOnly,
                includeNotReady,
                limit,
                cursor,
                cancellationToken).ConfigureAwait(false);

            return Project(inner, KindPods, page => new ListOrchestratorResult(KindPods, Pods: page, Investigations: null));
        }
        else
        {
            var inner = await OrchestratorTools.ListActiveInvestigations(
                store,
                options,
                principalAccessor,
                server,
                loggerFactory,
                includeTerminal,
                includeAllSessions,
                cancellationToken).ConfigureAwait(false);

            return Project(inner, KindInvestigations, page => new ListOrchestratorResult(KindInvestigations, Pods: null, Investigations: page));
        }
    }

    private static DiagnosticResult<ListOrchestratorResult> Project<TInner>(
        DiagnosticResult<TInner> inner,
        string kind,
        System.Func<TInner, ListOrchestratorResult> wrap)
    {
        if (inner.IsError)
        {
            return new DiagnosticResult<ListOrchestratorResult>(inner.Summary, inner.Hints, inner.Error)
            {
                Data = null,
                Handle = inner.Handle,
                HandleExpiresAt = inner.HandleExpiresAt,
                ResolvedProcess = inner.ResolvedProcess,
            };
        }

        var data = inner.Data is null ? new ListOrchestratorResult(kind, null, null) : wrap(inner.Data);
        return new DiagnosticResult<ListOrchestratorResult>(inner.Summary, inner.Hints, inner.Error)
        {
            Data = data,
            Handle = inner.Handle,
            HandleExpiresAt = inner.HandleExpiresAt,
            ResolvedProcess = inner.ResolvedProcess,
        };
    }
}

/// <summary>
/// Discriminated payload for <see cref="ListOrchestratorTool.ListOrchestrator"/>. Exactly
/// one of <see cref="Pods"/> / <see cref="Investigations"/> is populated, matching the
/// requested <see cref="Kind"/>; the other is always <c>null</c> so JSON consumers can
/// branch without re-running the tool.
/// </summary>
public sealed record ListOrchestratorResult(
    string Kind,
    PodCandidatePage? Pods,
    InvestigationListPage? Investigations);
