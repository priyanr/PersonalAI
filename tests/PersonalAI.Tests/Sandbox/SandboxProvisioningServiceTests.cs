using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.Sandbox;
using PersonalAI.Core.Tools;
using PersonalAI.Sandbox.Protocol;

namespace PersonalAI.Tests.Sandbox;

/// <summary>EnsureAsync's full get-or-create/recreate behavior is already thoroughly proven, unchanged, by
/// SandboxToolTests (which exercises SandboxTool.ExecuteAsync as a black box after the pure extraction of
/// this method's body) — so this file only smoke-tests EnsureAsync and focuses its real coverage on what's
/// actually new here: TryEnsureExistingAsync and CheckBrowsableAsync, the browsing-only variants that never
/// create a sandbox that never existed for an owner.</summary>
public class SandboxProvisioningServiceTests
{
    private sealed record Harness(SandboxProvisioningService Service, ISandboxRepository Repository, Mock<ISandboxHostClient> HostClient);

    private static Harness NewHarness()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped<ISandboxRepository, SandboxRepository>();
        var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<ISandboxRepository>();

        var hostClient = new Mock<ISandboxHostClient>();
        hostClient
            .Setup(h => h.EnsureSandboxAsync(It.IsAny<Guid>(), It.IsAny<SandboxKind>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SandboxEnsureResult("/data/sandbox/owner", [51000, 51001]));

        return new Harness(new SandboxProvisioningService(hostClient.Object, repository), repository, hostClient);
    }

