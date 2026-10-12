using System.Text.Json;
using FluentAssertions;
using PersonalAI.Sandbox.Protocol;

namespace PersonalAI.Tests.Sandbox;

public class SandboxProtocolTests
{
    public static IEnumerable<object[]> AllFrames()
    {
        var requestId = Guid.NewGuid();

        yield return new object[] { new HostHello(requestId, "host-1", "24.0.5", 1) };
        yield return new object[] { new HostHelloAck(requestId, true, null) };
        yield return new object[] { new Heartbeat(requestId, DateTimeOffset.UtcNow) };
        yield return new object[] { new ListSandboxesRequest(requestId) };
        yield return new object[]
        {
            new ListSandboxesResponse(requestId,
                [new AdoptedSandbox(Guid.NewGuid(), "container-1", "running", DateTimeOffset.UtcNow)])
        };
        yield return new object[]
        {
            new CreateSandboxRequest(requestId, Guid.NewGuid(), SandboxKind.Session, Guid.NewGuid(),
                "python:3.12-slim", 500, 268_435_456, 128, false, ["NET_RAW"], [51234])
        };
        yield return new object[]
        {
            new CreateSandboxResponse(requestId, true, "container-1", "/data/sandbox/abc123", [51234, 51235], null)
        };
        yield return new object[]
        {
            new ExecRequest(requestId, Guid.NewGuid(), ["python3", "-c", "print(1)"], "/work", 60)
        };
        yield return new object[]
        {
            new ExecOutputChunk(requestId, StreamKind.Stderr, 3, Convert.ToBase64String("oops"u8.ToArray()))
        };
        yield return new object[] { new ExecCompleted(requestId, 1, false, null) };
        yield return new object[]
        {
            new ExecCompleted(requestId, null, false, "Unknown sandbox 'x'; create it first.", ContainerMissing: true)
        };
        yield return new object[]
        {
            new FileOpRequest(requestId, Guid.NewGuid(), FileOp.Write, "script.py",
                ContentBase64: Convert.ToBase64String("print(1)"u8.ToArray()), Append: true)
        };
        yield return new object[]
        {
            new FileOpRequest(requestId, Guid.NewGuid(), FileOp.ReadRaw, "photo.jpg", MaxBytes: 26_214_400)
        };
        yield return new object[]
        {
            new FileOpRequest(requestId, Guid.NewGuid(), FileOp.Zip, ".",
                SelectedPaths: ["src/main.py", "notes.txt"])
        };
        yield return new object[]
        {
            new FileOpResponse(requestId, true, null,
                Entries: [new FileEntry("a.txt", false, 10, DateTimeOffset.UtcNow)],
                Hits: [new SearchHit("a.txt", 4, "match here")],
                ReplacementCount: 2)
        };
        yield return new object[]
        {
            new FileOpResponse(requestId, false, "Unknown sandbox 'x'; create it first.", ContainerMissing: true)
        };
        yield return new object[] { new DestroySandboxRequest(requestId, Guid.NewGuid(), true) };
        yield return new object[] { new DestroySandboxResponse(requestId, true, null) };
        yield return new object[] { new ErrorFrame(requestId, "boom") };
    }

    /// <summary>Round-trips one instance of every SandboxFrame subtype through the polymorphic base
    /// type. This is the cheap net that catches a typo'd [JsonDerivedType] discriminator or a field that
    /// silently fails to (de)serialize — both would otherwise only surface once the API and the broker
    /// disagree on the wire.</summary>
    [Theory]
    [MemberData(nameof(AllFrames))]
    public void RoundTrips_ThroughPolymorphicBase_PreservingTypeAndFields(SandboxFrame frame)
    {
        var json = JsonSerializer.Serialize(frame, SandboxJson.Options);
        var roundTripped = JsonSerializer.Deserialize<SandboxFrame>(json, SandboxJson.Options);

        roundTripped.Should().NotBeNull();
        roundTripped.Should().BeOfType(frame.GetType());
        roundTripped!.RequestId.Should().Be(frame.RequestId);
        roundTripped.Should().BeEquivalentTo(frame, options => options.RespectingRuntimeTypes());
    }

    [Fact]
    public void ExecOutputChunk_NonUtf8Base64Payload_RoundTripsExactBytes()
    {
        // Not valid UTF-8 on its own (a lone continuation byte and a lone leading byte with no
        // continuation) — exactly the kind of raw process output base64 exists to carry safely.
        byte[] rawBytes = [0xFF, 0xFE, 0x80, 0x81, 0x00, 0xC0];
        var frame = new ExecOutputChunk(Guid.NewGuid(), StreamKind.Stdout, 1, Convert.ToBase64String(rawBytes));

        var json = JsonSerializer.Serialize<SandboxFrame>(frame, SandboxJson.Options);
        var roundTripped = JsonSerializer.Deserialize<SandboxFrame>(json, SandboxJson.Options)
            .Should().BeOfType<ExecOutputChunk>().Subject;

        Convert.FromBase64String(roundTripped.DataBase64).Should().BeEquivalentTo(rawBytes);
    }

    [Fact]
    public void ExecCompleted_JsonOmittingContainerMissing_DeserializesToFalse()
    {
        // Proves wire backward-compat: a broker/API build from before this field existed still
        // deserializes cleanly on the other end, defaulting to "not a self-heal signal" rather than
        // failing to deserialize or throwing.
        var requestId = Guid.NewGuid();
        var json = $$"""{"$type":"sandbox.exec.completed","RequestId":"{{requestId}}","ExitCode":0,"TimedOut":false,"Error":null}""";

        var frame = JsonSerializer.Deserialize<SandboxFrame>(json, SandboxJson.Options)
            .Should().BeOfType<ExecCompleted>().Subject;

        frame.ContainerMissing.Should().BeFalse();
    }

    [Fact]
    public void FileOpResponse_JsonOmittingContainerMissing_DeserializesToFalse()
    {
        var requestId = Guid.NewGuid();
        var json = $$"""{"$type":"sandbox.file.result","RequestId":"{{requestId}}","Success":true,"Error":null}""";

        var frame = JsonSerializer.Deserialize<SandboxFrame>(json, SandboxJson.Options)
            .Should().BeOfType<FileOpResponse>().Subject;

        frame.ContainerMissing.Should().BeFalse();
    }

    [Fact]
    public void FileOpResponse_NonUtf8Base64Content_RoundTripsExactBytes()
    {
        byte[] rawBytes = [0x00, 0xFF, 0xD8, 0xFF, 0xE0]; // looks like the start of a binary (JPEG) file
        var frame = new FileOpResponse(Guid.NewGuid(), true, null, ContentBase64: Convert.ToBase64String(rawBytes));

        var json = JsonSerializer.Serialize<SandboxFrame>(frame, SandboxJson.Options);
        var roundTripped = JsonSerializer.Deserialize<SandboxFrame>(json, SandboxJson.Options)
            .Should().BeOfType<FileOpResponse>().Subject;

        Convert.FromBase64String(roundTripped.ContentBase64!).Should().BeEquivalentTo(rawBytes);
    }
}
