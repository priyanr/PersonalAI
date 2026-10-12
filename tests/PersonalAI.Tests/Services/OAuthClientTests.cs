using System.Net;
using System.Text;
using System.Web;
using FluentAssertions;
using PersonalAI.Core.Services.Auth;
using static PersonalAI.Tests.Services.OAuthTestFactory;

namespace PersonalAI.Tests.Services;

public class OAuthClientTests
{
    private const string CustomProviderToken = "https://auth.example.com/oauth2/token";
    private const string Discovery = OidcAuthority + "/.well-known/openid-configuration";
    private const string OidcToken = OidcAuthority + "/protocol/openid-connect/token";

    private static RoutingHttpMessageHandler OidcProvider(bool withOptionalEndpoints = true) =>
        new RoutingHttpMessageHandler().On(Discovery, DiscoveryDocument(withOptionalEndpoints));

    // ---- custom provider: explicit endpoints, no discovery --------------------------------------------

    [Fact]
    public async Task CustomProvider_BuildAuthorizeUrl_UsesExplicitEndpoint_AndAllRequiredParameters()
    {
        var handler = new RoutingHttpMessageHandler();
        var client = CreateClient(CustomProviderSettings(), handler);

        var url = await client.BuildAuthorizeUrlAsync("the-state", null, CancellationToken.None);

        url.Should().StartWith("https://auth.example.com/oauth2/authorize?");
        var query = HttpUtility.ParseQueryString(new Uri(url).Query);
        query["client_id"].Should().Be("PersonalAI_abc");
        query["redirect_uri"].Should().Be("http://localhost:3000/auth/callback");
        query["response_type"].Should().Be("code");
        query["scope"].Should().Be("personalai");
        query["state"].Should().Be("the-state");
        query["code_challenge"].Should().BeNull();
        handler.Requests.Should().BeEmpty("discovery is off, so nothing is fetched");
    }

