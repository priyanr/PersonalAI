using System.Text.Json;
using FluentAssertions;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.Agents;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Tests.Services;

public class AgentsServiceTests
{
    private static (AgentsService Service, AppDbContext Db) CreateService()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        return (new AgentsService(db, new AgentsRepository(db), new AgentRunsRepository(db), TestAgentTypes.Registry), db);
    }

    private static SaveAgentRequest ValidRequest(string name = "My agent") =>
        new(name, "does a thing", "You orchestrate.", "ollama", "llama3.1:8b", null, [], [], null,
            AgentScheduleKind.Manual, null, null, null, true);

    [Fact]
    public async Task Create_ValidManualAgent_IsGeneralWithNoNextRun()
    {
        var (service, db) = CreateService();
        await using var _ = db;

        var dto = await service.Create(ValidRequest(" My agent "), CancellationToken.None);

        dto.Name.Should().Be("My agent");
        dto.Type.Should().Be(AgentType.General);
        dto.ScheduleKind.Should().Be(AgentScheduleKind.Manual);
        dto.NextRunAt.Should().BeNull();
    }

    [Fact]
    public async Task Create_IntervalSchedule_ComputesNextRunAt()
    {
        var (service, db) = CreateService();
        await using var _ = db;

        var dto = await service.Create(ValidRequest() with { ScheduleKind = AgentScheduleKind.Interval, IntervalMinutes = 30 }, CancellationToken.None);

        dto.NextRunAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(30), TimeSpan.FromMinutes(1));
    }

    [Theory]
    [InlineData("", "prompt", "model")]
    [InlineData("name", "", "model")]
    [InlineData("name", "prompt", "")]
    public async Task Create_MissingRequiredField_IsInvalid(string name, string prompt, string model)
    {
        var (service, db) = CreateService();
        await using var _ = db;

        var act = () => service.Create(ValidRequest() with { Name = name, Prompt = prompt, ModelName = model }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>();
    }

    [Theory]
    [InlineData(AgentScheduleKind.Interval, null)]
    [InlineData(AgentScheduleKind.Cron, "garbage")]
    public async Task Create_InvalidSchedule_IsInvalid(AgentScheduleKind kind, string? cron)
    {
        var (service, db) = CreateService();
        await using var _ = db;

        var act = () => service.Create(ValidRequest() with { ScheduleKind = kind, CronExpression = cron }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>();
    }

    [Fact]
    public async Task Create_NonPositiveMaxLlmCalls_IsInvalid()
    {
        var (service, db) = CreateService();
        await using var _ = db;

        var act = () => service.Create(ValidRequest() with { MaxLlmCalls = 0 }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>();
    }

    [Theory]
    [InlineData(" HIGH ", "high")]
    [InlineData("auto", null)]
    [InlineData("", null)]
    public async Task Create_ReasoningEffort_IsNormalized(string effort, string? expected)
    {
        var (service, db) = CreateService();
        await using var _ = db;

        var dto = await service.Create(ValidRequest() with { ReasoningEffort = effort }, CancellationToken.None);

        dto.ReasoningEffort.Should().Be(expected);
    }

    [Fact]
    public async Task Create_UnknownReasoningEffort_IsInvalid()
    {
        var (service, db) = CreateService();
        await using var _ = db;

        var act = () => service.Create(ValidRequest() with { ReasoningEffort = "extreme" }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>();
    }

    [Fact]
    public async Task Update_ExistingAgent_ChangesFieldsAndRearmsSchedule()
    {
        var (service, db) = CreateService();
        await using var _ = db;
        var created = await service.Create(ValidRequest(), CancellationToken.None);

        var dto = await service.Update(
            created.Id,
            new SaveAgentRequest("Renamed", "new desc", "new prompt", "ollama", "llama3.1:8b", 4096, [], ["gmail"], 20,
                AgentScheduleKind.Interval, null, 45, null, true),
            CancellationToken.None);

        dto.Name.Should().Be("Renamed");
        dto.IntervalMinutes.Should().Be(45);
        dto.NextRunAt.Should().NotBeNull();
        dto.EnabledTools.Should().Equal("gmail");
        dto.MaxLlmCalls.Should().Be(20);
    }

    [Fact]
    public async Task GetUpdateDelete_UnknownId_AreNotFound()
    {
        var (service, db) = CreateService();
        await using var _ = db;
        var id = Guid.NewGuid();

        await FluentActions.Awaiting(() => service.Get(id, CancellationToken.None)).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => service.Update(id, ValidRequest(), CancellationToken.None)).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => service.Delete(id, CancellationToken.None)).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Delete_ExistingAgent_RemovesIt()
    {
        var (service, db) = CreateService();
        await using var _ = db;
        var created = await service.Create(ValidRequest(), CancellationToken.None);

        await service.Delete(created.Id, CancellationToken.None);

        (await service.GetAll(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetRuns_ReturnsSummariesForThatAgentOnly()
    {
        var (service, db) = CreateService();
        await using var _ = db;
        var agentId = Guid.NewGuid();
        await TestSeed.RunAsync(db, agentId, AgentRunTrigger.Manual);
        await TestSeed.RunAsync(db, Guid.NewGuid(), AgentRunTrigger.Manual);

        var runs = await service.GetRuns(agentId, 0, CancellationToken.None);

        runs.Should().ContainSingle().Which.AgentId.Should().Be(agentId);
    }

    [Fact]
    public async Task GetRun_RunOfAnotherAgent_IsNotFound()
    {
        var (service, db) = CreateService();
        await using var _ = db;
        var run = await TestSeed.RunAsync(db, Guid.NewGuid(), AgentRunTrigger.Manual);

        var act = () => service.GetRun(Guid.NewGuid(), run.Id, CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task GetRun_ExistingRun_ReturnsDetailWithTranscript()
    {
        var (service, db) = CreateService();
        await using var _ = db;
        var agentId = Guid.NewGuid();
        var run = await TestSeed.RunAsync(db, agentId, AgentRunTrigger.Scheduled);
        run.Status = AgentRunStatus.Succeeded;
        run.TranscriptJson = """[{"Kind":"text","SkillName":null,"Content":"hi","IsError":false}]""";
        await db.SaveChangesAsync();

        var dto = await service.GetRun(agentId, run.Id, CancellationToken.None);

        dto.Status.Should().Be(AgentRunStatus.Succeeded);
        dto.Transcript.Should().ContainSingle(e => e.Content == "hi");
    }

    [Fact]
    public async Task EnsureRunAcceptsMessage_BlankMessage_RunningRun_OrOtherAgent_AreRejected()
    {
        var (service, db) = CreateService();
        await using var _ = db;
        var agentId = Guid.NewGuid();
        var run = await TestSeed.RunAsync(db, agentId, AgentRunTrigger.Manual);

        await FluentActions.Awaiting(() => service.EnsureRunAcceptsMessage(agentId, run.Id, " ", CancellationToken.None))
            .Should().ThrowAsync<InvalidRequestException>();
        await FluentActions.Awaiting(() => service.EnsureRunAcceptsMessage(agentId, run.Id, "hi", CancellationToken.None))
            .Should().ThrowAsync<ConflictException>();
        await FluentActions.Awaiting(() => service.EnsureRunAcceptsMessage(Guid.NewGuid(), run.Id, "hi", CancellationToken.None))
            .Should().ThrowAsync<NotFoundException>();
    }

    // ---- agent type must survive a PATCH that doesn't mention it ----

    private static async Task<AgentDto> CreateDashboardAgentAsync(AgentsService service)
    {
        return await service.Create(
            ValidRequest("monitor") with
            {
                Type = AgentType.DashboardAlerts,
                TypeConfig = JsonDocument.Parse("{\"collectIntervalMinutes\":2,\"retentionDays\":7}").RootElement
            }, CancellationToken.None);
    }

    [Fact]
    public async Task Update_WithoutTypeFields_KeepsTheAgentsTypeAndSettings()
    {
        var (service, db) = CreateService();
        await using var _ = db;
        var agent = await CreateDashboardAgentAsync(service);

        var dto = await service.Update(agent.Id, ValidRequest("renamed"), CancellationToken.None);

        dto.Name.Should().Be("renamed");
        dto.Type.Should().Be(AgentType.DashboardAlerts);
        dto.TypeConfig.GetProperty("collectIntervalMinutes").GetInt32().Should().Be(2);
        dto.TypeConfig.GetProperty("retentionDays").GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task Update_SameTypeWithNewConfig_ReplacesTheSettings()
    {
        var (service, db) = CreateService();
        await using var _ = db;
        var agent = await CreateDashboardAgentAsync(service);

        var dto = await service.Update(agent.Id, ValidRequest("monitor") with
        {
            Type = AgentType.DashboardAlerts,
            TypeConfig = JsonDocument.Parse("{\"collectIntervalMinutes\":4}").RootElement
        }, CancellationToken.None);

        dto.TypeConfig.GetProperty("collectIntervalMinutes").GetInt32().Should().Be(4);
    }

    [Fact]
    public async Task Update_ToAnotherTypeWithoutConfig_GetsThatTypesDefaults()
    {
        var (service, db) = CreateService();
        await using var _ = db;
        var agent = await CreateDashboardAgentAsync(service);

        var dto = await service.Update(agent.Id, ValidRequest("monitor") with { Type = AgentType.General }, CancellationToken.None);

        dto.Type.Should().Be(AgentType.General);
        dto.TypeConfig.ToString().Should().Be("{}");
    }

    [Fact]
    public async Task Update_InvalidTypeConfig_IsRejected_AndTheAgentIsUnchanged()
    {
        var (service, db) = CreateService();
        await using var _ = db;
        var agent = await CreateDashboardAgentAsync(service);

        var act = () => service.Update(agent.Id, ValidRequest("monitor") with
        {
            Type = AgentType.DashboardAlerts,
            TypeConfig = JsonDocument.Parse("{\"collectIntervalMinutes\":99}").RootElement
        }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>();
        (await service.Get(agent.Id, CancellationToken.None)).TypeConfig.GetProperty("collectIntervalMinutes").GetInt32().Should().Be(2);
    }
}
