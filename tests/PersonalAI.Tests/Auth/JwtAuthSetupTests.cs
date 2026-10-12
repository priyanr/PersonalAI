using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using PersonalAI.Api.Auth;
using PersonalAI.Tests.Services;

namespace PersonalAI.Tests.Auth;

public class JwtAuthSetupTests
{
    private static JwtBearerOptions Configure(Core.Models.Settings.AuthSettings settings)
    {
        var options = new JwtBearerOptions();
        AuthServiceCollectionExtensions.ConfigureJwtBearer(options, settings);
        return options;
    }

    [Fact]
    public void CustomProvider_SymmetricKey_NoDiscovery_NoIssuerCheck()
    {
        var options = Configure(OAuthTestFactory.CustomProviderSettings());

        options.TokenValidationParameters.IssuerSigningKey.Should().BeOfType<SymmetricSecurityKey>();
        options.TokenValidationParameters.ValidateIssuer.Should().BeFalse();
        options.TokenValidationParameters.ValidateAudience.Should().BeFalse();
        options.Authority.Should().BeNull();
        options.ConfigurationManager.Should().BeNull();
    }

    [Fact]
    public void Oidc_UsesDiscoveryForKeysAndIssuer_AndChecksAudienceWhenSet()
    {
        var settings = OAuthTestFactory.OidcSettings();
        settings.Audience = "https://api.example.com";

        var options = Configure(settings);

        options.Authority.Should().Be(OAuthTestFactory.OidcAuthority);
        options.MetadataAddress.Should().Be(OAuthTestFactory.OidcAuthority + "/.well-known/openid-configuration");
        options.TokenValidationParameters.IssuerSigningKey.Should().BeNull("keys come from the provider's JWKS");
        options.TokenValidationParameters.ValidateIssuer.Should().BeTrue();
        options.TokenValidationParameters.ValidIssuer.Should().BeNull("the discovery document's issuer is used");
        options.TokenValidationParameters.ValidateAudience.Should().BeTrue();
        options.TokenValidationParameters.ValidAudience.Should().Be("https://api.example.com");
    }

    [Fact]
    public void JwksUriWithoutDiscovery_GetsItsOwnConfigurationManager_AndExplicitIssuer()
    {
        var settings = OAuthTestFactory.OidcSettings();
        settings.UseDiscovery = false;
        settings.JwksUri = "/keys";
        settings.Issuer = "https://issuer.example.com";

        var options = Configure(settings);

        options.ConfigurationManager.Should().NotBeNull();
        options.Authority.Should().BeNull();
        options.TokenValidationParameters.ValidIssuer.Should().Be("https://issuer.example.com");
        options.TokenValidationParameters.ValidateIssuer.Should().BeTrue();
    }

    [Fact]
    public void Validate_RejectsConfigWithNoWayToCheckSignatures()
    {
        var settings = OAuthTestFactory.CustomProviderSettings();
        settings.JwtKey = "";

        FluentActions.Invoking(() => AuthServiceCollectionExtensions.Validate(settings))
            .Should().Throw<InvalidOperationException>().WithMessage("*JwtKey*");

        settings.JwksUri = "https://auth.example.com/keys";
        FluentActions.Invoking(() => AuthServiceCollectionExtensions.Validate(settings)).Should().NotThrow();
    }

    [Theory]
    [InlineData("client_id", "PersonalAI_abc", true)]
    [InlineData("azp", "PersonalAI_abc", true)]
    [InlineData("cid", "PersonalAI_abc", true)]
    [InlineData("client_id", "SomeOtherApp", false)]
    [InlineData("unrelated", "PersonalAI_abc", false)]
    public void ClientIdMatches_ChecksTheFirstClientIdClaimPresent(string claimType, string value, bool expected)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(claimType, value)], "test"));

        AuthServiceCollectionExtensions.ClientIdMatches(principal, OAuthTestFactory.CustomProviderSettings()).Should().Be(expected);
    }

    [Fact]
    public void ClientIdMatches_UsesFirstConfiguredClaimTypeThatIsPresent()
    {
        // client_id (first in the list) names another app, so a matching azp further down must not rescue it.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("client_id", "SomeOtherApp"), new Claim("azp", "PersonalAI_abc")], "test"));

        AuthServiceCollectionExtensions.ClientIdMatches(principal, OAuthTestFactory.CustomProviderSettings()).Should().BeFalse();
    }
}
