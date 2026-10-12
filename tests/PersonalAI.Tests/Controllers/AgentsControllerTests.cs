using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using PersonalAI.Api.Controllers;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.Agent;
using PersonalAI.Core.Services.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace PersonalAI.Tests.Controllers;

public class AgentsControllerTests
{
    private sealed class FakeAgentExecutionService : IAgentExecutionService
    {
        public async IAsyncEnumerable<AgentStreamEvent> RunAsync(Guid agentId, AgentRunTrigger trigger, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new DoneEvent();
        }
    }

    private static (AgentsController Controller, AppDbContext Db, AgentRunsRepository RunsRepo) CreateController()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var repo = new AgentsRepository(db);
        var runsRepo = new AgentRunsRepository(db);
        return (new AgentsController(repo, runsRepo, new FakeAgentExecutionService(), Mock.Of<ILogger<AgentsController>>()), db, runsRepo);
    }

    private static CreateAgentRequest ValidCreateRequest(string name = "My agent") =>
        new(name, "does a thing", "You orchestrate.", "ollama", "llama3.1:8b", null, [], [], null,
            AgentScheduleKind.Manual, null, null, null, true);

    [Fact]
    public async Task Create_ValidRequest_ReturnsDto()
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;

        var result = await controller.Create(ValidCreateRequest(), CancellationToken.None);
        var dto = ((CreatedAtActionResult)result.Result!).Value as AgentDto;

        dto!.Name.Should().Be("My agent");
        dto.ScheduleKind.Should().Be(AgentScheduleKind.Manual);
    }

    [Theory]
    [InlineData("", "prompt", "model")]
    [InlineData("name", "", "model")]
    [InlineData("name", "prompt", "")]
    public async Task Create_MissingRequiredField_ReturnsBadRequest(string name, string prompt, string model)
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;

        var request = new CreateAgentRequest(name, null, prompt, "ollama", model, null, [], [], null, AgentScheduleKind.Manual, null, null, null, true);
        var result = await controller.Create(request, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_IntervalScheduleWithoutMinutes_ReturnsBadRequest()
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;

        var request = new CreateAgentRequest(
            "a", null, "p", "ollama", "m", null, [], [], null, AgentScheduleKind.Interval, null, null, null, true);
        var result = await controller.Create(request, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_CronScheduleWithInvalidExpression_ReturnsBadRequest()
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;

        var request = new CreateAgentRequest(
            "a", null, "p", "ollama", "m", null, [], [], null, AgentScheduleKind.Cron, null, null, "garbage", true);
        var result = await controller.Create(request, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Get_UnknownId_ReturnsNotFound()
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;

        var result = await controller.Get(Guid.NewGuid(), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Update_ExistingAgent_ReturnsUpdatedDto()
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;
        var created = ((CreatedAtActionResult)(await controller.Create(ValidCreateRequest(), CancellationToken.None)).Result!).Value as AgentDto;

        var result = await controller.Update(
            created!.Id,
            new UpdateAgentRequest("Renamed", "new desc", "new prompt", "ollama", "llama3.1:8b", 4096, [], ["gmail"], 20,
                AgentScheduleKind.Interval, null, 45, null, true),
            CancellationToken.None);

        var dto = ((OkObjectResult)result.Result!).Value as AgentDto;
        dto!.Name.Should().Be("Renamed");
        dto.ScheduleKind.Should().Be(AgentScheduleKind.Interval);
        dto.IntervalMinutes.Should().Be(45);
        dto.NextRunAt.Should().NotBeNull();
        dto.EnabledTools.Should().Equal("gmail");
        dto.MaxLlmCalls.Should().Be(20);
    }

    [Fact]
    public async Task Update_UnknownId_ReturnsNotFound()
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;

        var result = await controller.Update(
            Guid.NewGuid(),
            new UpdateAgentRequest("name", null, "prompt", "ollama", "model", null, [], [], null, AgentScheduleKind.Manual, null, null, null, true),
            CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Delete_ExistingAgent_ReturnsNoContent()
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;
        var created = ((CreatedAtActionResult)(await controller.Create(ValidCreateRequest(), CancellationToken.None)).Result!).Value as AgentDto;

        var result = await controller.Delete(created!.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Delete_UnknownId_ReturnsNotFound()
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;

        var result = await controller.Delete(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetRuns_ReturnsSummariesForThatAgentOnly()
    {
        var (controller, db, runsRepo) = CreateController();
        await using var _1 = db;
        var agentId = Guid.NewGuid();
        var otherAgentId = Guid.NewGuid();
        await runsRepo.CreateAsync(agentId, AgentRunTrigger.Manual, CancellationToken.None);
        await runsRepo.CreateAsync(otherAgentId, AgentRunTrigger.Manual, CancellationToken.None);

        var result = await controller.GetRuns(agentId, 0, CancellationToken.None);
        var runs = ((OkObjectResult)result.Result!).Value as IReadOnlyList<AgentRunSummaryDto>;

        runs!.Should().ContainSingle();
        runs![0].AgentId.Should().Be(agentId);
    }

    [Fact]
    public async Task GetRun_ExistingRunForDifferentAgent_ReturnsNotFound()
    {
        var (controller, db, runsRepo) = CreateController();
        await using var _1 = db;
        var run = await runsRepo.CreateAsync(Guid.NewGuid(), AgentRunTrigger.Manual, CancellationToken.None);

        var result = await controller.GetRun(Guid.NewGuid(), run.Id, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetRun_ExistingRun_ReturnsDetailWithTranscript()
    {
        var (controller, db, runsRepo) = CreateController();
        await using var _1 = db;
        var agentId = Guid.NewGuid();
        var run = await runsRepo.CreateAsync(agentId, AgentRunTrigger.Scheduled, CancellationToken.None);
        await runsRepo.CompleteAsync(run.Id, AgentRunStatus.Succeeded, "ok",
            """[{"Kind":"text","SkillName":null,"Content":"hi","IsError":false}]""", CancellationToken.None);

        var result = await controller.GetRun(agentId, run.Id, CancellationToken.None);
        var dto = ((OkObjectResult)result.Result!).Value as AgentRunDetailDto;

        dto!.Status.Should().Be(AgentRunStatus.Succeeded);
        dto.Transcript.Should().ContainSingle(e => e.Content == "hi");
    }

    [Fact]
    public async Task Create_ReasoningEffort_IsNormalizedAndReturned()
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;

        var result = await controller.Create(ValidCreateRequest() with { ReasoningEffort = " HIGH " }, CancellationToken.None);

        (((CreatedAtActionResult)result.Result!).Value as AgentDto)!.ReasoningEffort.Should().Be("high");
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    public async Task Create_AutoOrBlankReasoningEffort_IsStoredAsNull(string effort)
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;

        var result = await controller.Create(ValidCreateRequest() with { ReasoningEffort = effort }, CancellationToken.None);

        (((CreatedAtActionResult)result.Result!).Value as AgentDto)!.ReasoningEffort.Should().BeNull();
    }

    [Fact]
    public async Task Create_UnknownReasoningEffort_ReturnsBadRequest()
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;

        var result = await controller.Create(ValidCreateRequest() with { ReasoningEffort = "extreme" }, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }
}
