using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Tools.Dispatch;
using DotnetDiagnosticsMcp.Server.Azure;
using DotnetDiagnosticsMcp.Server.Azure.Discovery;
using DotnetDiagnosticsMcp.Server.Security;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Tools;

/// <summary>
/// Azure discovery v1 (issue #232, parent #230). Single <c>kind</c>-discriminated
/// tool that enumerates .NET workload candidates in an Azure subscription across
/// three platforms — App Service (<c>webapps</c>), Container Apps
/// (<c>containerapps</c>), and AKS (<c>aksclusters</c>).
/// </summary>
/// <remarks>
/// <para>This PR (#232) ships only the contract: the tool, the three backend
/// interfaces and the response envelope. The real backends arrive in #233
/// (App Service + Container Apps) and #234 (AKS). Default backend implementations
/// throw <see cref="NotImplementedException"/> on every call so any wiring
/// regression surfaces immediately.</para>
/// <para>Mirrors the style of <see cref="ListOrchestratorTool"/>: discriminator
/// validation via <see cref="DiscriminatorDispatch"/>, structured envelope with
/// <c>data.kind</c> echo and exactly one populated payload, scope enforcement via
/// <see cref="RequireScopeAttribute"/>.</para>
/// </remarks>
[McpServerToolType]
public sealed class DiscoverAzureTool
{
    public const string KindWebApps = "webapps";
    public const string KindContainerApps = "containerapps";
    public const string KindAksClusters = "aksclusters";

    /// <summary>Required scope for this tool. Distinct from the orchestrator scopes
    /// because Azure ARM credentials are a different trust domain.</summary>
    public const string Scope = "azure-discovery";

    /// <summary>Hard ceiling on the <c>limit</c> parameter, mirroring the orchestrator
    /// <c>list_orchestrator</c> tool's default upper bound. Backends MAY clamp further.</summary>
    public const int MaxLimit = 200;

    private const int DefaultLimit = 100;

    private static readonly IReadOnlyList<string> AllowedKinds =
        new[] { KindWebApps, KindContainerApps, KindAksClusters };

    [RequireScope(Scope)]
    [McpServerTool(
        Name = "discover_azure",
        Title = "Discover .NET workload candidates in an Azure subscription",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Discover .NET workloads in an Azure subscription. Pass kind='webapps' to enumerate " +
        "App Service sites; kind='containerapps' for Azure Container Apps; kind='aksclusters' " +
        "for AKS managed clusters. Read-only ARM listing — never returns raw kubeconfig (AKS " +
        "responses carry an opaque handle when includeKubeconfig=true). Backends for webapps/" +
        "containerapps land in #233; AKS in #234.")]
    public async Task<DiagnosticResult<DiscoverAzureResult>> DiscoverAzureAsync(
        IAzureWebAppsDiscovery webApps,
        IAzureContainerAppsDiscovery containerApps,
        IAzureAksDiscovery aksClusters,
        AzureDiscoveryOptions options,
        [Description("Azure subscription id (required)")] string subscriptionId,
        [Description("Kind: webapps | containerapps | aksclusters")] string kind = KindWebApps,
        [Description("Optional resource group filter")] string? resourceGroup = null,
        [Description("Include stopped resources (default false)")] bool includeStopped = false,
        [Description("Page size (default 100)")] int limit = DefaultLimit,
        [Description("Pagination cursor")] string? cursor = null,
        [Description("[aksclusters only] return a kubeconfig handle (never raw kubeconfig)")] bool includeKubeconfig = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(webApps);
        ArgumentNullException.ThrowIfNull(containerApps);
        ArgumentNullException.ThrowIfNull(aksClusters);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            // The discover_azure tool is unregistered when AzureDiscovery:Enabled=false,
            // but a host that wires the type up manually must still see a structured
            // failure rather than throwing through the backend stub.
            const string msg = "discover_azure: Azure discovery is disabled (AzureDiscovery:Enabled=false).";
            return DiagnosticResult.Fail<DiscoverAzureResult>(
                msg,
                new DiagnosticError(AzureDiscoveryErrorKinds.AzureDiscoveryDisabled, msg),
                new NextActionHint(
                    "discover_azure",
                    "Set AzureDiscovery:Enabled=true on the MCP server and re-deploy."));
        }

        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            const string msg = "discover_azure: 'subscriptionId' is required.";
            return DiagnosticResult.Fail<DiscoverAzureResult>(
                msg,
                new DiagnosticError(AzureDiscoveryErrorKinds.InvalidArgument, msg, "subscriptionId"));
        }

        if (!DiscriminatorDispatch.TryValidate<DiscoverAzureResult>(
                kind, AllowedKinds, parameterName: "kind",
                out var canonicalKind, out var failure))
        {
            return failure!;
        }

        var clampedLimit = limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);

        var request = new AzureDiscoveryRequest(
            SubscriptionId: subscriptionId.Trim(),
            ResourceGroup: string.IsNullOrWhiteSpace(resourceGroup) ? null : resourceGroup.Trim(),
            IncludeStopped: includeStopped,
            Limit: clampedLimit,
            Cursor: cursor,
            IncludeKubeconfig: includeKubeconfig);

        switch (canonicalKind)
        {
            case KindWebApps:
            {
                var page = await webApps.ListAsync(request, cancellationToken).ConfigureAwait(false);
                return Ok(new DiscoverAzureResult(KindWebApps, page, null, null), canonicalKind, page.Items.Count);
            }
            case KindContainerApps:
            {
                var page = await containerApps.ListAsync(request, cancellationToken).ConfigureAwait(false);
                return Ok(new DiscoverAzureResult(KindContainerApps, null, page, null), canonicalKind, page.Items.Count);
            }
            case KindAksClusters:
            {
                var page = await aksClusters.ListAsync(request, cancellationToken).ConfigureAwait(false);
                return Ok(new DiscoverAzureResult(KindAksClusters, null, null, page), canonicalKind, page.Items.Count);
            }
            default:
                // Unreachable — DiscriminatorDispatch.TryValidate restricts canonicalKind to AllowedKinds.
                throw new InvalidOperationException($"Unhandled canonical kind '{canonicalKind}'.");
        }
    }

    private static DiagnosticResult<DiscoverAzureResult> Ok(
        DiscoverAzureResult data, string kind, int itemCount)
    {
        var summary = $"discover_azure(kind={kind}): {itemCount} candidate(s).";
        return DiagnosticResult.Ok(data, summary);
    }
}
