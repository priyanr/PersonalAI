using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Auth;

namespace PersonalAI.Tests.Services;

/// <summary>Builds an <see cref="OAuthClient"/> (and its endpoint resolver) over one fake handler, plus the
/// two provider shapes the auth code must support: a custom provider (explicit endpoints, no discovery) and a
/// standard OIDC provider (everything from the discovery document).</summary>
public static class OAuthTestFactory
{
    public const string OidcAuthority = "https://idp.example.com/realms/main";

    public static AuthSettings CustomProviderSettings() => new()
    {
        Enabled = true,
        Authority = "https://auth.example.com",
        ClientId = "PersonalAI_abc",
        ClientSecret = "shh",
        RedirectUri = "http://localhost:3000/auth/callback",
        Scope = "personalai",
        JwtKey = "key",
        UseDiscovery = false,
        AuthorizationEndpoint = "/oauth2/authorize",
        TokenEndpoint = "/oauth2/token",
        RevocationEndpoint = "/oauth2/revoke",
        IntrospectionEndpoint = "/oauth2/validate"
    };

    public static AuthSettings OidcSettings() => new()
    {
        Enabled = true,
        Authority = OidcAuthority,
        ClientId = "personalai",
        ClientSecret = "s3cret",
        RedirectUri = "http://localhost:3000/auth/callback",
        Scope = "openid offline_access",
        PostLogoutRedirectUri = "http://localhost:3000/"
    };

    /// <summary>A discovery document for <see cref="OidcAuthority"/>; pass false to leave out the optional endpoints.</summary>
    public static string DiscoveryDocument(bool withOptionalEndpoints = true) => withOptionalEndpoints
        ? $$"""
          {"issuer":"{{OidcAuthority}}",
           "authorization_endpoint":"{{OidcAuthority}}/protocol/openid-connect/auth",
           "token_endpoint":"{{OidcAuthority}}/protocol/openid-connect/token",
           "revocation_endpoint":"{{OidcAuthority}}/protocol/openid-connect/revoke",
           "introspection_endpoint":"{{OidcAuthority}}/protocol/openid-connect/token/introspect",
           "end_session_endpoint":"{{OidcAuthority}}/protocol/openid-connect/logout",
           "jwks_uri":"{{OidcAuthority}}/protocol/openid-connect/certs"}
          """
        : $$"""
          {"issuer":"{{OidcAuthority}}",
           "authorization_endpoint":"{{OidcAuthority}}/authorize",
           "token_endpoint":"{{OidcAuthority}}/token",
           "jwks_uri":"{{OidcAuthority}}/jwks"}
          """;

    public static OAuthClient CreateClient(AuthSettings settings, HttpMessageHandler handler, TimeProvider? clock = null)
    {
        var options = Options.Create(settings);
        var resolver = new OAuthEndpointResolver(() => new HttpClient(handler, disposeHandler: false), options, clock ?? TimeProvider.System);
        return new OAuthClient(new HttpClient(handler, disposeHandler: false), resolver, options);
    }
}

/// <summary>Answers by URL (ignoring the query string), recording every request; unknown URLs get 404.</summary>
public sealed class RoutingHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = new(StringComparer.OrdinalIgnoreCase);

    public List<(HttpMethod Method, Uri Uri, string? Body, string? Authorization)> Requests { get; } = [];

    public RoutingHttpMessageHandler On(string url, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[url] = (status, body);
        return this;
    }

    public int CountTo(string url) => Requests.Count(r => r.Uri.GetLeftPart(UriPartial.Path).Equals(url, StringComparison.OrdinalIgnoreCase));

    public (HttpMethod Method, Uri Uri, string? Body, string? Authorization) LastTo(string url) =>
        Requests.Last(r => r.Uri.GetLeftPart(UriPartial.Path).Equals(url, StringComparison.OrdinalIgnoreCase));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request.Method, request.RequestUri!, body, request.Headers.Authorization?.ToString()));

        var key = request.RequestUri!.GetLeftPart(UriPartial.Path);
        var (status, responseBody) = _routes.TryGetValue(key, out var route) ? route : (HttpStatusCode.NotFound, "");
        return new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
    }
}
