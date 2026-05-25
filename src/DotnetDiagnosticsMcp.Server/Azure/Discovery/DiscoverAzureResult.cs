namespace DotnetDiagnosticsMcp.Server.Azure.Discovery;

/// <summary>
/// Discriminated payload for <see cref="DotnetDiagnosticsMcp.Server.Tools.DiscoverAzureTool"/>.
/// Exactly one of <see cref="WebApps"/> / <see cref="ContainerApps"/> / <see cref="AksClusters"/>
/// is populated, matching <see cref="Kind"/>; the rest are <c>null</c>. Mirrors the shape
/// used by <c>list_orchestrator</c> so JSON consumers can branch on <c>data.kind</c>
/// without re-running the tool.
/// </summary>
public sealed record DiscoverAzureResult(
    string Kind,
    AzurePagedResult<AzureWebAppCandidate>? WebApps,
    AzurePagedResult<AzureContainerAppCandidate>? ContainerApps,
    AzurePagedResult<AzureAksClusterCandidate>? AksClusters);
