using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using PersonalAI.Api.BackgroundServices;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Sandbox;
using PersonalAI.Core.Services.Tools;
using PersonalAI.Sandbox.Protocol;
using PersonalAI.Core.Services.Sessions;

namespace PersonalAI.Tests.Sandbox;

/// <summary>Covers SignalHostPowerStateAsync — the reaper tick's HostStopUrl trigger — against a real
/// (InMemory) AppDbContext/SandboxRepository, since the interesting behaviour here is entirely in what
/// GetAllActiveAsync's actual query returns, not in the candidate-selection logic SandboxReapPlannerTests
/// already covers in isolation.</summary>
public class SandboxReaperHostedServiceTests
{
    private static (SandboxReaperHostedService Reaper, Mock<ISandboxHostPowerSignaler> PowerSignaler, IServiceProvider Services) NewReaper()
    {
        // The database name must be captured once, outside the configure lambda — AddDbContext invokes
        // that lambda fresh for every scope, so a Guid.NewGuid() call inside it would hand each scope its
        // own private in-memory database instead of one shared store (see SandboxToolTests.NewTool).
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<ISandboxRepository, PersonalAI.Core.Services.Sandbox.SandboxRepository>();
        services.AddScoped<IChatSessionRepository>(_ => Mock.Of<IChatSessionRepository>());
        var provider = services.BuildServiceProvider();

        var powerSignaler = new Mock<ISandboxHostPowerSignaler>();
        var reaper = new SandboxReaperHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new SandboxSettings()),
            powerSignaler.Object, new ToolConfigCache(), Mock.Of<ILogger<SandboxReaperHostedService>>());

        return (reaper, powerSignaler, provider);
    }

    private static async Task SeedAsync(IServiceProvider services, SandboxInstanceStatus status)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SandboxInstances.Add(new SandboxInstance
        {
            Kind = SandboxKind.Session,
            OwnerId = Guid.NewGuid(),
            Image = "python:3.12-slim",
            Status = status
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SignalHostPowerStateAsync_NoActiveSandboxes_FiresTheStopSignal()
    {
        var (reaper, powerSignaler, _) = NewReaper();

        await reaper.SignalHostPowerStateAsync(CancellationToken.None);

        powerSignaler.Verify(p => p.TryFireStopAsync(It.IsAny<CancellationToken>()), Times.Once);
        powerSignaler.Verify(p => p.NotifyActiveSandboxObserved(), Times.Never);
    }

    [Fact]
    public async Task SignalHostPowerStateAsync_ARunningSandboxExists_NotifiesActiveInsteadOfFiringStop()
    {
        var (reaper, powerSignaler, services) = NewReaper();
        await SeedAsync(services, SandboxInstanceStatus.Running);

        await reaper.SignalHostPowerStateAsync(CancellationToken.None);

        powerSignaler.Verify(p => p.NotifyActiveSandboxObserved(), Times.Once);
        powerSignaler.Verify(p => p.TryFireStopAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(SandboxInstanceStatus.Creating)]
    [InlineData(SandboxInstanceStatus.Orphaned)]
    public async Task SignalHostPowerStateAsync_ANonDestroyedSandboxOfAnyStatus_CountsAsActive(SandboxInstanceStatus status)
    {
        // Creating and Orphaned both represent ongoing intent to use a sandbox (an Orphaned row is simply
        // recreated on next use) -- neither should trigger a premature stop just because it isn't Running.
        var (reaper, powerSignaler, services) = NewReaper();
        await SeedAsync(services, status);

        await reaper.SignalHostPowerStateAsync(CancellationToken.None);

        powerSignaler.Verify(p => p.NotifyActiveSandboxObserved(), Times.Once);
        powerSignaler.Verify(p => p.TryFireStopAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SignalHostPowerStateAsync_OnlyDestroyedSandboxesExist_StillFiresTheStopSignal()
    {
        var (reaper, powerSignaler, services) = NewReaper();
        await SeedAsync(services, SandboxInstanceStatus.Destroyed);

        await reaper.SignalHostPowerStateAsync(CancellationToken.None);

        powerSignaler.Verify(p => p.TryFireStopAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
