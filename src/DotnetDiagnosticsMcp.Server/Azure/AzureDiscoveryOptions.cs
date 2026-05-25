using System;

namespace DotnetDiagnosticsMcp.Server.Azure;

/// <summary>
/// Configuration surface for the Azure discovery foundation (issue #231, parent #230).
/// Bound from the <c>AzureDiscovery</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// Azure discovery is OFF by default. When <see cref="Enabled"/> is false the
/// <see cref="IAzureArmClientFactory"/> is not registered and no Azure SDK code is
/// reachable from the running server.
/// </para>
/// <para>
/// This v1 surface intentionally exposes only the master switch. Per-request inputs
/// (such as <c>subscriptionId</c>) flow through the upcoming Azure discovery tool
/// (#232) rather than configuration. Global tuning fields (custom credential chain,
/// retry policy overrides, …) will be added when the corresponding follow-up PRs
/// (#233, #234) need them.
/// </para>
/// </remarks>
public sealed class AzureDiscoveryOptions
{
    /// <summary>Default configuration section name.</summary>
    public const string SectionName = "AzureDiscovery";

    /// <summary>
    /// Master switch. When false (default) the Azure ARM client factory is NOT
    /// registered with the DI container and the Azure SDK is never instantiated.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// AKS kubeconfig handle TTL (#234). The opaque handle minted by
    /// <c>discover_azure(kind=aksclusters, includeKubeconfig=true)</c> stays valid
    /// in the process-local handle store for at most this long; the kubeconfig
    /// bytes are zeroed and the entry removed on expiry. Default 10 minutes.
    /// </summary>
    /// <remarks>
    /// The TTL exists to bound the blast radius of a leaked handle id: even an
    /// attacker who scrapes a handle out of a transcript can only use it briefly
    /// before re-discovery is required. Lower it in security-sensitive deployments;
    /// raise it cautiously, since the bytes are held in memory for the entire window.
    /// </remarks>
    public TimeSpan KubeconfigHandleTtl { get; set; } = TimeSpan.FromMinutes(10);
}

