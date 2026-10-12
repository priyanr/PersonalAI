using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Auth;
using static PersonalAI.Tests.Services.OAuthTestFactory;

namespace PersonalAI.Tests.Services;

public class OAuthEndpointResolverTests
{
    private const string Discovery = OidcAuthority + "/.well-known/openid-configuration";

    private static OAuthEndpointResolver Create(AuthSettings settings, HttpMessageHandler handler, TimeProvider? clock = null) =>
        new(() => new HttpClient(handler, disposeHandler: false), Options.Create(settings), clock ?? TimeProvider.System);

    [Fact]
    public async Task ExplicitEndpoint_WinsOverDiscovery_AndRelativePathsResolveAgainstAuthority()
    {
        var settings = OidcSettings();
        settings.TokenEndpoint = "/custom/token";
        settings.EndSessionEndpoint = "https://elsewhere.example.com/logout";
        var resolver = Create(settings, new RoutingHttpMessageHandler().On(Discovery, DiscoveryDocument()));

        var endpoints = await resolver.GetAsync(CancellationToken.None);

        endpoints.Token.Should().Be(OidcAuthority + "/custom/token");
        endpoints.EndSession.Should().Be("https://elsewhere.example.com/logout");
        endpoints.Authorization.Should().Be(OidcAuthority + "/protocol/openid-connect/auth");
    }

    [Fact]
    public async Task CustomMetadataAddress_IsUsedInsteadOfWellKnownPath()
    {
        var settings = OidcSettings();
        settings.MetadataAddress = "https://meta.example.com/oidc.json";
        var handler = new RoutingHttpMessageHandler().On("https://meta.example.com/oidc.json", DiscoveryDocument());

        await Create(settings, handler).GetAsync(CancellationToken.None);

        handler.Requests.Should().ContainSingle().Which.Uri.ToString().Should().Be("https://meta.example.com/oidc.json");
    }

    [Fact]
    public async Task DiscoveryDocument_IsCached_ThenRefetchedAfterCacheDuration()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var handler = new RoutingHttpMessageHandler().On(Discovery, DiscoveryDocument());
        var resolver = Create(OidcSettings(), handler, clock);

        await resolver.GetAsync(CancellationToken.None);
        await resolver.GetAsync(CancellationToken.None);
        handler.CountTo(Discovery).Should().Be(1);

        clock.Advance(OAuthEndpointResolver.CacheDuration + TimeSpan.FromSeconds(1));
        await resolver.GetAsync(CancellationToken.None);
        handler.CountTo(Discovery).Should().Be(2);
    }

    [Fact]
    public async Task FailedDiscovery_IsNotCached()
    {
        var handler = new RoutingHttpMessageHandler().On(Discovery, "down", HttpStatusCode.BadGateway);
        var resolver = Create(OidcSettings(), handler);

        await FluentActions.Awaiting(() => resolver.GetAsync(CancellationToken.None)).Should().ThrowAsync<HttpRequestException>();
        handler.On(Discovery, DiscoveryDocument());

        (await resolver.GetAsync(CancellationToken.None)).Token.Should().EndWith("/token");
    }

    [Fact]
    public async Task MalformedDiscoveryDocument_SurfacesAsHttpRequestException()
    {
        var handler = new RoutingHttpMessageHandler().On(Discovery, "<html>not json</html>");

        await FluentActions.Awaiting(() => Create(OidcSettings(), handler).GetAsync(CancellationToken.None))
            .Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task PlainHttpDiscovery_IsRefused_UnlessAllowed()
    {
        var settings = OidcSettings();
        settings.Authority = "http://localhost:8080/realms/dev";
        var handler = new RoutingHttpMessageHandler()
            .On("http://localhost:8080/realms/dev/.well-known/openid-configuration", DiscoveryDocument());

        await FluentActions.Awaiting(() => Create(settings, handler).GetAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();

        settings.RequireHttpsMetadata = false;
        (await Create(settings, handler).GetAsync(CancellationToken.None)).Authorization.Should().NotBeEmpty();
    }

    [Fact]
    public async Task NoDiscovery_AndNoTokenEndpoint_ExplainsWhatToConfigure()
    {
        var settings = CustomProviderSettings();
        settings.TokenEndpoint = "";

        await FluentActions.Awaiting(() => Create(settings, new RoutingHttpMessageHandler()).GetAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*Auth:TokenEndpoint*");
    }
}
