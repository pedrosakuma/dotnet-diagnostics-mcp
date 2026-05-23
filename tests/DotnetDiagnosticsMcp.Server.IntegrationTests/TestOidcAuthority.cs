using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

internal sealed class TestOidcAuthority : IAsyncDisposable
{
    private readonly RSA _rsa;
    private readonly RsaSecurityKey _signingKey;
    private readonly SigningCredentials _signingCredentials;
    private readonly WebApplication _app;

    private TestOidcAuthority(RSA rsa, RsaSecurityKey signingKey, WebApplication app, string issuer, string audience)
    {
        _rsa = rsa;
        _signingKey = signingKey;
        _signingCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256);
        _app = app;
        Issuer = issuer;
        Audience = audience;
    }

    public string Issuer { get; }

    public string Audience { get; }

    public static async Task<TestOidcAuthority> StartAsync(string audience = "dotnet-diagnostics-mcp")
    {
        var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa)
        {
            KeyId = Guid.NewGuid().ToString("N"),
        };

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        string? issuer = null;
        app.MapGet("/.well-known/openid-configuration", () => Results.Json(new
        {
            issuer = issuer!,
            jwks_uri = issuer! + "/.well-known/jwks.json",
        }));
        app.MapGet("/.well-known/jwks.json", () => Results.Json(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    kid = signingKey.KeyId,
                    alg = SecurityAlgorithms.RsaSha256,
                    n = Base64UrlEncoder.Encode(signingKey.Rsa.ExportParameters(false).Modulus),
                    e = Base64UrlEncoder.Encode(signingKey.Rsa.ExportParameters(false).Exponent),
                },
            },
        }));

        await app.StartAsync().ConfigureAwait(false);
        issuer = app.Urls.Single().TrimEnd('/');
        return new TestOidcAuthority(rsa, signingKey, app, issuer, audience);
    }

    public string CreateToken(
        IEnumerable<string> scopes,
        string subject = "oidc-subject",
        IReadOnlyDictionary<string, string>? claims = null,
        TimeSpan? lifetime = null)
    {
        var tokenClaims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new("scp", string.Join(' ', scopes)),
        };

        if (claims is not null)
        {
            foreach (var pair in claims)
            {
                tokenClaims.Add(new Claim(pair.Key, pair.Value));
            }
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(tokenClaims),
            Issuer = Issuer,
            Audience = Audience,
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(10)),
            SigningCredentials = _signingCredentials,
        };

        return new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _rsa.Dispose();
    }
}
