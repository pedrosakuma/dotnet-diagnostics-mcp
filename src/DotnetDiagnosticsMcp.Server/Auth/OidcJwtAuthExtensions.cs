using DotnetDiagnosticsMcp.Server.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace DotnetDiagnosticsMcp.Server.Auth;

internal static class OidcJwtAuthExtensions
{
    public const string JwtScheme = "OidcJwtBearer";

    public static OidcJwtAuthOptions AddOidcJwtAuth(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var authOptions = OidcJwtAuthOptions.FromConfiguration(builder.Configuration);
        builder.Services.AddSingleton(authOptions);

        var authentication = builder.Services.AddAuthentication();
        if (!authOptions.IsEnabled)
        {
            return authOptions;
        }

        authentication.AddJwtBearer(JwtScheme, options => ConfigureJwtBearer(options, authOptions));
        return authOptions;
    }

    private static void ConfigureJwtBearer(JwtBearerOptions options, OidcJwtAuthOptions authOptions)
    {
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = !authOptions.MetadataAddress!.IsLoopback;
        options.SaveToken = false;
        options.RefreshOnIssuerKeyNotFound = true;
        options.MetadataAddress = authOptions.MetadataAddress.AbsoluteUri;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = authOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = authOptions.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
        };

        options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            options.MetadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever
            {
                RequireHttps = options.RequireHttpsMetadata,
            })
        {
            AutomaticRefreshInterval = TimeSpan.FromHours(24),
            RefreshInterval = TimeSpan.FromMinutes(5),
        };

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                string? failureMessage = null;
                if (context.Principal is null ||
                    !authOptions.TryCreatePrincipal(context.Principal, out var bearerPrincipal, out failureMessage))
                {
                    context.Fail(failureMessage ?? "OIDC/JWT principal mapping failed.");
                    return Task.CompletedTask;
                }

                context.HttpContext.SetBearerPrincipal(bearerPrincipal!);
                return Task.CompletedTask;
            },
        };
    }
}