    [Fact]
    public async Task CustomProvider_ExchangeCode_PostsAuthorizationCodeGrant_WithCredentialsInBody()
    {
        var handler = new RoutingHttpMessageHandler().On(CustomProviderToken,
            """{"access_token":"at","token_type":"Bearer","expires_in":300,"refresh_token":"rt"}""");
        var client = CreateClient(CustomProviderSettings(), handler);

        var tokens = await client.ExchangeCodeAsync("the-code", null, CancellationToken.None);

        tokens!.AccessToken.Should().Be("at");
        tokens.RefreshToken.Should().Be("rt");
        tokens.ExpiresIn.Should().Be(300);
        var request = handler.LastTo(CustomProviderToken);
        request.Body.Should().Contain("grant_type=authorization_code").And.Contain("code=the-code")
            .And.Contain("client_id=PersonalAI_abc").And.Contain("client_secret=shh")
            .And.NotContain("code_verifier");
        request.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task CustomProvider_Introspect_PostsToValidateEndpoint_AndParsesActiveFlag()
    {
        var handler = new RoutingHttpMessageHandler().On("https://auth.example.com/oauth2/validate",
            """{"active":true,"client_id":"PersonalAI_abc","exp":1900000000}""");
        var client = CreateClient(CustomProviderSettings(), handler);

        var result = await client.IntrospectAsync("the-jwt", CancellationToken.None);

        result!.Active.Should().BeTrue();
        result.ClientId.Should().Be("PersonalAI_abc");
        result.Exp.Should().Be(1900000000);
        handler.LastTo("https://auth.example.com/oauth2/validate").Body
            .Should().Contain("token=the-jwt").And.Contain("token_type_hint=access_token");
    }

    [Fact]
    public async Task Introspect_RevokedToken_ReturnsInactive()
    {
        var handler = new RoutingHttpMessageHandler().On("https://auth.example.com/oauth2/validate", """{"active":false}""");
        var client = CreateClient(CustomProviderSettings(), handler);

        (await client.IntrospectAsync("the-jwt", CancellationToken.None))!.Active.Should().BeFalse();
    }

    [Fact]
    public async Task CustomProvider_Logout_HasNoEndSessionEndpoint()
    {
        var client = CreateClient(CustomProviderSettings(), new RoutingHttpMessageHandler());

        (await client.BuildEndSessionUrlAsync("id", CancellationToken.None)).Should().BeNull();
    }

    // ---- Refresh -----------------------------------------------------------------------------------

    [Fact]
    public async Task Refresh_ProviderRejects_ReturnsNull()
    {
        var handler = new RoutingHttpMessageHandler().On(CustomProviderToken, """{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        var client = CreateClient(CustomProviderSettings(), handler);

        var tokens = await client.RefreshAsync("stale-refresh-token", CancellationToken.None);

        tokens.Should().BeNull();
        handler.LastTo(CustomProviderToken).Body.Should().Contain("grant_type=refresh_token").And.Contain("scope=personalai");
    }

    [Fact]
    public async Task Refresh_ProviderErrors_Throws_SoTheApiCanAnswer503()
    {
        var handler = new RoutingHttpMessageHandler().On(CustomProviderToken, "oops", HttpStatusCode.BadGateway);
        var client = CreateClient(CustomProviderSettings(), handler);

        var act = () => client.RefreshAsync("rt", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Refresh_ProviderRotates_ReturnsNewRefreshToken()
    {
        var handler = new RoutingHttpMessageHandler().On(CustomProviderToken,
            """{"access_token":"new-at","expires_in":300,"refresh_token":"new-rt"}""");
        var client = CreateClient(CustomProviderSettings(), handler);

        var tokens = await client.RefreshAsync("old-refresh-token", CancellationToken.None);

        tokens!.AccessToken.Should().Be("new-at");
        tokens.RefreshToken.Should().Be("new-rt");
    }

    [Fact]
    public async Task Refresh_ProviderOmitsRefreshToken_KeepsTheOneSent()
    {
        // RFC 6749 §6: a provider that doesn't rotate may leave refresh_token out (Google does).
        var handler = new RoutingHttpMessageHandler().On(CustomProviderToken, """{"access_token":"new-at","expires_in":3600}""");
        var client = CreateClient(CustomProviderSettings(), handler);

        var tokens = await client.RefreshAsync("still-valid-rt", CancellationToken.None);

        tokens!.RefreshToken.Should().Be("still-valid-rt");
    }

    // ---- Standard OIDC provider: discovery ---------------------------------------------------------

    [Fact]
    public async Task Oidc_BuildAuthorizeUrl_UsesDiscoveredEndpoint_WithPkceAndExtraParameters()
    {
        var settings = OidcSettings();
        settings.AuthorizeParameters["audience"] = "https://api.example.com";
        var client = CreateClient(settings, OidcProvider());

        var url = await client.BuildAuthorizeUrlAsync("st", "the-challenge", CancellationToken.None);

        url.Should().StartWith(OidcAuthority + "/protocol/openid-connect/auth?");
        var query = HttpUtility.ParseQueryString(new Uri(url).Query);
        query["code_challenge"].Should().Be("the-challenge");
        query["code_challenge_method"].Should().Be("S256");
        query["scope"].Should().Be("openid offline_access");
        query["audience"].Should().Be("https://api.example.com");
    }

    [Fact]
    public async Task Oidc_ExchangeCode_SendsPkceVerifier_ToDiscoveredTokenEndpoint()
    {
        var handler = OidcProvider().On(OidcToken, """{"access_token":"at","refresh_token":"rt","id_token":"idt"}""");
        var client = CreateClient(OidcSettings(), handler);

        var tokens = await client.ExchangeCodeAsync("c", "the-verifier", CancellationToken.None);

        tokens!.IdToken.Should().Be("idt");
        tokens.ExpiresIn.Should().BeNull("expires_in is optional and wasn't sent");
        handler.LastTo(OidcToken).Body.Should().Contain("code_verifier=the-verifier");
    }

    [Fact]
    public async Task ClientSecretBasic_SendsCredentialsInAuthorizationHeader_NotTheBody()
    {
        var settings = OidcSettings();
        settings.TokenEndpointAuthMethod = "client_secret_basic";
        var handler = OidcProvider().On(OidcToken, """{"access_token":"at"}""");
        var client = CreateClient(settings, handler);

        await client.RefreshAsync("rt", CancellationToken.None);

        var request = handler.LastTo(OidcToken);
        request.Authorization.Should().Be("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("personalai:s3cret")));
        request.Body.Should().NotContain("client_secret").And.NotContain("client_id");
    }

    [Fact]
    public async Task Oidc_Revoke_UsesDiscoveredEndpoint_WithTokenTypeHint()
    {
        const string revoke = OidcAuthority + "/protocol/openid-connect/revoke";
        var handler = OidcProvider().On(revoke, "");
        var client = CreateClient(OidcSettings(), handler);

        await client.RevokeAsync("rt", "refresh_token", CancellationToken.None);

        handler.LastTo(revoke).Body.Should().Contain("token=rt").And.Contain("token_type_hint=refresh_token")
            .And.Contain("client_secret=s3cret");
    }

    [Fact]
    public async Task Oidc_EndSessionUrl_CarriesIdTokenHintAndPostLogoutRedirect()
    {
        var client = CreateClient(OidcSettings(), OidcProvider());

        var url = await client.BuildEndSessionUrlAsync("the-id-token", CancellationToken.None);

        url.Should().StartWith(OidcAuthority + "/protocol/openid-connect/logout?");
        var query = HttpUtility.ParseQueryString(new Uri(url!).Query);
        query["id_token_hint"].Should().Be("the-id-token");
        query["post_logout_redirect_uri"].Should().Be("http://localhost:3000/");
        query["client_id"].Should().Be("personalai");
    }

    [Fact]
    public async Task Oidc_ProviderWithoutOptionalEndpoints_DisablesThoseFeatures()
    {
        var handler = OidcProvider(withOptionalEndpoints: false);
        var client = CreateClient(OidcSettings(), handler);

        (await client.IntrospectAsync("at", CancellationToken.None)).Should().BeNull();
        (await client.BuildEndSessionUrlAsync("id", CancellationToken.None)).Should().BeNull();
        await client.RevokeAsync("rt", "refresh_token", CancellationToken.None);

        handler.Requests.Should().OnlyContain(r => r.Uri.ToString() == Discovery);
    }

    [Fact]
    public async Task DiscoveryUnreachable_Throws_SoTheApiCanAnswer503()
    {
        var handler = new RoutingHttpMessageHandler().On(Discovery, "down", HttpStatusCode.ServiceUnavailable);
        var client = CreateClient(OidcSettings(), handler);

        var act = () => client.BuildAuthorizeUrlAsync("st", null, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task PublicClient_WithoutSecret_SendsOnlyClientId()
    {
        var settings = OidcSettings();
        settings.ClientSecret = "";
        var handler = OidcProvider().On(OidcToken, """{"access_token":"at"}""");
        var client = CreateClient(settings, handler);

        await client.ExchangeCodeAsync("c", "v", CancellationToken.None);

        handler.LastTo(OidcToken).Body.Should().Contain("client_id=personalai").And.NotContain("client_secret");
    }
}
