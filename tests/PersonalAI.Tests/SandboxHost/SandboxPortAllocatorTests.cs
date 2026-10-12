using FluentAssertions;
using PersonalAI.SandboxHost;

namespace PersonalAI.Tests.SandboxHost;

/// <summary>DockerSandboxManager's real TCP bind-test is not exercised here — Allocate takes the
/// availability check as a delegate specifically so this class's selection logic (which ports, in what
/// order, respecting what's already handed out) is testable without a socket or a Docker daemon.</summary>
public class SandboxPortAllocatorTests
{
    private static bool AlwaysFree(int port) => true;

    [Fact]
    public void Allocate_NoPreferred_ReturnsCountPortsFromTheLowEndOfTheRange()
    {
        var allocator = new SandboxPortAllocator(rangeStart: 50000, rangeEnd: 50010, count: 3);

        var result = allocator.Allocate([], AlwaysFree);

        result.Should().Equal(50000, 50001, 50002);
    }

    [Fact]
    public void Allocate_PreferredPortsAreFree_ReusesThemExactly()
    {
        var allocator = new SandboxPortAllocator(rangeStart: 50000, rangeEnd: 50010, count: 3);

        var result = allocator.Allocate([50007, 50008, 50009], AlwaysFree);

        result.Should().Equal(50007, 50008, 50009);
    }

    [Fact]
    public void Allocate_SomePreferredPortsUnavailable_FallsBackToRangeForTheRest()
    {
        var allocator = new SandboxPortAllocator(rangeStart: 50000, rangeEnd: 50010, count: 3);
        bool IsFree(int port) => port != 50008; // one of the three preferred ports is taken

        var result = allocator.Allocate([50007, 50008, 50009], IsFree);

        // Keeps the two still-free preferred ports and fills the gap from the low end of the range,
        // skipping anything already selected.
        result.Should().Equal(50007, 50009, 50000);
    }

    [Fact]
    public void Allocate_TwoCallsDoNotHandOutTheSamePortTwice()
    {
        var allocator = new SandboxPortAllocator(rangeStart: 50000, rangeEnd: 50010, count: 3);

        var first = allocator.Allocate([], AlwaysFree);
        var second = allocator.Allocate([], AlwaysFree);

        second.Should().NotIntersectWith(first);
    }

    [Fact]
    public void Release_FreesPortsForReuseByALaterAllocate()
    {
        var allocator = new SandboxPortAllocator(rangeStart: 50000, rangeEnd: 50002, count: 3);

        var first = allocator.Allocate([], AlwaysFree);
        allocator.Release(first);
        var second = allocator.Allocate([], AlwaysFree);

        second.Should().Equal(first);
    }

    [Fact]
    public void Seed_MarksPortsAsAllocatedWithoutGoingThroughAllocate()
    {
        // Mirrors what DockerSandboxManager does on discovering an already-running adopted container:
        // a sandbox that needs only 1 port here, but the range's first two slots were already claimed
        // by something Seed told the allocator about, must skip straight to the third.
        var allocator = new SandboxPortAllocator(rangeStart: 50000, rangeEnd: 50002, count: 1);
        allocator.Seed([50000, 50001]);

        var result = allocator.Allocate([], AlwaysFree);

        result.Should().Equal(50002);
    }

    [Fact]
    public void Allocate_LiveAvailabilityCheckRejectsAPort_SkipsIt()
    {
        var allocator = new SandboxPortAllocator(rangeStart: 50000, rangeEnd: 50010, count: 2);
        bool IsFree(int port) => port != 50000; // e.g. something else on the host already bound it

        var result = allocator.Allocate([], IsFree);

        result.Should().Equal(50001, 50002);
    }

    [Fact]
    public void Allocate_RangeExhausted_ThrowsRatherThanReturningFewerThanRequested()
    {
        var allocator = new SandboxPortAllocator(rangeStart: 50000, rangeEnd: 50001, count: 3);

        var act = () => allocator.Allocate([], AlwaysFree);

        act.Should().Throw<InvalidOperationException>().WithMessage("*50000-50001*");
    }

    [Fact]
    public void Allocate_RetryAfterConflict_KeepsRemainingPreferredPortsAndSkipsTheRejectedOne()
    {
        // Mirrors DockerSandboxManager's retry: the first attempt's ports are released, the port Docker
        // blamed is rejected, and the rest are preferred again.
        var allocator = new SandboxPortAllocator(rangeStart: 50000, rangeEnd: 50010, count: 3);
        var first = allocator.Allocate([], AlwaysFree);
        allocator.Release(first);
        var rejected = first[1];

        var second = allocator.Allocate(first.Where(p => p != rejected).ToList(), p => p != rejected);

        second.Should().Equal(first[0], first[2], 50003);
    }

    [Theory]
    [InlineData("Docker API responded with status code=InternalServerError, response={\"message\":\"failed to set up container networking: driver failed programming external connectivity on endpoint personalai-sbx-fd595848 (0a1f): failed to bind host port 0.0.0.0:49156/tcp: address already in use\"}", 49156)]
    [InlineData("failed to bind host port [::]:20001/tcp: address already in use", 20001)]
    [InlineData("driver failed programming external connectivity on endpoint x: Bind for 0.0.0.0:20002 failed: port is already allocated", 20002)]
    public void ParseConflictPort_ExtractsTheBlamedPort(string message, int expected)
    {
        SandboxPortAllocator.IsPortConflict(message).Should().BeTrue();
        SandboxPortAllocator.ParseConflictPort(message).Should().Be(expected);
    }

    [Fact]
    public void ParseConflictPort_UnrecognisedMessage_ReturnsNull()
    {
        SandboxPortAllocator.ParseConflictPort("address already in use").Should().BeNull();
    }

    [Fact]
    public void IsPortConflict_OtherDockerError_IsFalse()
    {
        SandboxPortAllocator.IsPortConflict("No such image: python:3.12-slim").Should().BeFalse();
    }
}
