using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using PersonalAI.Core.Services.Auth;

namespace PersonalAI.Tests.Services;

public class TokenValidationCacheTests
{
    private sealed class CountingHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls;
        public string Body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static (TokenValidationCache cache, FakeTimeProvider clock) Create(CountingHandler handler)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = OAuthTestFactory.CreateClient(OAuthTestFactory.CustomProviderSettings(), handler);
        var cache = new TokenValidationCache(() => client, TimeSpan.FromMinutes(1), clock);
        return (cache, clock);
    }

    [Fact]
    public async Task SecondCallWithinWindow_DoesNotHitProvider()
    {
        var handler = new CountingHandler("""{"active":true,"client_id":"c"}""");
        var (cache, clock) = Create(handler);

        (await cache.IsActiveAsync("tok", CancellationToken.None)).Should().BeTrue();
        clock.Advance(TimeSpan.FromSeconds(59));
        (await cache.IsActiveAsync("tok", CancellationToken.None)).Should().BeTrue();

        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task AfterWindow_ProviderIsAskedAgain_AndRevocationIsPickedUp()
    {
        var handler = new CountingHandler("""{"active":true}""");
        var (cache, clock) = Create(handler);
        await cache.IsActiveAsync("tok", CancellationToken.None);

        handler.Body = """{"active":false}""";
        clock.Advance(TimeSpan.FromSeconds(61));

        (await cache.IsActiveAsync("tok", CancellationToken.None)).Should().BeFalse();
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task InactiveResult_IsCachedToo()
    {
        var handler = new CountingHandler("""{"active":false}""");
        var (cache, _) = Create(handler);

        (await cache.IsActiveAsync("tok", CancellationToken.None)).Should().BeFalse();
        (await cache.IsActiveAsync("tok", CancellationToken.None)).Should().BeFalse();

        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task CacheEntryNeverOutlivesTokenExpiry()
    {
        var handler = new CountingHandler("{}");
        var (cache, clock) = Create(handler);
        handler.Body = $$"""{"active":true,"exp":{{clock.GetUtcNow().AddSeconds(20).ToUnixTimeSeconds()}}}""";

        await cache.IsActiveAsync("tok", CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(30));
        await cache.IsActiveAsync("tok", CancellationToken.None);

        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task DifferentTokens_AreCachedIndependently()
    {
        var handler = new CountingHandler("""{"active":true}""");
        var (cache, _) = Create(handler);

        await cache.IsActiveAsync("a", CancellationToken.None);
        await cache.IsActiveAsync("b", CancellationToken.None);

        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task ConcurrentCalls_ProduceOneProviderCall()
    {
        var handler = new CountingHandler("""{"active":true}""");
        var (cache, _) = Create(handler);

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => cache.IsActiveAsync("tok", CancellationToken.None)));

        results.Should().OnlyContain(r => r);
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ProviderError_Propagates_AndIsNotCached()
    {
        var handler = new CountingHandler("oops", HttpStatusCode.InternalServerError);
        var (cache, _) = Create(handler);

        var act = () => cache.IsActiveAsync("tok", CancellationToken.None);
        await act.Should().ThrowAsync<HttpRequestException>();
        await act.Should().ThrowAsync<HttpRequestException>();

        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task ProviderWithoutIntrospectionEndpoint_TreatsTokensAsActive()
    {
        var settings = OAuthTestFactory.CustomProviderSettings();
        settings.IntrospectionEndpoint = "";
        var handler = new CountingHandler("{}");
        var client = OAuthTestFactory.CreateClient(settings, handler);
        var cache = new TokenValidationCache(() => client, TimeSpan.FromMinutes(1), new FakeTimeProvider());

        (await cache.IsActiveAsync("tok", CancellationToken.None)).Should().BeTrue();
        handler.Calls.Should().Be(0);
    }
}
