using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnosticsMcp.Server.Security;

/// <summary>
/// v1 <see cref="IPrincipalResolver"/> backed by an in-memory table parsed from the
/// <c>Auth:BearerTokens</c> configuration section (canonical shape per RFC 0001 §3 / §6).
/// </summary>
/// <remarks>
/// Construction is the only validation path: duplicates, empty fields, and missing scopes
/// fail loudly at startup rather than at request time. Lookup is constant-time per
/// registered entry and never short-circuits on a hit — both properties are important to
/// avoid leaking which slot a token occupies via response timing.
/// </remarks>
internal sealed class BearerTokenRegistry : IPrincipalResolver
{
    // Stored as (utf8 bytes, principal) so we never re-encode per request and so the raw
    // string can be discarded after construction. Iteration order is fixed at build time.
    private readonly IReadOnlyList<(byte[] TokenBytes, BearerPrincipal Principal)> _entries;

    public static readonly BearerTokenRegistry Empty = new(Array.Empty<(byte[] TokenBytes, BearerPrincipal Principal)>());

    private BearerTokenRegistry(IReadOnlyList<(byte[] TokenBytes, BearerPrincipal Principal)> entries)
    {
        _entries = entries;
    }

    /// <summary>Count of registered tokens. Used by startup diagnostics; the values
    /// themselves are never enumerated.</summary>
    public int Count => _entries.Count;

    public BearerPrincipal? TryResolve(string presentedBearer)
    {
        if (string.IsNullOrEmpty(presentedBearer))
        {
            return null;
        }

        var presentedBytes = Encoding.UTF8.GetBytes(presentedBearer);
        BearerPrincipal? match = null;

        // Iterate every entry even after a hit. FixedTimeEquals is constant-time
        // *within* a length class; different-length presentations short-circuit inside
        // the framework call but every registered token is still compared. This avoids
        // a timing oracle on slot position without claiming length-hiding semantics
        // (acceptable trade-off documented in RFC 0001 §3.1).
        foreach (var (tokenBytes, principal) in _entries)
        {
            var equal = CryptographicOperations.FixedTimeEquals(tokenBytes, presentedBytes);
            if (equal)
            {
                match = principal;
            }
        }

        CryptographicOperations.ZeroMemory(presentedBytes);
        return match;
    }

    /// <summary>Builds a registry from <paramref name="configuration"/> plus the legacy
    /// <c>MCP_BEARER_TOKEN</c> environment variable, honouring the RFC 0001 §7
    /// coexistence + back-compat rules:
    /// <list type="bullet">
    ///   <item>Scoped tokens win when both shapes are configured; the legacy var is
    ///         ignored and a Warning is logged exactly once.</item>
    ///   <item>Otherwise the legacy var resolves to a synthetic <c>legacy-root</c>
    ///         principal holding the <see cref="BearerPrincipal.RootScope"/> wildcard.</item>
    ///   <item>When neither shape is configured and
    ///         <paramref name="allowEphemeralFallback"/> is true (loopback / stdio dev
    ///         mode), a 32-byte hex token is generated and surfaced as a
    ///         <c>legacy-root</c> principal — same ergonomics as today.</item>
    ///   <item>When neither shape is configured and
    ///         <paramref name="allowEphemeralFallback"/> is false (non-loopback bind),
    ///         construction throws — the H9/B1 bind guard.</item>
    /// </list></summary>
    public static BearerTokenRegistry Build(
        IConfiguration configuration,
        ILogger logger,
        bool allowEphemeralFallback)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        var scopedEntries = ParseScopedTokens(configuration);
        var legacyToken = Environment.GetEnvironmentVariable("MCP_BEARER_TOKEN");
        var hasLegacy = !string.IsNullOrWhiteSpace(legacyToken);

        if (scopedEntries.Count > 0)
        {
            if (hasLegacy)
            {
                logger.LogWarning(
                    "Legacy MCP_BEARER_TOKEN ignored because Auth:BearerTokens is configured. " +
                    "Remove MCP_BEARER_TOKEN to silence this warning.");
            }

            logger.LogInformation(
                "Bearer auth: loaded {Count} scoped token(s) from Auth:BearerTokens.",
                scopedEntries.Count);

            return new BearerTokenRegistry(BuildEntries(scopedEntries));
        }

