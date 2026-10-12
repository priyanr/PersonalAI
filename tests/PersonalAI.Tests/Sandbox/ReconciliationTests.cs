using FluentAssertions;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Services.Sandbox;
using PersonalAI.Sandbox.Protocol;

namespace PersonalAI.Tests.Sandbox;

public class ReconciliationTests
{
    private static SandboxInstance MakeInstance(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Kind = SandboxKind.Session,
        OwnerId = Guid.NewGuid(),
        Image = "python:3.12-slim"
    };

    [Fact]
    public void Reconcile_DbRowAndContainerBothAlive_MarksRunning()
    {
        var instance = MakeInstance();
        var reported = new AdoptedSandbox(instance.Id, "container-123", "running", DateTimeOffset.UtcNow);

        var results = SandboxReconciler.Reconcile([instance], [reported]);

        results.Should().ContainSingle();
        results[0].Instance.Should().BeSameAs(instance);
        results[0].Transition.Should().Be(SandboxReconciler.Transition.MarkRunning);
        results[0].ContainerId.Should().Be("container-123");
    }

    [Fact]
    public void Reconcile_DbRowSurvivesButNoContainer_MarksOrphaned_FilesImplicitlySurvive()
    {
        // The whole point of "orphaned" over "destroyed": the host directory keyed by owner is untouched
        // by this transition, so a later EnsureSandboxAsync call re-binds the same directory and nothing
        // is lost, even though there is no live container right now.
        var instance = MakeInstance();

        var results = SandboxReconciler.Reconcile([instance], []);

        results.Should().ContainSingle();
        results[0].Instance.Should().BeSameAs(instance);
        results[0].Transition.Should().Be(SandboxReconciler.Transition.MarkOrphaned);
        results[0].ContainerId.Should().BeNull();
    }

    [Fact]
    public void Reconcile_ContainerWithNoMatchingDbRow_RecordedAsUntracked()
    {
        var reported = new AdoptedSandbox(Guid.NewGuid(), "container-stale", "running", DateTimeOffset.UtcNow);

        var results = SandboxReconciler.Reconcile([], [reported]);

        results.Should().ContainSingle();
        results[0].Instance.Should().BeNull();
        results[0].Transition.Should().Be(SandboxReconciler.Transition.Untracked);
        results[0].ContainerId.Should().Be("container-stale");
    }

    [Fact]
    public void Reconcile_MixOfAllThreeCases_ProducesOneTransitionEach()
    {
        var running = MakeInstance();
        var orphaned = MakeInstance();
        var untrackedContainerId = Guid.NewGuid();

        var reported = new[]
        {
            new AdoptedSandbox(running.Id, "container-running", "running", DateTimeOffset.UtcNow),
            new AdoptedSandbox(untrackedContainerId, "container-untracked", "running", DateTimeOffset.UtcNow)
        };

        var results = SandboxReconciler.Reconcile([running, orphaned], reported);

        results.Should().HaveCount(3);
        results.Should().ContainSingle(r => r.Transition == SandboxReconciler.Transition.MarkRunning && r.Instance == running);
        results.Should().ContainSingle(r => r.Transition == SandboxReconciler.Transition.MarkOrphaned && r.Instance == orphaned);
        results.Should().ContainSingle(r => r.Transition == SandboxReconciler.Transition.Untracked && r.Instance == null);
    }

    [Fact]
    public void Reconcile_NoDbRowsAndNoContainers_ProducesNoTransitions()
    {
        SandboxReconciler.Reconcile([], []).Should().BeEmpty();
    }
}
