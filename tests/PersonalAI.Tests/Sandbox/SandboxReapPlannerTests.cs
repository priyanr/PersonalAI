using FluentAssertions;
using PersonalAI.Api.BackgroundServices;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Sandbox.Protocol;

namespace PersonalAI.Tests.Sandbox;

/// <summary>Covers SandboxReaperHostedService's candidate-selection logic in isolation — no database, no
/// hosted service, no fake ISandboxHostClient — because the DeleteFiles decision is the part that's easy
/// to get backwards and would silently delete a user's chat-session files.</summary>
public class SandboxReapPlannerTests
{
    private static SandboxInstance NewInstance(SandboxKind kind, Guid ownerId, SandboxInstanceStatus status = SandboxInstanceStatus.Running) => new()
    {
        Kind = kind,
        OwnerId = ownerId,
        Image = "python:3.12-slim",
        Status = status
    };

    [Fact]
    public void Plan_IdleChatSessionSandbox_RetiresContainerButKeepsFiles()
    {
        var instance = NewInstance(SandboxKind.Session, Guid.NewGuid());

        var plan = SandboxReapPlanner.Plan([instance], [instance], new HashSet<Guid> { instance.OwnerId });

        var entry = plan.Should().ContainSingle().Subject;
        entry.Instance.Should().BeSameAs(instance);
        entry.DeleteFiles.Should().BeFalse();
    }

    [Fact]
    public void Plan_IdleEphemeralSandbox_DeletesFilesAsAHardKillSafetyNet()
    {
        var instance = NewInstance(SandboxKind.Ephemeral, Guid.NewGuid());

        var plan = SandboxReapPlanner.Plan([instance], [], new HashSet<Guid>());

        var entry = plan.Should().ContainSingle().Subject;
        entry.Instance.Should().BeSameAs(instance);
        entry.DeleteFiles.Should().BeTrue();
    }

    [Fact]
    public void Plan_SessionSandboxWhoseOwningChatSessionIsGone_DeletesFilesEvenThoughItIsNotIdle()
    {
        var instance = NewInstance(SandboxKind.Session, Guid.NewGuid());
        // Not present in idleCandidates at all — it isn't idle — but its owning ChatSession no longer exists.
        var plan = SandboxReapPlanner.Plan([], [instance], new HashSet<Guid>());

        var entry = plan.Should().ContainSingle().Subject;
        entry.Instance.Should().BeSameAs(instance);
        entry.DeleteFiles.Should().BeTrue();
    }

    [Fact]
    public void Plan_SessionSandboxWhoseOwningChatSessionStillExists_IsNotReaped()
    {
        var instance = NewInstance(SandboxKind.Session, Guid.NewGuid());

        var plan = SandboxReapPlanner.Plan([], [instance], new HashSet<Guid> { instance.OwnerId });

        plan.Should().BeEmpty();
    }

    [Fact]
    public void Plan_IdleSessionSandboxWhoseOwningChatSessionIsAlsoGone_GoneSessionRuleOverridesKeepFiles()
    {
        var instance = NewInstance(SandboxKind.Session, Guid.NewGuid());
        // The idle sweep alone would keep files (Session kind); the gone-session rule must win instead.
        var plan = SandboxReapPlanner.Plan([instance], [instance], new HashSet<Guid>());

        var entry = plan.Should().ContainSingle().Subject;
        entry.DeleteFiles.Should().BeTrue();
    }

    [Fact]
    public void Plan_NothingIdleAndEverySessionStillExists_ReturnsAnEmptyPlan()
    {
        var instance = NewInstance(SandboxKind.Session, Guid.NewGuid());

        var plan = SandboxReapPlanner.Plan([], [instance], new HashSet<Guid> { instance.OwnerId });

        plan.Should().BeEmpty();
    }

    [Fact]
    public void Plan_MultipleUnrelatedCandidates_EachGetsItsOwnCorrectDecision()
    {
        var idleSession = NewInstance(SandboxKind.Session, Guid.NewGuid());
        var idleEphemeral = NewInstance(SandboxKind.Ephemeral, Guid.NewGuid());
        var goneSession = NewInstance(SandboxKind.Session, Guid.NewGuid());
        var liveSession = NewInstance(SandboxKind.Session, Guid.NewGuid());

        var allSessionSandboxes = new[] { idleSession, goneSession, liveSession };
        var existingOwnerIds = new HashSet<Guid> { idleSession.OwnerId, liveSession.OwnerId };

        var plan = SandboxReapPlanner.Plan([idleSession, idleEphemeral], allSessionSandboxes, existingOwnerIds)
            .ToDictionary(e => e.Instance.Id, e => e.DeleteFiles);

        plan.Should().HaveCount(3);
        plan[idleSession.Id].Should().BeFalse();
        plan[idleEphemeral.Id].Should().BeTrue();
        plan[goneSession.Id].Should().BeTrue();
        plan.Should().NotContainKey(liveSession.Id);
    }
}
