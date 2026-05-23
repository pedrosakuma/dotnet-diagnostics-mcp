using System.Text.RegularExpressions;

namespace DotnetDiagnosticsMcp.Core.Security;

/// <summary>
/// Pattern-based redactor for string content that may be surfaced to an LLM through the
/// heap drilldowns (<c>query_heap_snapshot</c> views <c>duplicate-strings</c> and
/// <c>object</c>). Even when the operator opts a deployment into
/// <see cref="SecurityOptions.AllowSensitiveHeapValues"/>, every string returned still
/// passes through this redactor so callers cannot accidentally exfiltrate well-known
/// secret shapes (Bearer tokens, PEM headers, connection strings, JWT-shaped tokens,
/// AWS-style access keys).
/// </summary>
/// <remarks>
/// The redactor is conservative on purpose: it never tries to be a full DLP engine. The
/// pattern set ships with high-signal, low-false-positive matchers and the whole match
/// is replaced with a stable <see cref="RedactedPlaceholder"/> marker so the LLM still
/// sees that a redaction happened. See issue #165 (B4) for the threat model and #166
/// (B5) for the eventual per-tool scope mechanism that will replace the flag gate.
/// </remarks>
public sealed class SensitiveDataRedactor
{
    /// <summary>Placeholder substituted in place of any match.</summary>
    public const string RedactedPlaceholder = "<redacted:sensitive>";

    /// <summary>Placeholder returned for whole values when the sensitive-value gate is closed.</summary>
    public const string MetadataOnlyPlaceholder = "<redacted:metadata-only>";

    /// <summary>
    /// Default regex patterns shipped with the server. Curated for high signal and low
    /// false-positive rate; deployments can extend the set via
    /// <see cref="SecurityOptions.RedactionPatterns"/>.
    /// </summary>
    public static IReadOnlyList<string> DefaultPatterns { get; } = new[]
    {
        // PEM headers (private keys, certificates, RSA, EC, OpenSSH, PGP, …)
        @"-----BEGIN[ A-Z0-9]+-----",
        // HTTP Authorization headers / bearer tokens (incl. surrounding token)
        @"(?i)\bBearer\s+[A-Za-z0-9._\-+/=]{8,}",
        @"(?i)\bBasic\s+[A-Za-z0-9+/=]{8,}",
        // JWT-shaped tokens (three base64url segments separated by dots)
        @"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+",
        // SQL Server / ADO.NET connection string fragments carrying a password
        @"(?i)(?:Password|Pwd)\s*=\s*[^;\s]+",
        // Generic api/secret/access key assignments in conn-string-style payloads
        @"(?i)(?:api[_\-]?key|secret(?:[_\-]?key)?|access[_\-]?key|client[_\-]?secret|auth[_\-]?token)\s*[:=]\s*[^;\s""'<>]+",
        // AWS access-key-id prefixes (incl. STS / temporary creds)
        @"\b(?:AKIA|ASIA|AGPA|AROA|AIPA|ANPA|ANVA|ANSA)[0-9A-Z]{12,}\b",
        // GitHub-style fine-grained / classic / app tokens
        @"\bgh[pousr]_[A-Za-z0-9]{20,}\b",
    };

    private readonly Regex[] _compiledPatterns;

    /// <summary>Creates a redactor using <see cref="DefaultPatterns"/> plus the configured
    /// <see cref="SecurityOptions.RedactionPatterns"/>. Invalid regex patterns are skipped
    /// (they would otherwise prevent the server from starting); callers can verify their
    /// pattern set with <see cref="CompilePatterns"/>.</summary>
    public SensitiveDataRedactor(SecurityOptions? options = null)
    {
        var extra = options?.RedactionPatterns ?? new List<string>();
        _compiledPatterns = CompilePatterns(DefaultPatterns.Concat(extra));
    }

    /// <summary>Compiles each pattern with a 200 ms per-match timeout to keep heap views
    /// responsive when a string is pathological. Invalid patterns are dropped silently.</summary>
    public static Regex[] CompilePatterns(IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        var compiled = new List<Regex>();
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            try
            {
                compiled.Add(new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200)));
            }
            catch (ArgumentException)
            {
                // Invalid pattern – skip rather than crash the server. Operators can
                // diagnose by inspecting logs at the registration site.
            }
        }
        return compiled.ToArray();
    }

    /// <summary>
    /// Applies the configured pattern set to <paramref name="value"/>. Matches are replaced
    /// with <see cref="RedactedPlaceholder"/>. Returns <c>null</c> when the input is
    /// <c>null</c>. Empty strings are passed through unchanged.
    /// </summary>
    public string? Redact(string? value)
    {
        if (value is null) return null;
        if (value.Length == 0) return value;

        var current = value;
        foreach (var rx in _compiledPatterns)
        {
            try
            {
                current = rx.Replace(current, RedactedPlaceholder);
            }
            catch (RegexMatchTimeoutException)
            {
                // Conservative fallback: if any pattern times out, collapse the whole value
                // so we never emit a string we failed to fully scan.
                return RedactedPlaceholder;
            }
        }
        return current;
    }
}
