using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using DotnetDiagnosticsMcp.Server.Security;
using Microsoft.Extensions.Configuration;

namespace DotnetDiagnosticsMcp.Server.Auth;

internal sealed class OidcJwtAuthOptions
{
    public static readonly OidcJwtAuthOptions Disabled = new(
        issuer: null,
        audience: null,
        metadataAddress: null,
        scopeClaimNames: ImmutableArray<string>.Empty,
        requiredClaims: ImmutableArray<RequiredClaimRule>.Empty);

    private OidcJwtAuthOptions(
        string? issuer,
        string? audience,
        Uri? metadataAddress,
        ImmutableArray<string> scopeClaimNames,
        ImmutableArray<RequiredClaimRule> requiredClaims)
    {
        Issuer = issuer;
        Audience = audience;
        MetadataAddress = metadataAddress;
        ScopeClaimNames = scopeClaimNames;
        RequiredClaims = requiredClaims;
    }

    public string? Issuer { get; }

    public string? Audience { get; }

    public Uri? MetadataAddress { get; }

    public bool IsEnabled => MetadataAddress is not null;

    public ImmutableArray<string> ScopeClaimNames { get; }

    public ImmutableArray<RequiredClaimRule> RequiredClaims { get; }

    public static OidcJwtAuthOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var issuer = TrimToNull(configuration["MCP_OIDC_ISSUER"]);
        var audience = TrimToNull(configuration["MCP_OIDC_AUDIENCE"]);
        var scopeClaimName = TrimToNull(configuration["MCP_OIDC_SCOPE_CLAIM"]);
        var requiredClaimsJson = TrimToNull(configuration["MCP_OIDC_REQUIRED_CLAIMS_JSON"]);

        if (issuer is null &&
            audience is null &&
            scopeClaimName is null &&
            requiredClaimsJson is null)
        {
            return Disabled;
        }

        if (issuer is null || audience is null)
        {
            throw new InvalidOperationException(
                "OIDC/JWT auth requires both MCP_OIDC_ISSUER and MCP_OIDC_AUDIENCE. " +
                "Set both values together or leave both unset to keep legacy bearer behavior.");
        }

        if (!Uri.TryCreate(issuer, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                $"MCP_OIDC_ISSUER must be an absolute URI. Value '{issuer}' is invalid.");
        }

        var scopeClaimNames = scopeClaimName is null
            ? ImmutableArray.Create("scp", "scope")
            : ImmutableArray.Create(scopeClaimName);

        return new OidcJwtAuthOptions(
            issuer,
            audience,
            BuildMetadataAddress(issuer),
            scopeClaimNames,
            ParseRequiredClaims(requiredClaimsJson));
    }

    public bool TryCreatePrincipal(
        ClaimsPrincipal principal,
        out BearerPrincipal? bearerPrincipal,
        out string? failureMessage)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (!ValidateRequiredClaims(principal, out failureMessage))
        {
            bearerPrincipal = null;
            return false;
        }

        var scopes = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var claimType in ScopeClaimNames)
        {
            foreach (var claim in principal.FindAll(claimType))
            {
                foreach (var scope in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    scopes.Add(scope);
                }
            }
        }

        if (scopes.Count == 0)
        {
            bearerPrincipal = null;
            failureMessage = ScopeClaimNames.Length == 1
                ? $"JWT is missing scope claim '{ScopeClaimNames[0]}'."
                : $"JWT is missing any configured scope claim ({string.Join(", ", ScopeClaimNames)}).";
            return false;
        }

        bearerPrincipal = new BearerPrincipal(ResolvePrincipalName(principal), scopes.ToImmutable());
        failureMessage = null;
        return true;
    }

    private bool ValidateRequiredClaims(ClaimsPrincipal principal, out string? failureMessage)
    {
        foreach (var rule in RequiredClaims)
        {
            var values = principal.FindAll(rule.ClaimType)
                .Select(c => c.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToArray();

            if (values.Length == 0)
            {
                failureMessage = $"JWT is missing required claim '{rule.ClaimType}'.";
                return false;
            }

            if (rule.AllowedValues.Count == 0)
            {
                continue;
            }

            if (values.Any(rule.AllowedValues.Contains))
            {
                continue;
            }

            failureMessage =
                $"JWT claim '{rule.ClaimType}' did not match any configured allowed value.";
            return false;
        }

        failureMessage = null;
        return true;
    }

    private static ImmutableArray<RequiredClaimRule> ParseRequiredClaims(string? json)
    {
        if (json is null)
        {
            return ImmutableArray<RequiredClaimRule>.Empty;
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "MCP_OIDC_REQUIRED_CLAIMS_JSON must be a JSON object mapping claim names to a string, null, or an array of strings.");
        }

        var rules = ImmutableArray.CreateBuilder<RequiredClaimRule>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var claimType = TrimToNull(property.Name);
            if (claimType is null)
            {
                throw new InvalidOperationException(
                    "MCP_OIDC_REQUIRED_CLAIMS_JSON contains an empty claim name.");
            }

            var allowedValues = property.Value.ValueKind switch
            {
                JsonValueKind.Null => ImmutableHashSet.Create<string>(StringComparer.Ordinal),
                JsonValueKind.String => ImmutableHashSet.Create(StringComparer.Ordinal, GetRequiredString(claimType, property.Value)),
                JsonValueKind.Array => ParseAllowedValues(claimType, property.Value),
                _ => throw new InvalidOperationException(
                    $"MCP_OIDC_REQUIRED_CLAIMS_JSON claim '{claimType}' must map to null, a string, or an array of strings."),
            };

            rules.Add(new RequiredClaimRule(claimType, allowedValues));
        }

        return rules.ToImmutable();
    }

    private static ImmutableHashSet<string> ParseAllowedValues(string claimType, JsonElement element)
    {
        var values = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    $"MCP_OIDC_REQUIRED_CLAIMS_JSON claim '{claimType}' array must contain only strings.");
            }

            values.Add(GetRequiredString(claimType, item));
        }

        return values.ToImmutable();
    }

    private static string GetRequiredString(string claimType, JsonElement element)
    {
        var value = TrimToNull(element.GetString());
        if (value is null)
        {
            throw new InvalidOperationException(
                $"MCP_OIDC_REQUIRED_CLAIMS_JSON claim '{claimType}' contains an empty string.");
        }

        return value;
    }

    private static string ResolvePrincipalName(ClaimsPrincipal principal)
    {
        foreach (var claimType in new[] { "preferred_username", "client_id", "azp", "appid", "sub" })
        {
            var value = TrimToNull(principal.FindFirst(claimType)?.Value);
            if (value is not null)
            {
                return value;
            }
        }

        return "oidc-jwt";
    }

    private static Uri BuildMetadataAddress(string issuer)
        => new(issuer.TrimEnd('/') + "/.well-known/openid-configuration", UriKind.Absolute);

    private static string? TrimToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal sealed record RequiredClaimRule(string ClaimType, ImmutableHashSet<string> AllowedValues);
}
