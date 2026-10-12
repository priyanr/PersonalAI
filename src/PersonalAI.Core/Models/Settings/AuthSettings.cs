namespace PersonalAI.Core.Models.Settings;

/// <summary>Delegates login to an external OAuth2 / OpenID Connect provider (Authorization Code grant,
/// with PKCE when the browser supplies a challenge) instead of PersonalAI managing its own
/// users/passwords. Pure REST/bearer-token model: the API issues the provider's access/refresh tokens
/// back to the caller as JSON (the frontend stores them and sends them as an Authorization header)
/// rather than setting any cookies. When <see cref="Enabled"/> is false (the default), no
/// authentication is enforced anywhere — local `dotnet run` behaves exactly as before.
///
/// Provider-neutral: endpoints come from OIDC discovery (<c>{Authority}/.well-known/openid-configuration</c>)
/// unless set explicitly, and access tokens are verified against the provider's published signing keys
/// (JWKS) unless a shared symmetric <see cref="JwtKey"/> is configured. A provider without discovery
/// sets <see cref="UseDiscovery"/> to false and lists its endpoints.</summary>
public class AuthSettings
{
    public const string SectionName = "Auth";

    public bool Enabled { get; set; } = false;

    /// <summary>The provider's issuer / base URL, e.g. https://auth.example.com or
    /// https://login.example.com/realms/main. Relative endpoint settings below resolve against it.</summary>
    public string Authority { get; set; } = "";

    public string ClientId { get; set; } = "";

    /// <summary>Empty for a public client (then only <c>client_id</c> is sent to the token endpoint).</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>The frontend's own callback route, registered with the provider as a redirect_uri,
    /// e.g. http://localhost:3000/auth/callback. The provider redirects the browser straight there;
    /// the frontend page then calls POST /api/auth/exchange with the code it received.</summary>
    public string RedirectUri { get; set; } = "";

    /// <summary>Space-separated scopes requested at login. OIDC providers typically want
    /// "openid profile offline_access" (offline_access is what grants a refresh token on most of them)
    /// plus whatever API scope makes the access token a JWT for this app.</summary>
    public string Scope { get; set; } = "personalai";

    /// <summary>Extra query parameters appended to the authorize URL, for provider-specific needs such as
    /// Auth0's <c>audience</c>, RFC 8707's <c>resource</c>, or <c>prompt</c>.</summary>
    public Dictionary<string, string> AuthorizeParameters { get; set; } = new();

    // ---- Endpoint discovery -------------------------------------------------------------------------

    /// <summary>Read endpoints (and, when <see cref="JwtKey"/> is empty, signing keys) from the provider's
    /// OIDC discovery document. Any endpoint set explicitly below still wins over the discovered one.</summary>
    public bool UseDiscovery { get; set; } = true;

    /// <summary>Discovery document URL; defaults to <c>{Authority}/.well-known/openid-configuration</c>.</summary>
    public string MetadataAddress { get; set; } = "";

    /// <summary>Require https for discovery/JWKS fetches. Only turn off for a local test provider.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Explicit endpoints — absolute URLs, or paths relative to <see cref="Authority"/>. Empty
    /// means "use discovery"; a revocation/introspection/end-session endpoint that is neither set nor
    /// discovered just disables that feature.</summary>
    public string AuthorizationEndpoint { get; set; } = "";
    public string TokenEndpoint { get; set; } = "";
    public string RevocationEndpoint { get; set; } = "";
    public string IntrospectionEndpoint { get; set; } = "";
    public string EndSessionEndpoint { get; set; } = "";

    /// <summary>Signing-key set URL for when discovery is off; discovery provides it otherwise.</summary>
    public string JwksUri { get; set; } = "";

    /// <summary>How the client authenticates to the token/revocation/introspection endpoints:
    /// <c>client_secret_post</c> (credentials in the form body) or <c>client_secret_basic</c>
    /// (HTTP Basic, RFC 6749 §2.3.1).</summary>
    public string TokenEndpointAuthMethod { get; set; } = "client_secret_post";

    // ---- Access token validation --------------------------------------------------------------------

    /// <summary>Shared HMAC signing key, for providers that sign with a symmetric secret.
    /// Leave empty to verify signatures against the provider's JWKS instead — the normal case.</summary>
    public string JwtKey { get; set; } = "";

    /// <summary>Expected <c>iss</c>. With discovery it defaults to the document's issuer; empty with
    /// discovery off means the issuer is not checked.</summary>
    public string Issuer { get; set; } = "";

    /// <summary>Expected <c>aud</c> of access tokens (the API identifier registered with the provider).
    /// Empty means the audience is not checked.</summary>
    public string Audience { get; set; } = "";

    /// <summary>Reject access tokens issued to a different client. Guards against a provider that signs
    /// every client's tokens with one key; less important when <see cref="Audience"/> is validated.</summary>
    public bool ValidateClientId { get; set; } = true;

    /// <summary>Claims that may carry the client id, first one present wins: <c>client_id</c> (RFC 9068),
    /// <c>azp</c> (OIDC: Keycloak, Auth0, Entra v2), <c>cid</c> (Okta), <c>appid</c> (Entra v1).</summary>
    public List<string> ClientIdClaimTypes { get; set; } = ["client_id", "azp", "cid", "appid"];

    /// <summary>Also ask the provider's introspection endpoint (RFC 7662) whether each access token is still
    /// active, catching revocation before the token expires. No-op when no introspection endpoint is known.</summary>
    public bool IntrospectAccessTokens { get; set; } = true;

    /// <summary>How long a token's introspection result is remembered in memory before it is asked again.</summary>
    public int ValidateCacheSeconds { get; set; } = 60;

    // ---- Logout -------------------------------------------------------------------------------------

    /// <summary>Where the provider's end-session endpoint (OIDC RP-initiated logout) sends the browser
    /// afterwards, e.g. http://localhost:3000/. Must be registered with the provider.</summary>
    public string PostLogoutRedirectUri { get; set; } = "";

    public string ResolveEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return "";
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
            return endpoint;
        return $"{Authority.TrimEnd('/')}/{endpoint.TrimStart('/')}";
    }

    public string ResolveMetadataAddress() =>
        string.IsNullOrWhiteSpace(MetadataAddress)
            ? $"{Authority.TrimEnd('/')}/.well-known/openid-configuration"
            : ResolveEndpoint(MetadataAddress);
}
