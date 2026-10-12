using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Sandbox;
using PersonalAI.Core.Services.Tools;
using PersonalAI.Tests.Services;

namespace PersonalAI.Tests.Sandbox;

/// <summary>SandboxTool calls TryFireStartAsync when it finds no broker connected; SandboxReaperHostedService
/// calls TryFireStopAsync/NotifyActiveSandboxObserved from its tick. Both HostStartUrl/HostStopUrl are
/// ToolConfig-backed sandbox tool settings (like AdditionalAllowedImages), not appsettings, so every test
/// here goes through ToolConfigCache rather than SandboxSettings for the URLs themselves.</summary>
public class SandboxHostPowerSignalerTests
{
    private static (SandboxHostPowerSignaler Signaler, FakeHttpMessageHandler Handler, FakeTimeProvider Clock) NewSignaler(
        ToolConfigCache? configCache = null, SandboxSettings? settings = null,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new FakeHttpMessageHandler("{}", statusCode);
        var client = new HttpClient(handler);
        var factory = Mock.Of<IHttpClientFactory>(f => f.CreateClient(SandboxHostPowerSignaler.HttpClientName) == client);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var signaler = new SandboxHostPowerSignaler(
            factory, configCache ?? new ToolConfigCache(), clock,
            Options.Create(settings ?? new SandboxSettings()), Mock.Of<ILogger<SandboxHostPowerSignaler>>());
        return (signaler, handler, clock);
    }

    private static ToolConfigCache WithUrls(string? startUrl = null, string? stopUrl = null)
    {
        var cache = new ToolConfigCache();
        var values = new Dictionary<string, string?>();
        if (startUrl is not null) values["HostStartUrl"] = startUrl;
        if (stopUrl is not null) values["HostStopUrl"] = stopUrl;
        cache.SetTool("sandbox", values);
        return cache;
    }

    [Fact]
    public async Task TryFireStartAsync_NoUrlConfigured_ReturnsNullWithoutCallingAnything()
    {
        var (signaler, handler, _) = NewSignaler();

        var result = await signaler.TryFireStartAsync(false, CancellationToken.None);

        result.Should().BeNull();
        handler.LastRequestUri.Should().BeNull();
    }

    [Fact]
    public async Task TryFireStartAsync_UrlConfigured_CallsItAndReturnsASuccessMessage()
    {
        var (signaler, handler, _) = NewSignaler(WithUrls(startUrl: "https://example.com/wake"));

        var result = await signaler.TryFireStartAsync(false, CancellationToken.None);

        handler.LastRequestUri.Should().Be("https://example.com/wake");
        result.Should().Contain("start signal was sent");
    }

    [Fact]
    public async Task TryFireStartAsync_CalledAgainWithinCooldown_DoesNotHitTheUrlASecondTime()
    {
        var (signaler, handler, clock) = NewSignaler(
            WithUrls(startUrl: "https://example.com/wake"),
            new SandboxSettings { HostStartCooldownSeconds = 120 });

        await signaler.TryFireStartAsync(false, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(60)); // still inside the 120s cooldown
        var second = await signaler.TryFireStartAsync(false, CancellationToken.None);

        second.Should().Contain("already sent recently");
    }

    [Fact]
    public async Task TryFireStartAsync_CalledAfterCooldownElapses_FiresAgain()
    {
        var (signaler, handler, clock) = NewSignaler(
            WithUrls(startUrl: "https://example.com/wake"),
            new SandboxSettings { HostStartCooldownSeconds = 120 });

        await signaler.TryFireStartAsync(false, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(121));
        var second = await signaler.TryFireStartAsync(false, CancellationToken.None);

        second.Should().Contain("start signal was sent");
    }

    [Fact]
    public async Task TryFireStartAsync_EndpointReturnsErrorStatus_MentionsTheStatusButDoesNotThrow()
    {
        var (signaler, _, _) = NewSignaler(WithUrls(startUrl: "https://example.com/wake"), statusCode: HttpStatusCode.InternalServerError);

        var result = await signaler.TryFireStartAsync(false, CancellationToken.None);

        result.Should().Contain("500");
    }

    [Fact]
    public async Task TryFireStartAsync_RequestThrows_ReturnsAFailureMessageWithoutThrowing()
    {
        // A malformed/unreachable URL throws inside HttpClient before any response comes back.
        var (signaler, _, _) = NewSignaler(WithUrls(startUrl: "not a url"));

        var act = async () => await signaler.TryFireStartAsync(false, CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Should().Contain("failed");
    }

    [Fact]
    public async Task TryFireStopAsync_NoUrlConfigured_DoesNotCallAnything()
    {
        var (signaler, handler, _) = NewSignaler();

        await signaler.TryFireStopAsync(CancellationToken.None);

        handler.LastRequestUri.Should().BeNull();
    }

    [Fact]
    public async Task TryFireStopAsync_UrlConfigured_CallsIt()
    {
        var (signaler, handler, _) = NewSignaler(WithUrls(stopUrl: "https://example.com/sleep"));

        await signaler.TryFireStopAsync(CancellationToken.None);

        handler.LastRequestUri.Should().Be("https://example.com/sleep");
    }

    [Fact]
    public async Task TryFireStopAsync_CalledTwiceWithNoActiveSandboxObservedBetween_FiresOnlyOnce()
    {
        var (signaler, handler, _) = NewSignaler(WithUrls(stopUrl: "https://example.com/sleep"));

        await signaler.TryFireStopAsync(CancellationToken.None);
        await signaler.TryFireStopAsync(CancellationToken.None);

        handler.RequestCount.Should().Be(1, "the second call is within the same idle period as the first");
    }

    [Fact]
    public async Task NotifyActiveSandboxObserved_ResetsTheDebounce_SoStopFiresAgainNextIdlePeriod()
    {
        var (signaler, handler, _) = NewSignaler(WithUrls(stopUrl: "https://example.com/sleep"));

        await signaler.TryFireStopAsync(CancellationToken.None);
        signaler.NotifyActiveSandboxObserved(); // a sandbox became active again in between
        await signaler.TryFireStopAsync(CancellationToken.None);

        handler.RequestCount.Should().Be(2);
    }
}