    [Fact]
    public async Task EnsureAsync_FirstCallForOwner_CreatesARunningInstance()
    {
        var h = NewHarness();
        var owner = new SandboxOwner(SandboxOwnerKind.ChatSession, Guid.NewGuid());

        var (sandboxId, ports, wasRecreated, error) = await h.Service.EnsureAsync(owner, "python:3.12-slim", CancellationToken.None);

        error.Should().BeNull();
        wasRecreated.Should().BeFalse();
        ports.Should().Equal(51000, 51001);
        sandboxId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task TryEnsureExistingAsync_NoRowForOwner_ReturnsNotFound()
    {
        var h = NewHarness();

        var (found, _, _, status, error) = await h.Service.TryEnsureExistingAsync(Guid.NewGuid(), CancellationToken.None);

        found.Should().BeFalse();
        status.Should().BeNull();
        error.Should().BeNull();
        h.HostClient.Verify(c => c.EnsureSandboxAsync(It.IsAny<Guid>(), It.IsAny<SandboxKind>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryEnsureExistingAsync_SessionKindDestroyedRow_RecreatesAndIsFound()
    {
        var h = NewHarness();
        var ownerId = Guid.NewGuid();
        await h.Repository.AddAsync(new SandboxInstance
        {
            Kind = SandboxKind.Session,
            OwnerId = ownerId,
            Image = "python:3.12-slim",
            Status = SandboxInstanceStatus.Destroyed,
            PortsJson = "[51000,51001]"
        }, CancellationToken.None);

        var (found, sandboxId, ports, status, error) = await h.Service.TryEnsureExistingAsync(ownerId, CancellationToken.None);

        found.Should().BeTrue("a chat session's Destroyed row still has its files kept — recreating is expected");
        error.Should().BeNull();
        status.Should().Be(SandboxInstanceStatus.Running);
        sandboxId.Should().NotBe(Guid.Empty);
        ports.Should().Equal(51000, 51001);
    }

    [Fact]
    public async Task TryEnsureExistingAsync_EphemeralDestroyedRow_ReturnsNotFoundWithoutRecreating()
    {
        var h = NewHarness();
        var ownerId = Guid.NewGuid();
        await h.Repository.AddAsync(new SandboxInstance
        {
            Kind = SandboxKind.Ephemeral,
            OwnerId = ownerId,
            Image = "python:3.12-slim",
            Status = SandboxInstanceStatus.Destroyed,
            PortsJson = "[]"
        }, CancellationToken.None);

        var (found, _, _, _, error) = await h.Service.TryEnsureExistingAsync(ownerId, CancellationToken.None);

        found.Should().BeFalse("an ephemeral run's files are deleted once it's torn down — nothing to recreate");
        error.Should().BeNull();
        h.HostClient.Verify(c => c.EnsureSandboxAsync(It.IsAny<Guid>(), It.IsAny<SandboxKind>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryEnsureExistingAsync_EphemeralRunningRow_IsFoundAndReused()
    {
        var h = NewHarness();
        var ownerId = Guid.NewGuid();
        await h.Repository.AddAsync(new SandboxInstance
        {
            Kind = SandboxKind.Ephemeral,
            OwnerId = ownerId,
            Image = "python:3.12-slim",
            Status = SandboxInstanceStatus.Running,
            PortsJson = "[51000]"
        }, CancellationToken.None);

        var (found, _, ports, status, error) = await h.Service.TryEnsureExistingAsync(ownerId, CancellationToken.None);

        found.Should().BeTrue("an in-progress agent/skill run's sandbox is still browsable");
        status.Should().Be(SandboxInstanceStatus.Running);
        ports.Should().Equal(51000);
        error.Should().BeNull();
        h.HostClient.Verify(c => c.EnsureSandboxAsync(It.IsAny<Guid>(), It.IsAny<SandboxKind>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()), Times.Never,
            "a Running row needs no recreation");
    }

    [Fact]
    public async Task TryEnsureExistingAsync_OrphanedRow_RecreatesPreservingItsPreviousPorts()
    {
        var h = NewHarness();
        var ownerId = Guid.NewGuid();
        var instance = await h.Repository.AddAsync(new SandboxInstance
        {
            Kind = SandboxKind.Session,
            OwnerId = ownerId,
            Image = "python:3.12-slim",
            Status = SandboxInstanceStatus.Orphaned,
            PortsJson = "[60000,60001]"
        }, CancellationToken.None);

        await h.Service.TryEnsureExistingAsync(ownerId, CancellationToken.None);

        h.HostClient.Verify(c => c.EnsureSandboxAsync(
            instance.Id, SandboxKind.Session, ownerId, "python:3.12-slim",
            It.Is<IReadOnlyList<int>>(p => p.SequenceEqual(new[] { 60000, 60001 })), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryEnsureExistingAsync_HostThrows_ReturnsFoundWithError()
    {
        var h = NewHarness();
        var ownerId = Guid.NewGuid();
        await h.Repository.AddAsync(new SandboxInstance
        {
            Kind = SandboxKind.Session,
            OwnerId = ownerId,
            Image = "python:3.12-slim",
            Status = SandboxInstanceStatus.Orphaned,
            PortsJson = "[]"
        }, CancellationToken.None);
        h.HostClient
            .Setup(c => c.EnsureSandboxAsync(It.IsAny<Guid>(), It.IsAny<SandboxKind>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SandboxHostDisconnectedException("no broker connected"));

        var (found, _, _, _, error) = await h.Service.TryEnsureExistingAsync(ownerId, CancellationToken.None);

        found.Should().BeTrue();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task TryEnsureExistingAsync_BumpsLastUsedAt()
    {
        var h = NewHarness();
        var ownerId = Guid.NewGuid();
        await h.Repository.AddAsync(new SandboxInstance
        {
            Kind = SandboxKind.Session,
            OwnerId = ownerId,
            Image = "python:3.12-slim",
            Status = SandboxInstanceStatus.Running,
            PortsJson = "[]"
        }, CancellationToken.None);

        await h.Service.TryEnsureExistingAsync(ownerId, CancellationToken.None);

        var refreshed = await h.Repository.GetActiveByOwnerAsync(ownerId, CancellationToken.None);
        refreshed!.LastUsedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CheckBrowsableAsync_NoRowForOwner_ReturnsFalseWithoutTouchingTheBroker()
    {
        var h = NewHarness();

        var (canBrowse, status, ports) = await h.Service.CheckBrowsableAsync(Guid.NewGuid(), CancellationToken.None);

        canBrowse.Should().BeFalse();
        status.Should().BeNull();
        ports.Should().BeEmpty();
        h.HostClient.Verify(c => c.EnsureSandboxAsync(It.IsAny<Guid>(), It.IsAny<SandboxKind>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckBrowsableAsync_SessionDestroyedRow_ReturnsTrueWithoutRecreatingTheContainer()
    {
        var h = NewHarness();
        var ownerId = Guid.NewGuid();
        await h.Repository.AddAsync(new SandboxInstance
        {
            Kind = SandboxKind.Session,
            OwnerId = ownerId,
            Image = "python:3.12-slim",
            Status = SandboxInstanceStatus.Destroyed,
            PortsJson = "[]"
        }, CancellationToken.None);

        var (canBrowse, status, _) = await h.Service.CheckBrowsableAsync(ownerId, CancellationToken.None);

        canBrowse.Should().BeTrue();
        status.Should().Be(SandboxInstanceStatus.Destroyed, "this is a pure read — it must not recreate the container just to answer a status check");
        h.HostClient.Verify(c => c.EnsureSandboxAsync(It.IsAny<Guid>(), It.IsAny<SandboxKind>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckBrowsableAsync_EphemeralDestroyedRow_ReturnsFalse()
    {
        var h = NewHarness();
        var ownerId = Guid.NewGuid();
        await h.Repository.AddAsync(new SandboxInstance
        {
            Kind = SandboxKind.Ephemeral,
            OwnerId = ownerId,
            Image = "python:3.12-slim",
            Status = SandboxInstanceStatus.Destroyed,
            PortsJson = "[]"
        }, CancellationToken.None);

        var (canBrowse, _, _) = await h.Service.CheckBrowsableAsync(ownerId, CancellationToken.None);

        canBrowse.Should().BeFalse();
    }

    [Fact]
    public async Task CheckBrowsableAsync_EphemeralRunningRow_ReturnsTrue()
    {
        var h = NewHarness();
        var ownerId = Guid.NewGuid();
        await h.Repository.AddAsync(new SandboxInstance
        {
            Kind = SandboxKind.Ephemeral,
            OwnerId = ownerId,
            Image = "python:3.12-slim",
            Status = SandboxInstanceStatus.Running,
            PortsJson = "[]"
        }, CancellationToken.None);

        var (canBrowse, _, _) = await h.Service.CheckBrowsableAsync(ownerId, CancellationToken.None);

        canBrowse.Should().BeTrue();
    }

    [Fact]
    public async Task RecreateAfterContainerMissingAsync_ActiveRowExists_EndsRunningWithPortsPreserved()
    {
        var h = NewHarness();
        var owner = new SandboxOwner(SandboxOwnerKind.ChatSession, Guid.NewGuid());
        var instance = await h.Repository.AddAsync(new SandboxInstance
        {
            Kind = SandboxKind.Session,
            OwnerId = owner.Id,
            Image = "python:3.12-slim",
            Status = SandboxInstanceStatus.Running,
            PortsJson = "[60000,60001]"
        }, CancellationToken.None);
        h.HostClient
            .Setup(c => c.EnsureSandboxAsync(It.IsAny<Guid>(), It.IsAny<SandboxKind>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SandboxEnsureResult("/data/sandbox/owner", [60000, 60001]));

        var (recreated, ports, error) = await h.Service.RecreateAfterContainerMissingAsync(owner, CancellationToken.None);

        recreated.Should().BeTrue();
        error.Should().BeNull();
        ports.Should().Equal(60000, 60001);
        h.HostClient.Verify(c => c.EnsureSandboxAsync(
            instance.Id, SandboxKind.Session, owner.Id, "python:3.12-slim",
            It.Is<IReadOnlyList<int>>(p => p.SequenceEqual(new[] { 60000, 60001 })), It.IsAny<CancellationToken>()), Times.Once,
            "the broker report came in while the DB row still said Running -- this is the one path that must recreate even though EnsureAsync's own Status check would have skipped it");
        var refreshed = await h.Repository.GetActiveByOwnerAsync(owner.Id, CancellationToken.None);
        refreshed!.Status.Should().Be(SandboxInstanceStatus.Running);
    }

    [Fact]
    public async Task RecreateAfterContainerMissingAsync_NoActiveRowForOwner_ReturnsNotRecreatedWithoutTouchingTheBroker()
    {
        var h = NewHarness();
        var owner = new SandboxOwner(SandboxOwnerKind.ChatSession, Guid.NewGuid());

        var (recreated, ports, error) = await h.Service.RecreateAfterContainerMissingAsync(owner, CancellationToken.None);

        recreated.Should().BeFalse();
        ports.Should().BeEmpty();
        error.Should().NotBeNullOrEmpty();
        h.HostClient.Verify(c => c.EnsureSandboxAsync(It.IsAny<Guid>(), It.IsAny<SandboxKind>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecreateAfterContainerMissingAsync_HostThrowsDuringRecreate_LeavesTheRowOrphanedNotFalselyRunning()
    {
        var h = NewHarness();
        var owner = new SandboxOwner(SandboxOwnerKind.ChatSession, Guid.NewGuid());
        await h.Repository.AddAsync(new SandboxInstance
        {
            Kind = SandboxKind.Session,
            OwnerId = owner.Id,
            Image = "python:3.12-slim",
            Status = SandboxInstanceStatus.Running,
            PortsJson = "[]"
        }, CancellationToken.None);
        h.HostClient
            .Setup(c => c.EnsureSandboxAsync(It.IsAny<Guid>(), It.IsAny<SandboxKind>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SandboxHostDisconnectedException("no broker connected"));

        var (recreated, _, error) = await h.Service.RecreateAfterContainerMissingAsync(owner, CancellationToken.None);

        recreated.Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
        // The critical assertion: the row must NOT be left as Running -- that's the exact bug this method
        // exists to fix (a dead container behind a DB row that says everything is fine forever).
        var refreshed = await h.Repository.GetActiveByOwnerAsync(owner.Id, CancellationToken.None);
        refreshed!.Status.Should().Be(SandboxInstanceStatus.Orphaned);
    }
}
