using FluentAssertions;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.Scheduling;
// Alias needed: PersonalAI.Core.Services.Agent (the ChatAgentService namespace) collides with the
// Models.Entities.Agent entity type's simple name once PersonalAI.Core.Services.Scheduling is imported.
using AgentEntity = PersonalAI.Core.Models.Entities.Agent;

namespace PersonalAI.Tests.Services;

public class ScheduleCalculatorTests
{
    private static AgentEntity NewAgent(AgentScheduleKind kind) => new()
    {
        Name = "a",
        Prompt = "p",
        ModelName = "m",
        ScheduleKind = kind,
        Enabled = true
    };

    [Fact]
    public void Validate_Manual_NoFieldsRequired()
    {
        var agent = NewAgent(AgentScheduleKind.Manual);
        ScheduleCalculator.Validate(agent).Should().BeNull();
    }

    [Fact]
    public void Validate_Once_RequiresScheduleOnceAt()
    {
        var agent = NewAgent(AgentScheduleKind.Once);
        ScheduleCalculator.Validate(agent).Should().NotBeNull();

        agent.ScheduleOnceAt = DateTimeOffset.UtcNow.AddHours(1);
        ScheduleCalculator.Validate(agent).Should().BeNull();
    }

    [Fact]
    public void Validate_Interval_RequiresPositiveMinutes()
    {
        var agent = NewAgent(AgentScheduleKind.Interval);
        ScheduleCalculator.Validate(agent).Should().NotBeNull();

        agent.IntervalMinutes = 0;
        ScheduleCalculator.Validate(agent).Should().NotBeNull();

        agent.IntervalMinutes = 15;
        ScheduleCalculator.Validate(agent).Should().BeNull();
    }

    [Fact]
    public void Validate_Cron_RequiresValidExpression()
    {
        var agent = NewAgent(AgentScheduleKind.Cron);
        ScheduleCalculator.Validate(agent).Should().NotBeNull();

        agent.CronExpression = "not a cron";
        ScheduleCalculator.Validate(agent).Should().NotBeNull();

        agent.CronExpression = "0 9 * * *";
        ScheduleCalculator.Validate(agent).Should().BeNull();
    }

    [Fact]
    public void ComputeNextRunAt_Disabled_ReturnsNull()
    {
        var agent = NewAgent(AgentScheduleKind.Interval);
        agent.IntervalMinutes = 10;
        agent.Enabled = false;

        ScheduleCalculator.ComputeNextRunAt(agent, DateTimeOffset.UtcNow).Should().BeNull();
    }

    [Fact]
    public void ComputeNextRunAt_Manual_ReturnsNull()
    {
        var agent = NewAgent(AgentScheduleKind.Manual);
        ScheduleCalculator.ComputeNextRunAt(agent, DateTimeOffset.UtcNow).Should().BeNull();
    }

    [Fact]
    public void ComputeNextRunAt_Interval_AddsMinutesFromReferenceTime()
    {
        var agent = NewAgent(AgentScheduleKind.Interval);
        agent.IntervalMinutes = 30;
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        ScheduleCalculator.ComputeNextRunAt(agent, from).Should().Be(from.AddMinutes(30));
    }

    [Fact]
    public void ComputeNextRunAt_OnceInFuture_ReturnsThatInstant()
    {
        var agent = NewAgent(AgentScheduleKind.Once);
        var runAt = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        agent.ScheduleOnceAt = runAt;
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        ScheduleCalculator.ComputeNextRunAt(agent, from).Should().Be(runAt);
    }

    [Fact]
    public void ComputeNextRunAt_OnceAlreadyRan_ReturnsNull()
    {
        var agent = NewAgent(AgentScheduleKind.Once);
        agent.ScheduleOnceAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        agent.LastRunAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        ScheduleCalculator.ComputeNextRunAt(agent, DateTimeOffset.UtcNow).Should().BeNull();
    }

    [Fact]
    public void ComputeNextRunAt_OncePastInstant_ReturnsNull()
    {
        var agent = NewAgent(AgentScheduleKind.Once);
        agent.ScheduleOnceAt = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        ScheduleCalculator.ComputeNextRunAt(agent, DateTimeOffset.UtcNow).Should().BeNull();
    }

    [Fact]
    public void ComputeNextRunAt_Cron_DelegatesToCronExpression()
    {
        var agent = NewAgent(AgentScheduleKind.Cron);
        agent.CronExpression = "0 9 * * *";
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var next = ScheduleCalculator.ComputeNextRunAt(agent, from);

        next.Should().Be(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ComputeNextRunAt_InvalidCron_ReturnsNullInsteadOfThrowing()
    {
        var agent = NewAgent(AgentScheduleKind.Cron);
        agent.CronExpression = "garbage";

        ScheduleCalculator.ComputeNextRunAt(agent, DateTimeOffset.UtcNow).Should().BeNull();
    }
}