        if (hasLegacy)
        {
            logger.LogInformation(
                "Bearer auth: loaded legacy MCP_BEARER_TOKEN (resolves to '{Name}' with root scope).",
                BearerPrincipal.LegacyRootName);

            return new BearerTokenRegistry(BuildLegacyEntries(legacyToken!));
        }

        if (!allowEphemeralFallback)
        {
            // H9 (issue #162) — refuse to generate an ephemeral token when bound to a
            // non-loopback address.
            throw new InvalidOperationException(
                "Refusing to start: server is configured to bind to a non-loopback address but " +
                "no bearer credentials are configured. Set Auth:BearerTokens or MCP_BEARER_TOKEN " +
                "to an operator-managed secret before exposing the MCP endpoint, or restrict " +
                "--urls / ASPNETCORE_URLS to loopback (http://127.0.0.1:<port>) for local development.");
        }

        var ephemeral = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        logger.LogWarning(
            "MCP_BEARER_TOKEN not set and no Auth:BearerTokens configured. " +
            "Generated ephemeral token for this run: {Token}",
            ephemeral);
        return new BearerTokenRegistry(BuildLegacyEntries(ephemeral));
    }

    private static (byte[] TokenBytes, BearerPrincipal Principal)[] BuildEntries(
        IReadOnlyList<ScopedTokenEntry> source)
    {
        var arr = new (byte[], BearerPrincipal)[source.Count];
        for (var i = 0; i < source.Count; i++)
        {
            var entry = source[i];
            var principal = new BearerPrincipal(entry.Name, entry.Scopes);
            arr[i] = (Encoding.UTF8.GetBytes(entry.Token), principal);
        }
        return arr;
    }

    private static (byte[] TokenBytes, BearerPrincipal Principal)[] BuildLegacyEntries(string token)
    {
        var principal = new BearerPrincipal(
            BearerPrincipal.LegacyRootName,
            ImmutableHashSet.Create(BearerPrincipal.RootScope));
        return new[] { (Encoding.UTF8.GetBytes(token), principal) };
    }

    /// <summary>Parses <c>Auth:BearerTokens</c> with strict validation. Errors are raised
    /// without including any token value in the exception message.</summary>
    private static IReadOnlyList<ScopedTokenEntry> ParseScopedTokens(IConfiguration configuration)
    {
        var section = configuration.GetSection("Auth:BearerTokens");
        if (!section.Exists())
        {
            return Array.Empty<ScopedTokenEntry>();
        }

        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ScopedTokenEntry>();

        var index = 0;
        foreach (var child in section.GetChildren())
        {
            var name = child["Name"];
            var token = child["Token"];
            var scopeChildren = child.GetSection("Scopes").GetChildren()
                .Select(c => c.Value)
                .ToArray();

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException(
                    $"Auth:BearerTokens[{index}] is missing a non-empty Name.");
            }
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    $"Auth:BearerTokens entry '{name}' is missing a non-empty Token.");
            }
            if (scopeChildren.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Auth:BearerTokens entry '{name}' must declare at least one scope.");
            }

            var scopes = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            foreach (var scope in scopeChildren)
            {
                if (string.IsNullOrWhiteSpace(scope))
                {
                    throw new InvalidOperationException(
                        $"Auth:BearerTokens entry '{name}' contains an empty scope string.");
                }
                scopes.Add(scope.Trim());
            }

            if (!seenNames.Add(name))
            {
                throw new InvalidOperationException(
                    $"Auth:BearerTokens contains duplicate Name '{name}'. Names must be unique.");
            }
            if (!seenTokens.Add(token))
            {
                // Mention only the *name* of the second occurrence — never the token value.
                throw new InvalidOperationException(
                    $"Auth:BearerTokens entry '{name}' reuses a Token value already registered for another entry.");
            }

            result.Add(new ScopedTokenEntry(name, token, scopes.ToImmutable()));
            index++;
        }

        return result;
    }

    private sealed record ScopedTokenEntry(string Name, string Token, ImmutableHashSet<string> Scopes);
}
