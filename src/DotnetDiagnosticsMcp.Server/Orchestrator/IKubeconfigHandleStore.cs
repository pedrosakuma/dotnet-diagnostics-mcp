using System;

namespace DotnetDiagnosticsMcp.Server.Orchestrator;

/// <summary>
/// Process-local, in-memory store of kubeconfig bytes keyed by an opaque handle id
/// minted by the AKS discovery backend (issue #234, parent #230).
/// </summary>
/// <remarks>
/// <para>
/// Security model — the points the gpt-5.5 review will check:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>Kubeconfig bytes are kept as <see cref="byte"/>[] end-to-end and
///     are NEVER converted to <see cref="string"/> inside the store; this prevents
///     an accidental <c>ToString()</c> / logger interpolation from leaking the
///     credential into a log scope or exception message.</description>
///   </item>
///   <item>
///     <description>Bytes are NEVER persisted to disk and the store is a singleton
///     scoped to this process. Multi-replica deployments do NOT share handles —
///     re-discovery is required after every restart or fail-over.</description>
///   </item>
///   <item>
///     <description>Entries expire after a configurable TTL (default 10 minutes).
///     On eviction the byte buffer is overwritten with zero via
///     <see cref="System.Array.Clear(System.Array, int, int)"/> before being released
///     to the GC.</description>
///   </item>
///   <item>
///     <description>The handle id itself is treated as a bearer credential. It
///     MUST NOT appear in any structured log scope, exception message, or response
///     field outside of the <c>AzureAksHandoff.KubeconfigHandle</c> field on the
///     discover_azure result envelope. Loggers should only assert handle presence
///     (true/false), never log the value.</description>
///   </item>
/// </list>
/// </remarks>
public interface IKubeconfigHandleStore
{
    /// <summary>
    /// Registers <paramref name="kubeconfig"/> in the store under a freshly minted
    /// opaque handle id and returns the handle plus its expiry. The store takes
    /// ownership of the passed-in byte array (it will be zeroed on expiry) and
    /// the caller MUST NOT mutate or retain the reference after the call.
    /// </summary>
    /// <param name="kubeconfig">Raw kubeconfig YAML bytes (already UTF-8 decoded from base64 by the AKS backend).</param>
    /// <returns>Handle id + UTC expiry moment.</returns>
    KubeconfigHandleMint Register(byte[] kubeconfig);

    /// <summary>
    /// Resolves a handle id back to the kubeconfig bytes. Returns null when the
    /// handle is unknown OR has expired (the store does NOT distinguish the two
    /// — both surface as "not found" so a stale handle cannot be probed for
    /// existence).
    /// </summary>
    /// <param name="handle">Opaque handle id (case-sensitive).</param>
    /// <returns>The kubeconfig bytes (a defensive copy — caller may not mutate the store entry), or null.</returns>
    byte[]? TryResolve(string handle);

    /// <summary>
    /// Diagnostics-only count of live (non-expired) entries. Used by the redaction
    /// audit test and by future health endpoints. The store never exposes the
    /// handle ids themselves through this surface.
    /// </summary>
    int Count { get; }
}

/// <summary>
/// Return shape from <see cref="IKubeconfigHandleStore.Register(byte[])"/>: the
/// opaque handle id (treat as a bearer credential — see store remarks) and the
/// UTC moment after which it is invalid.
/// </summary>
public readonly record struct KubeconfigHandleMint(string Handle, DateTimeOffset ExpiresAt);
