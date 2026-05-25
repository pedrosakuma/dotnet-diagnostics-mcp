using System;
using System.Collections.Generic;

namespace DotnetDiagnosticsMcp.Server.Azure.Discovery;

/// <summary>
/// Tool-contract request shape shared by every Azure discovery backend
/// (<see cref="IAzureWebAppsDiscovery"/>, <see cref="IAzureContainerAppsDiscovery"/>,
/// <see cref="IAzureAksDiscovery"/>). Lives in the contract PR (#232) so the
/// backend PRs (#233 App Service + Container Apps, #234 AKS) can be written
/// against a stable seam.
/// </summary>
/// <param name="SubscriptionId">Required Azure subscription id (string form GUID).</param>
/// <param name="ResourceGroup">Optional resource-group filter; null means "every group in the subscription".</param>
/// <param name="IncludeStopped">When false (default) backends MUST filter out stopped / failed resources.</param>
/// <param name="Limit">Page size already clamped by the tool to a sane maximum.</param>
/// <param name="Cursor">Opaque continuation cursor from a prior page, or null for the first page.</param>
/// <param name="IncludeKubeconfig">AKS-only flag: when true, the AKS backend mints a kubeconfig handle (never raw kubeconfig).</param>
public sealed record AzureDiscoveryRequest(
    string SubscriptionId,
    string? ResourceGroup,
    bool IncludeStopped,
    int Limit,
    string? Cursor,
    bool IncludeKubeconfig);

/// <summary>
/// Page of <typeparamref name="T"/> rows plus an optional opaque continuation cursor.
/// Mirrors the orchestrator <c>PodCandidatePage</c> shape so the LLM-side paging
/// idiom is identical across tools.
/// </summary>
public sealed record AzurePagedResult<T>(IReadOnlyList<T> Items, string? NextCursor = null);

/// <summary>
/// One Azure App Service candidate. Populated by the App Service backend in #233.
/// </summary>
/// <param name="ResourceId">Full ARM resource id (e.g. <c>/subscriptions/.../sites/foo</c>).</param>
/// <param name="Name">Site name.</param>
/// <param name="Location">Azure region (e.g. <c>westeurope</c>).</param>
/// <param name="State">Site state — usually <c>Running</c> or <c>Stopped</c>.</param>
/// <param name="Kind">Raw site kind (<c>app</c>, <c>app,linux</c>, <c>functionapp</c>, …).</param>
/// <param name="ReadinessWarnings">Backend-emitted warnings the LLM should surface (e.g. "no SCM endpoint reachable").</param>
/// <param name="DefaultHostName">Default hostname (e.g. <c>foo.azurewebsites.net</c>); null when unknown.</param>
/// <param name="RuntimeStack">Stack identifier such as <c>linuxFxVersion</c> / <c>netFrameworkVersion</c>; null when undetectable.</param>
/// <param name="RuntimeVersion">Resolved runtime version when known.</param>
/// <param name="InstanceCount">Effective instance count when surfaced by ARM; null otherwise.</param>
public sealed record AzureWebAppCandidate(
    string ResourceId,
    string Name,
    string Location,
    string State,
    string Kind,
    IReadOnlyList<string> ReadinessWarnings,
    string? DefaultHostName = null,
    string? RuntimeStack = null,
    string? RuntimeVersion = null,
    int? InstanceCount = null);

/// <summary>
/// One Azure Container Apps candidate. Populated by the Container Apps backend in #233.
/// </summary>
/// <param name="ResourceId">Full ARM resource id of the container app.</param>
/// <param name="Name">Container app name.</param>
/// <param name="Location">Azure region.</param>
/// <param name="ContainerImages">Image references declared on the latest revision template.</param>
/// <param name="ProvisioningState">ARM provisioning state (e.g. <c>Succeeded</c>, <c>Failed</c>).</param>
/// <param name="RunningState">Runtime state (e.g. <c>Running</c>, <c>Stopped</c>).</param>
/// <param name="ReadinessWarnings">Backend-emitted warnings (e.g. "no ingress").</param>
/// <param name="LatestRevisionFqdn">FQDN of the latest revision when available.</param>
/// <param name="MinReplicas">Configured minimum replica count; null when not set on the revision.</param>
/// <param name="MaxReplicas">Configured maximum replica count; null when not set on the revision.</param>
public sealed record AzureContainerAppCandidate(
    string ResourceId,
    string Name,
    string Location,
    IReadOnlyList<string> ContainerImages,
    string ProvisioningState,
    string RunningState,
    IReadOnlyList<string> ReadinessWarnings,
    string? LatestRevisionFqdn = null,
    int? MinReplicas = null,
    int? MaxReplicas = null);

/// <summary>
/// One AKS cluster candidate. Populated by the AKS backend in #234.
/// </summary>
/// <param name="ResourceId">Full ARM resource id of the managed cluster.</param>
/// <param name="Name">Cluster name.</param>
/// <param name="Location">Azure region.</param>
/// <param name="AgentPoolCount">Number of agent pools on the cluster.</param>
/// <param name="ReadinessWarnings">Backend-emitted warnings (e.g. "private cluster — kubeconfig handle requires VPN").</param>
/// <param name="Fqdn">Cluster API server FQDN when reachable.</param>
/// <param name="KubernetesVersion">Currently running Kubernetes version.</param>
/// <param name="NodeResourceGroup">Auto-managed node resource group.</param>
/// <param name="Handoff">Populated only when <see cref="AzureDiscoveryRequest.IncludeKubeconfig"/> is true; never carries raw kubeconfig.</param>
public sealed record AzureAksClusterCandidate(
    string ResourceId,
    string Name,
    string Location,
    int AgentPoolCount,
    IReadOnlyList<string> ReadinessWarnings,
    string? Fqdn = null,
    string? KubernetesVersion = null,
    string? NodeResourceGroup = null,
    AzureAksHandoff? Handoff = null);

/// <summary>
/// Opaque, process-local handle for a kubeconfig minted by the AKS backend (#234).
/// The raw kubeconfig is NEVER returned over MCP; consumers exchange the handle for
/// an attach via the orchestrator's kubeconfig handle store (also #234).
/// </summary>
/// <param name="KubeconfigHandle">Opaque handle id. Format is backend-defined.</param>
/// <param name="ExpiresAt">UTC moment after which the handle is invalid and a fresh discovery is required.</param>
public sealed record AzureAksHandoff(string KubeconfigHandle, DateTimeOffset ExpiresAt);

/// <summary>
/// Backend interface for App Service discovery. Default implementation throws —
/// implemented in #233.
/// </summary>
public interface IAzureWebAppsDiscovery
{
    Task<AzurePagedResult<AzureWebAppCandidate>> ListAsync(
        AzureDiscoveryRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Backend interface for Container Apps discovery. Default implementation throws —
/// implemented in #233.
/// </summary>
public interface IAzureContainerAppsDiscovery
{
    Task<AzurePagedResult<AzureContainerAppCandidate>> ListAsync(
        AzureDiscoveryRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Backend interface for AKS cluster discovery. Default implementation throws —
/// implemented in #234.
/// </summary>
public interface IAzureAksDiscovery
{
    Task<AzurePagedResult<AzureAksClusterCandidate>> ListAsync(
        AzureDiscoveryRequest request, CancellationToken cancellationToken);
}
