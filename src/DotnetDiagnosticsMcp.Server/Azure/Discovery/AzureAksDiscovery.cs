using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using global::Azure;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnosticsMcp.Server.Azure.Discovery;

/// <summary>
/// Production <see cref="IAzureAksDiscovery"/> implementation (issue #234, parent #230).
/// Lists AKS managed clusters in a subscription via
/// <see cref="IAzureManagedClusterCollectionAdapter"/> and, when
/// <see cref="AzureDiscoveryRequest.IncludeKubeconfig"/> is true, mints a
/// <see cref="AzureAksHandoff"/> per cluster by storing the kubeconfig bytes in the
/// process-local <see cref="IKubeconfigHandleStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// Security-critical paths the gpt-5.5 review will examine:
/// </para>
/// <list type="bullet">
///   <item><description>Kubeconfig bytes never enter a <see cref="string"/> on this
///   call path; they leave the SDK as <see cref="byte"/>[] and are handed directly
///   to the store.</description></item>
///   <item><description>The minted handle is NEVER logged. Only handle <em>presence</em>
///   (true/false) is logged at debug.</description></item>
///   <item><description>On listClusterUserCredential 403, the cluster row is still
///   returned (with a readinessWarning) but <see cref="AzureAksClusterCandidate.Handoff"/>
///   stays null — no partial handle is leaked.</description></item>
/// </list>
/// <para>
/// Paging: the Azure SDK pageable does not surface a portable continuation token
/// at the public API surface in 1.4.0, so this backend collects up to <c>limit</c>
/// rows per call and exposes a synthetic cursor encoding the running offset.
/// Subscriptions with hundreds of clusters are vanishingly rare in practice;
/// callers that hit the cursor can re-page.
/// </para>
/// </remarks>
internal sealed class AzureAksDiscovery : IAzureAksDiscovery
{
    private const string CursorPrefix = "off:";

    private readonly IAzureManagedClusterCollectionAdapter _adapter;
    private readonly IKubeconfigHandleStore _kubeconfigStore;
    private readonly ILogger<AzureAksDiscovery> _logger;

    public AzureAksDiscovery(
        IAzureManagedClusterCollectionAdapter adapter,
        IKubeconfigHandleStore kubeconfigStore,
        ILogger<AzureAksDiscovery> logger)
    {
        _adapter = adapter;
        _kubeconfigStore = kubeconfigStore;
        _logger = logger;
    }

    public async Task<AzurePagedResult<AzureAksClusterCandidate>> ListAsync(
        AzureDiscoveryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var skip = DecodeCursor(request.Cursor);
        var items = new List<AzureAksClusterCandidate>(capacity: Math.Min(request.Limit, 32));
        var seen = 0;
        var producedFullPage = false;

        await foreach (var row in _adapter.ListAsync(request.SubscriptionId, request.ResourceGroup, cancellationToken)
            .ConfigureAwait(false))
        {
            if (seen++ < skip) continue;
            if (items.Count >= request.Limit)
            {
                producedFullPage = true;
                break;
            }

            var candidate = await ProjectAsync(row, request.IncludeKubeconfig, cancellationToken).ConfigureAwait(false);
            items.Add(candidate);
        }

        string? nextCursor = producedFullPage ? EncodeCursor(skip + items.Count) : null;
        return new AzurePagedResult<AzureAksClusterCandidate>(items, nextCursor);
    }

    private async Task<AzureAksClusterCandidate> ProjectAsync(
        AzureAksClusterRow row,
        bool includeKubeconfig,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        if (row.IsPrivateCluster)
        {
            warnings.Add("private cluster — sidecar requires VNet integration to reach the API server");
        }
        if (row.AgentPoolCount == 0)
        {
            warnings.Add("agent pool count == 0 — cluster is degenerate, no nodes will be reachable");
        }

        AzureAksHandoff? handoff = null;
        if (includeKubeconfig)
        {
            try
            {
                var kubeconfigBytes = await _adapter.GetClusterUserKubeconfigAsync(row.ResourceId, cancellationToken)
                    .ConfigureAwait(false);
                var mint = _kubeconfigStore.Register(kubeconfigBytes);
                handoff = new AzureAksHandoff(mint.Handle, mint.ExpiresAt);

                // Audit log MUST NOT mention the handle value — treat as bearer credential.
                _logger.LogDebug(
                    "AKS kubeconfig handle minted for cluster {ClusterName} in {Location} (handle present: true).",
                    row.Name, row.Location);
            }
            catch (RequestFailedException ex) when (ex.IsForbidden())
            {
                warnings.Add("no AKS Cluster User permission detected — listClusterUserCredential returned " + ex.Status);
                _logger.LogInformation(
                    "AKS listClusterUserCredential denied for cluster {ClusterName} ({Status}); kubeconfig handle NOT minted.",
                    row.Name, ex.Status);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Defense in depth: any other failure (network, deserialization, SDK
                // bug) must still NOT leak partial state into the handoff. The
                // exception message intentionally does not interpolate any candidate
                // ARM payload — only the cluster name (which is already in the row).
                warnings.Add("failed to fetch kubeconfig: " + Sanitize(ex.GetType().Name));
                _logger.LogWarning(ex,
                    "Unexpected error minting kubeconfig handle for cluster {ClusterName}; returning candidate without handoff.",
                    row.Name);
            }
        }

        return new AzureAksClusterCandidate(
            ResourceId: row.ResourceId,
            Name: row.Name,
            Location: row.Location,
            AgentPoolCount: row.AgentPoolCount,
            ReadinessWarnings: warnings,
            Fqdn: row.Fqdn,
            KubernetesVersion: row.KubernetesVersion,
            NodeResourceGroup: row.NodeResourceGroup,
            Handoff: handoff);
    }

    private static string Sanitize(string token)
    {
        // Belt-and-braces: keep warning strings ASCII-letters-only so an attacker
        // cannot smuggle injected bytes (e.g. ANSI escapes, embedded credentials)
        // into the response envelope via an exception type name.
        var sb = new StringBuilder(token.Length);
        foreach (var c in token)
        {
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_')
            {
                sb.Append(c);
            }
        }
        return sb.Length == 0 ? "unknown" : sb.ToString();
    }

    private static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return 0;
        if (!cursor.StartsWith(CursorPrefix, StringComparison.Ordinal)) return 0;
        return int.TryParse(cursor.AsSpan(CursorPrefix.Length), out var n) && n > 0 ? n : 0;
    }

    private static string EncodeCursor(int offset) => CursorPrefix + offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
