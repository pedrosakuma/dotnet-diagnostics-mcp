using System.Collections.Generic;

namespace DotnetDiagnosticsMcp.Server.Orchestrator;

/// <summary>
/// Configuration surface for the central Kubernetes orchestrator (issue #20, phase P3).
/// Bound from the <c>Orchestrator</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// The orchestrator is OFF by default. When <see cref="Enabled"/> is false the
/// orchestrator tools are not registered and the server behaves identically to a
/// single-target sidecar.
/// </para>
/// <para>
/// <see cref="NamespaceAllowlist"/> is enforced as a deny-by-omission policy: only
/// namespaces explicitly listed (or matched by a single <c>"*"</c> entry) are reachable
/// from <c>list_pods</c> / <c>attach_to_pod</c>. The wildcard exists only for
/// cluster-wide deployments and must be opted into deliberately — see
/// docs/central-orchestrator-design.md §2.3 and §6.
/// </para>
/// <para>
/// <see cref="LabelKeyAllowlist"/> caps the keys a caller may reference in a
/// <c>labelSelector</c>. This is the design's "SelectorRejected" guard against an LLM
/// stringing together unbounded selectors that escape the orchestrator's intended scope.
/// An empty list means "any label key accepted"; populate it in production deployments.
/// </para>
/// </remarks>
public sealed class OrchestratorOptions
{
    /// <summary>Default configuration section name.</summary>
    public const string SectionName = "Orchestrator";

    /// <summary>Default Pod label that opts a target into orchestrator discovery.</summary>
    public const string DefaultPreparedLabelKey = "diagnostics.dotnet.io/prepared";

    /// <summary>
    /// Master switch. When false (default) the orchestrator tools (<c>list_pods</c>,
    /// <c>attach_to_pod</c>, …) are NOT registered with the MCP server.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Allowlist of namespaces the orchestrator may touch. A single entry of <c>"*"</c>
    /// means "every namespace" (cluster-wide) — only set in deployments where the
    /// orchestrator's ServiceAccount also has cluster-scoped RBAC. Default: empty (no
    /// namespaces reachable, so an enabled-but-unconfigured orchestrator fails closed).
    /// </summary>
    public IList<string> NamespaceAllowlist { get; } = new List<string>();

    /// <summary>
    /// Default namespace used when a tool call omits <c>namespace</c>. Must be in
    /// <see cref="NamespaceAllowlist"/> (or the allowlist must be <c>"*"</c>).
    /// </summary>
    public string? DefaultNamespace { get; set; }

    /// <summary>
    /// Optional cap on label-selector keys callers may use. Empty list = any key. This
    /// is enforced before forwarding the selector to the Kubernetes API to limit blast
    /// radius. Common safe values: <c>app</c>, <c>app.kubernetes.io/name</c>.
    /// </summary>
    public IList<string> LabelKeyAllowlist { get; } = new List<string>();

    /// <summary>
    /// Label key indicating a Pod is intentionally prepared for diagnostics attach
    /// (mounts shared /tmp emptyDir, pins UID/GID, sets DOTNET_EnableDiagnostics=1).
    /// Default: <see cref="DefaultPreparedLabelKey"/>.
    /// </summary>
    public string PreparedLabelKey { get; set; } = DefaultPreparedLabelKey;

    /// <summary>
    /// When true (default), a Pod must opt in via the prepared label to be reachable.
    /// When false, the orchestrator falls back to a best-effort heuristic on Pod spec
    /// (shared /tmp emptyDir + non-root UID + DOTNET_EnableDiagnostics=1).
    /// </summary>
    public bool RequirePreparedLabel { get; set; } = true;

    /// <summary>
    /// Hard cap on the <c>limit</c> parameter callers may request from <c>list_pods</c>.
    /// Default 500.
    /// </summary>
    public int MaxListLimit { get; set; } = 500;

    /// <summary>
    /// Container image the orchestrator injects as the ephemeral diagnostics container
    /// on <c>attach_to_pod</c>. Must already be reachable by the target node's pull
    /// configuration. Default: <c>ghcr.io/pedrosakuma/dotnet-diagnostics-mcp:latest</c>;
    /// override in production to pin a digest.
    /// </summary>
    public string EphemeralContainerImage { get; set; } = "ghcr.io/pedrosakuma/dotnet-diagnostics-mcp:latest";

    /// <summary>
    /// Prefix applied to every injected ephemeral container's name. Lets operators
    /// distinguish orchestrator-installed diagnostics containers from other ephemeral
    /// containers (e.g. <c>kubectl debug</c>) when auditing a Pod's <c>ephemeralContainerStatuses</c>.
    /// Default: <c>dotnet-dbg-mcp-</c>.
    /// </summary>
    public string EphemeralContainerNamePrefix { get; set; } = "dotnet-dbg-mcp-";

    /// <summary>
    /// Maximum time to wait for the injected ephemeral container to reach a running
    /// state after the patch is accepted. On expiry, <c>attach_to_pod</c> returns
    /// <see cref="OrchestratorErrorKinds.AttachTimeout"/>. Default: 60 seconds.
    /// </summary>
    public int AttachReadinessTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Default investigation TTL (seconds) when the caller omits <c>ttlSeconds</c>.
    /// Tracked on the <see cref="Investigations.InvestigationHandle"/> so the reaper
    /// (P4) can close idle sessions. Default: 1800 (30 minutes).
    /// </summary>
    public int DefaultInvestigationTtlSeconds { get; set; } = 1800;

    /// <summary>
    /// TCP port the ephemeral diagnostics container listens on inside the target Pod.
    /// Must match the <c>ASPNETCORE_URLS</c> binding the orchestrator injects on attach
    /// (see <see cref="Investigations.KubernetesPodAttachOrchestrator"/>). Default: 5130.
    /// </summary>
    public int ProxyPodPort { get; set; } = 5130;

    /// <summary>
    /// URL path prefix the orchestrator mounts the reverse proxy under. Subsequent
    /// diagnostic tool calls for an investigation are routed via
    /// <c>{ProxyBasePath}/{handleId}/{**rest}</c>. Default: <c>/proxy</c>.
    /// </summary>
    public string ProxyBasePath { get; set; } = "/proxy";
}
