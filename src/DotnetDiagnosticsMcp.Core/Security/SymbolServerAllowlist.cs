namespace DotnetDiagnosticsMcp.Core.Security;

/// <summary>
/// Validates caller-supplied <c>symbolPath</c> values against an allowlist of remote
/// symbol-server hosts (issue #165 / M3). The default policy is "no remote symbol
/// servers" — only local file paths are accepted. Operators flip individual hosts in
/// via <c>Diagnostics__SymbolServerAllowlist__0=msdl.microsoft.com</c>, etc.
/// </summary>
/// <remarks>
/// The symbol search path uses the classic Microsoft <c>srv*[cache*]&lt;url&gt;</c>
/// syntax, semicolon-separated with bare directory entries. We tokenize on that
/// grammar and only inspect entries that begin with <c>srv*</c> / <c>symsrv*</c> /
/// <c>cache*</c>. Local paths (anything without those prefixes) flow through
/// unchanged. Any segment whose URL host is not on the allowlist is rejected.
/// </remarks>
public sealed class SymbolServerAllowlist
{
    private readonly HashSet<string> _allowedHosts;

    public SymbolServerAllowlist(SecurityOptions? options)
    {
        _allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (options?.SymbolServerAllowlist is { Count: > 0 } configured)
        {
            foreach (var host in configured)
            {
                if (!string.IsNullOrWhiteSpace(host))
                {
                    _allowedHosts.Add(host.Trim());
                }
            }
        }
    }

    /// <summary>Snapshot of the configured host allowlist (sorted, for stable error reporting).</summary>
    public IReadOnlyList<string> AllowedHosts => _allowedHosts.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Container for the validation outcome.</summary>
    public sealed record ValidationResult(bool IsAllowed, string? DeniedHost, string? DeniedSegment)
    {
        public static ValidationResult Allow() => new(true, null, null);
        public static ValidationResult Deny(string host, string segment) => new(false, host, segment);
    }

    private static readonly char[] SegmentSeparators = new[] { ';' };

    /// <summary>
    /// Validates a caller-supplied symbol path. Returns <see cref="ValidationResult.Allow"/>
    /// for null/empty, pure local paths, or remote segments whose host is allowlisted.
    /// Returns <see cref="ValidationResult.Deny"/> on the first violation.
    /// </summary>
    public ValidationResult Validate(string? symbolPath)
    {
        if (string.IsNullOrWhiteSpace(symbolPath)) return ValidationResult.Allow();

        var segments = symbolPath.Split(SegmentSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var segment in segments)
        {
            // Tokenize the srv*/cache*/symsrv* grammar (case-insensitive). Anything else
            // is treated as a local path and accepted.
            if (TryExtractRemoteUrl(segment, out var url))
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    return ValidationResult.Deny(url, segment);
                }

                if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                {
                    return ValidationResult.Deny(uri.Host, segment);
                }

                if (!_allowedHosts.Contains(uri.Host))
                {
                    return ValidationResult.Deny(uri.Host, segment);
                }
            }
        }

        return ValidationResult.Allow();
    }

    private static bool TryExtractRemoteUrl(string segment, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(segment)) return false;

        // The classic NT symbol-path grammar tokenizes on '*'. Examples:
        //   srv*c:\symcache*https://msdl.microsoft.com/download/symbols
        //   symsrv*symsrv.dll*c:\symcache*https://attacker.example/syms
        //   cache*c:\symcache
        // We pick the first http(s) token; everything else is a directory.
        var parts = segment.Split('*', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        var head = parts[0];
        if (!head.Equals("srv", StringComparison.OrdinalIgnoreCase) &&
            !head.Equals("symsrv", StringComparison.OrdinalIgnoreCase) &&
            !head.Equals("cache", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var i = 1; i < parts.Length; i++)
        {
            var token = parts[i].Trim();
            if (token.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                token.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = token;
                return true;
            }
        }

        return false;
    }
}
