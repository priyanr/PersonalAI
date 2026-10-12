using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Auth;

namespace PersonalAI.Api.Auth;

/// <summary>Wires delegated OAuth2/OIDC login: the provider client, and JWT bearer validation of the access
/// tokens it issues. Signature keys come from, in order of preference:
/// <list type="bullet">
/// <item>a shared symmetric <see cref="AuthSettings.JwtKey"/> (providers that sign with HMAC);</item>
/// <item>the JWKS named by OIDC discovery (the standard case; JwtBearer fetches and rotates it itself);</item>
/// <item>an explicit <see cref="AuthSettings.JwksUri"/> when discovery is off.</item>
/// </list></summary>
public static class AuthServiceCollectionExtensions
{
    public const string ProviderUnavailableItemKey = "PersonalAI.AuthProviderUnavailable";

    public static IServiceCollection AddOAuthClient(this IServiceCollection services)
    {
        services.AddHttpClient<OAuthClient>();
        services.AddHttpClient(nameof(OAuthEndpointResolver));
        services.AddSingleton(sp => new OAuthEndpointResolver(
            () => sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(OAuthEndpointResolver)),
            sp.GetRequiredService<IOptions<AuthSettings>>(),
            TimeProvider.System));
        return services;
    }

    public static IServiceCollection AddDelegatedJwtAuthentication(this IServiceCollection services, AuthSettings authSettings)
    {
        Validate(authSettings);

        services.AddSingleton(sp => new TokenValidationCache(
            () => sp.GetRequiredService<OAuthClient>(),
            TimeSpan.FromSeconds(Math.Max(1, authSettings.ValidateCacheSeconds)),
            TimeProvider.System));

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options => ConfigureJwtBearer(options, authSettings));
        services.AddAuthorization();
        return services;
    }

    /// <summary>Fails fast at startup on a configuration that could never validate a token.</summary>
    public static void Validate(AuthSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Authority))
            throw new InvalidOperationException("Auth:Authority is required when Auth:Enabled is true.");
        if (string.IsNullOrWhiteSpace(settings.ClientId))
            throw new InvalidOperationException("Auth:ClientId is required when Auth:Enabled is true.");
        if (string.IsNullOrEmpty(settings.JwtKey) && !settings.UseDiscovery && string.IsNullOrWhiteSpace(settings.JwksUri))
            throw new InvalidOperationException(
                "No way to verify access token signatures: set Auth:JwtKey (shared HMAC key), enable Auth:UseDiscovery, or set Auth:JwksUri.");
    }

    public static void ConfigureJwtBearer(JwtBearerOptions options, AuthSettings settings)
    {
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = settings.RequireHttpsMetadata;

        var symmetric = !string.IsNullOrEmpty(settings.JwtKey);
        var hasIssuer = !string.IsNullOrWhiteSpace(settings.Issuer);

        if (!symmetric)
        {
            if (settings.UseDiscovery)
            {
                // JwtBearer reads jwks_uri + issuer from the discovery document and refreshes the keys itself.
                options.Authority = settings.Authority;
                options.MetadataAddress = settings.ResolveMetadataAddress();
            }
            else
            {
                options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    settings.ResolveEndpoint(settings.JwksUri),
                    new JwksOnlyConfigurationRetriever(),
                    new HttpDocumentRetriever { RequireHttps = settings.RequireHttpsMetadata });
            }
        }

        options.TokenValidationParameters = new TokenValidationParameters
        {
            // With discovery, a null ValidIssuer means "the issuer from the discovery document".
            ValidateIssuer = hasIssuer || (!symmetric && settings.UseDiscovery),
            ValidIssuer = hasIssuer ? settings.Issuer : null,
            ValidateAudience = !string.IsNullOrWhiteSpace(settings.Audience),
            ValidAudience = string.IsNullOrWhiteSpace(settings.Audience) ? null : settings.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = symmetric ? new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.JwtKey)) : null,
            NameClaimType = JwtRegisteredClaimNames.Sub,
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                // Some providers sign every client's tokens with the same key, so a token minted for a
                // different app would otherwise also pass signature validation.
                if (settings.ValidateClientId && !ClientIdMatches(context.Principal, settings))
                {
                    context.Fail("Token was not issued to this client.");
                    return;
                }

                if (!settings.IntrospectAccessTokens)
                    return;

                // Ask the provider whether the token was revoked (remembered for ValidateCacheSeconds, so the
                // provider sees at most one check per token per window). Fail closed if unreachable.
                var rawToken = (context.SecurityToken as JsonWebToken)?.EncodedToken;
                if (rawToken is null)
                {
                    context.Fail("Unreadable token.");
                    return;
                }
                try
                {
                    var cache = context.HttpContext.RequestServices.GetRequiredService<TokenValidationCache>();
                    if (!await cache.IsActiveAsync(rawToken, context.HttpContext.RequestAborted))
                        context.Fail("Token has been revoked.");
                }
                catch (HttpRequestException ex)
                {
                    context.HttpContext.RequestServices.GetRequiredService<ILogger<TokenValidationCache>>()
                        .LogWarning(ex, "Could not introspect access token with the OAuth2 provider; rejecting request.");
                    context.HttpContext.Items[ProviderUnavailableItemKey] = true;
                    context.Fail("Token validation unavailable.");
                }
            },
            // Still fail closed, but as 503 rather than 401: the token may be perfectly good, and a 401
            // makes the frontend throw away its tokens and force a fresh login over a provider outage.
            OnChallenge = context =>
            {
                if (context.HttpContext.Items.ContainsKey(ProviderUnavailableItemKey))
                {
                    context.HandleResponse();
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                }
                return Task.CompletedTask;
            }
        };
    }

    /// <summary>True when the first of <see cref="AuthSettings.ClientIdClaimTypes"/> present on the token
    /// names this app. A token carrying none of them is rejected.</summary>
    public static bool ClientIdMatches(ClaimsPrincipal? principal, AuthSettings settings)
    {
        if (principal is null)
            return false;
        var claimed = settings.ClientIdClaimTypes
            .Select(type => principal.FindFirst(type)?.Value)
            .FirstOrDefault(value => !string.IsNullOrEmpty(value));
        return claimed == settings.ClientId;
    }

    /// <summary>Turns a bare JWKS document into the configuration JwtBearer expects, for providers that
    /// publish signing keys but no discovery document.</summary>
    private sealed class JwksOnlyConfigurationRetriever : IConfigurationRetriever<OpenIdConnectConfiguration>
    {
        public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
            string address, IDocumentRetriever retriever, CancellationToken cancel)
        {
            var json = await retriever.GetDocumentAsync(address, cancel);
            var configuration = new OpenIdConnectConfiguration { JwksUri = address, JsonWebKeySet = new JsonWebKeySet(json) };
            foreach (var key in configuration.JsonWebKeySet.GetSigningKeys())
                configuration.SigningKeys.Add(key);
            return configuration;
        }
    }
}
