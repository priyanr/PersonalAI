using FluentAssertions;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Sandbox;
using PersonalAI.Sandbox.Protocol;
using PersonalAI.SandboxHost;

namespace PersonalAI.Tests.Sandbox;

/// <summary>SandboxHostRegistry.MaxFrameBytesFor / BrokerConnection.MaxFrameBytesFor size the WebSocket
/// transport's inbound frame cap off the new MaxZipBytes/MaxDownloadBytes settings. Getting this wrong is
/// not a "that one request fails" bug — WebSocketFrameTransport.ReceiveAsync throws when the cap is
/// exceeded, which SandboxFramePump's reader loop turns into faulting every pending request and killing
/// the whole broker connection. Both sides must independently produce a cap that comfortably exceeds the
/// configured caps' base64+JSON-envelope size, and never shrink below WebSocketFrameTransport's own
/// 16 MB default (which plain exec/file ops already rely on). Both overloads below now delegate to the
/// single shared SandboxFrameLimits.MaxFrameBytesFor formula in PersonalAI.Sandbox.Protocol, so
/// BothSides_DefaultSettings_AgreeOnTheSameCap is a structural guarantee rather than a coincidence of two
/// copy-pasted formulas staying in sync.</summary>
public class MaxFrameBytesTests
{
    [Fact]
    public void SharedFormula_DefaultCaps_ExceedsBase64InflatedZipCapWithMargin()
    {
        var maxFrameBytes = SandboxFrameLimits.MaxFrameBytesFor(maxZipBytes: 50 * 1024 * 1024, maxDownloadBytes: 25 * 1024 * 1024);

        var base64Inflated = (long)(50 * 1024 * 1024 * 4.0 / 3.0);
        maxFrameBytes.Should().BeGreaterThan((int)base64Inflated);
    }

    [Fact]
    public void ApiSide_DefaultSettings_ExceedsBase64InflatedZipCapWithMargin()
    {
        var settings = new SandboxSettings();

        var maxFrameBytes = SandboxHostRegistry.MaxFrameBytesFor(settings);

        var base64Inflated = (long)(settings.MaxZipBytes * 4.0 / 3.0);
        maxFrameBytes.Should().BeGreaterThan((int)base64Inflated);
    }

    [Fact]
    public void ApiSide_TinyCaps_NeverShrinksBelowTransportDefault()
    {
        var settings = new SandboxSettings { MaxZipBytes = 1024, MaxDownloadBytes = 1024 };

        var maxFrameBytes = SandboxHostRegistry.MaxFrameBytesFor(settings);

        maxFrameBytes.Should().BeGreaterOrEqualTo(16 * 1024 * 1024);
    }

    [Fact]
    public void ApiSide_LargerDownloadCapThanZipCap_UsesTheLargerOfTheTwo()
    {
        var smallZip = new SandboxSettings { MaxZipBytes = 1024, MaxDownloadBytes = 30 * 1024 * 1024 };
        var smallDownload = new SandboxSettings { MaxZipBytes = 30 * 1024 * 1024, MaxDownloadBytes = 1024 };

        SandboxHostRegistry.MaxFrameBytesFor(smallZip).Should().Be(SandboxHostRegistry.MaxFrameBytesFor(smallDownload));
    }

    [Fact]
    public void BrokerSide_DefaultSettings_ExceedsBase64InflatedZipCapWithMargin()
    {
        var settings = new BrokerSettings();

        var maxFrameBytes = BrokerConnection.MaxFrameBytesFor(settings);

        var base64Inflated = (long)(settings.MaxZipBytes * 4.0 / 3.0);
        maxFrameBytes.Should().BeGreaterThan((int)base64Inflated);
    }

    [Fact]
    public void BrokerSide_TinyCaps_NeverShrinksBelowTransportDefault()
    {
        var settings = new BrokerSettings { MaxZipBytes = 1024, MaxDownloadBytes = 1024 };

        var maxFrameBytes = BrokerConnection.MaxFrameBytesFor(settings);

        maxFrameBytes.Should().BeGreaterOrEqualTo(16 * 1024 * 1024);
    }

    [Fact]
    public void BothSides_DefaultSettings_AgreeOnTheSameCap()
    {
        // Not a protocol requirement (each side only needs its own inbound cap sized correctly), but the
        // default configs are meant to match, and a silent divergence here would be exactly the kind of
        // thing that's easy to miss when only one appsettings.json gets edited.
        var apiCap = SandboxHostRegistry.MaxFrameBytesFor(new SandboxSettings());
        var brokerCap = BrokerConnection.MaxFrameBytesFor(new BrokerSettings());

        apiCap.Should().Be(brokerCap);
    }
}
