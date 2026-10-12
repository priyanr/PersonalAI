using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PersonalAI.Api.Controllers;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Auth;
using PersonalAI.Tests.Services;

namespace PersonalAI.Tests.Controllers;

public class AuthControllerTests
{
    private static AuthSettings Settings(bool enabled = true)
    {
        var settings = OAuthTestFactory.CustomProviderSettings();
        settings.Enabled = enabled;
        return settings;
    }

    private static string MintFakeJwt(string sub = "Administrator", string scope = "personalai", TimeSpan? lifetime = null, string scopeClaim = "scope")
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("any-key-at-least-16-bytes-long!!"));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[] { new Claim(JwtRegisteredClaimNames.Sub, sub), new Claim(scopeClaim, scope) };
        var token = new JwtSecurityToken(claims: claims, expires: DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(5)), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static AuthController CreateController(AuthSettings? settings = null, HttpMessageHandler? handler = null)
    {
        settings ??= Settings();
        var oauthClient = OAuthTestFactory.CreateClient(settings, handler ?? new FakeHttpMessageHandler("{}"));
        var controller = new AuthController(oauthClient, Options.Create(settings));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    [Fact]
    public void Status_ReturnsEnabledFlag()
    {
        var controller = CreateController(Settings(enabled: true));

        var result = ((OkObjectResult)controller.Status().Result!).Value as AuthStatusDto;

        result!.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Login_RedirectsToAuthorizeUrl_WithGivenState()
    {
        var controller = CreateController();

        var result = (RedirectResult)await controller.Login("the-state", null, CancellationToken.None);

        result.Url.Should().StartWith("https://auth.example.com/oauth2/authorize?");
        result.Url.Should().Contain("state=the-state");
    }

    [Fact]
    public async Task Login_MissingState_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = await controller.Login("", null, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Exchange_MissingCode_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = await controller.Exchange(new ExchangeCodeRequest(""), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Exchange_ValidCode_ReturnsTokensAndDecodedClaims()
    {
        var jwt = MintFakeJwt(sub: "Administrator", scope: "personalai");
        var handler = new FakeHttpMessageHandler(
            $$"""{"access_token":"{{jwt}}","token_type":"Bearer","expires_in":300,"refresh_token":"rt"}""");
        var controller = CreateController(handler: handler);

        var result = ((OkObjectResult)(await controller.Exchange(new ExchangeCodeRequest("a-code"), CancellationToken.None)).Result!).Value as TokenResponseDto;

        result!.AccessToken.Should().Be(jwt);
        result.RefreshToken.Should().Be("rt");
        result.ExpiresIn.Should().Be(300);
        result.Subject.Should().Be("Administrator");
        result.Scope.Should().Be("personalai");
    }

    [Fact]
    public async Task Refresh_ProviderRejects_ReturnsUnauthorized()
    {
        var handler = new FakeHttpMessageHandler("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        var controller = CreateController(handler: handler);

        var result = await controller.Refresh(new RefreshRequest("stale-rt"), CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Refresh_ProviderErrors_ReturnsServiceUnavailable_NotUnauthorized()
    {
        var handler = new FakeHttpMessageHandler("oops", HttpStatusCode.InternalServerError);
        var controller = CreateController(handler: handler);

        var result = await controller.Refresh(new RefreshRequest("good-rt"), CancellationToken.None);

        result.Result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Refresh_ProviderAccepts_ReturnsNewTokens()
    {
        var jwt = MintFakeJwt();
        var handler = new FakeHttpMessageHandler(
            $$"""{"access_token":"{{jwt}}","token_type":"Bearer","expires_in":300,"refresh_token":"new-rt"}""");
        var controller = CreateController(handler: handler);

        var result = ((OkObjectResult)(await controller.Refresh(new RefreshRequest("old-rt"), CancellationToken.None)).Result!).Value as TokenResponseDto;

        result!.RefreshToken.Should().Be("new-rt");
    }

    [Fact]
    public async Task Logout_RevokesRefreshToken_ReturnsOk()
    {
        var handler = new FakeHttpMessageHandler("{}");
        var controller = CreateController(handler: handler);

        var result = await controller.Logout(new LogoutRequest("some-refresh-token"), CancellationToken.None);

        ((result.Result as OkObjectResult)!.Value as LogoutResponseDto)!.EndSessionUrl.Should().BeNull("the custom provider has no end-session endpoint");
        handler.LastRequestUri!.ToString().Should().Be("https://auth.example.com/oauth2/revoke");
    }

    [Fact]
    public void Me_AuthDisabled_ReturnsLocalUser()
    {
        var controller = CreateController(Settings(enabled: false));

        var result = ((OkObjectResult)controller.Me().Result!).Value as MeDto;

        result!.Authenticated.Should().BeTrue();
        result.Subject.Should().Be("local");
    }

    [Fact]
    public async Task Login_DiscoveryUnavailable_Returns503()
    {
        var handler = new RoutingHttpMessageHandler()
            .On(OAuthTestFactory.OidcAuthority + "/.well-known/openid-configuration", "down", HttpStatusCode.BadGateway);
        var controller = CreateController(OAuthTestFactory.OidcSettings(), handler);

        var result = await controller.Login("st", "challenge", CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Exchange_ProviderRejectsCode_ReturnsUnauthorized()
    {
        var handler = new FakeHttpMessageHandler("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        var controller = CreateController(handler: handler);

        var result = await controller.Exchange(new ExchangeCodeRequest("used-code", "verifier"), CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedResult>();
        handler.LastRequestBody.Should().Contain("code_verifier=verifier");
    }

    [Fact]
    public async Task Exchange_NoExpiresIn_DerivesItFromTheAccessTokenExpiry()
    {
        var jwt = MintFakeJwt(lifetime: TimeSpan.FromMinutes(10));
        var controller = CreateController(handler: new FakeHttpMessageHandler($$"""{"access_token":"{{jwt}}","refresh_token":"rt"}"""));

        var result = ((OkObjectResult)(await controller.Exchange(new ExchangeCodeRequest("c"), CancellationToken.None)).Result!).Value as TokenResponseDto;

        result!.ExpiresIn.Should().BeInRange(590, 600);
    }

    [Fact]
    public async Task Exchange_OpaqueAccessToken_TakesSubjectFromIdToken()
    {
        var idToken = MintFakeJwt(sub: "user-42");
        var controller = CreateController(handler: new FakeHttpMessageHandler(
            $$"""{"access_token":"opaque-abc","refresh_token":"rt","id_token":"{{idToken}}","scope":"openid email"}"""));

        var result = ((OkObjectResult)(await controller.Exchange(new ExchangeCodeRequest("c"), CancellationToken.None)).Result!).Value as TokenResponseDto;

        result!.AccessToken.Should().Be("opaque-abc");
        result.Subject.Should().Be("user-42");
        result.Scope.Should().Be("openid email");
        result.IdToken.Should().Be(idToken);
        result.ExpiresIn.Should().Be(300, "nothing says when an opaque token without expires_in expires");
    }

    [Fact]
    public async Task Exchange_ScpClaim_IsReadAsScope()
    {
        var jwt = MintFakeJwt(scope: "chat.read", scopeClaim: "scp");
        var controller = CreateController(handler: new FakeHttpMessageHandler($$"""{"access_token":"{{jwt}}","expires_in":60}"""));

        var result = ((OkObjectResult)(await controller.Exchange(new ExchangeCodeRequest("c"), CancellationToken.None)).Result!).Value as TokenResponseDto;

        result!.Scope.Should().Be("chat.read");
        result.RefreshToken.Should().BeEmpty();
    }

    [Fact]
    public async Task Logout_OidcProvider_ReturnsEndSessionUrl()
    {
        var handler = new RoutingHttpMessageHandler()
            .On(OAuthTestFactory.OidcAuthority + "/.well-known/openid-configuration", OAuthTestFactory.DiscoveryDocument());
        var controller = CreateController(OAuthTestFactory.OidcSettings(), handler);

        var result = await controller.Logout(new LogoutRequest("rt", "the-id-token"), CancellationToken.None);

        ((result.Result as OkObjectResult)!.Value as LogoutResponseDto)!.EndSessionUrl
            .Should().Contain("/protocol/openid-connect/logout?").And.Contain("id_token_hint=the-id-token");
    }
}
