namespace DotnetDiagnosticsMcp.Core.Security;

/// <summary>
/// Provider-name allowlist used by <c>collect_event_source</c> (issue #165 / M2).
/// Custom EventSources can emit user ids, request payloads, and auth-failure detail —
/// the LLM ingests every captured event. The allowlist defaults to a curated set of
/// runtime / ASP.NET Core / HttpClient providers; anything else is rejected unless
/// the deployment grants the sensitive-value gate <em>and</em> the caller explicitly
/// passes <c>unsafeProvider=true</c>.
/// </summary>
public sealed class EventSourceAllowlist
{
    /// <summary>
    /// Curated default set of providers we consider low-risk: runtime + framework
    /// instrumentation that is unlikely to log application data verbatim. Operators
    /// can extend or replace the list via <see cref="SecurityOptions.EventSourceAllowlist"/>.
    /// </summary>
    public static IReadOnlyList<string> DefaultProviders { get; } = new[]
    {
        "System.Runtime",
        "System.Threading.Tasks.TplEventSource",
        "System.Buffers.ArrayPoolEventSource",
        "System.Net.Http",
        "System.Net.Sockets",
        "System.Net.NameResolution",
        "System.Net.Security",
        "Microsoft-Windows-DotNETRuntime",
        "Microsoft-Windows-DotNETRuntimeRundown",
        "Microsoft-DotNETCore-SampleProfiler",
        "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Server.Kestrel",
        "Microsoft-AspNetCore-Server-Kestrel",
        "Microsoft.AspNetCore.Http.Connections",
        "Microsoft.AspNetCore.Routing",
        "Microsoft-AspNetCore-Mvc",
        "Microsoft.Extensions.Diagnostics.HealthChecks",
        "Microsoft.Extensions.Hosting",
    };

    private readonly HashSet<string> _allowed;

    public EventSourceAllowlist(SecurityOptions? options)
    {
        _allowed = new HashSet<string>(DefaultProviders, StringComparer.OrdinalIgnoreCase);
        if (options?.EventSourceAllowlist is { Count: > 0 } extra)
        {
            foreach (var provider in extra)
            {
                if (!string.IsNullOrWhiteSpace(provider))
                {
                    _allowed.Add(provider.Trim());
                }
            }
        }
    }

    /// <summary>Returns true when the deployment has implicitly allowlisted the provider.</summary>
    public bool IsAllowed(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        return _allowed.Contains(providerName.Trim());
    }

    /// <summary>Snapshot of the current allowlist (sorted, for stable error reporting).</summary>
    public IReadOnlyList<string> AllowedProviders => _allowed.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
}
