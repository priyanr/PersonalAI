using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using PersonalAI.Api.Controllers;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.Agent;
using PersonalAI.Core.Services.Skills;
using PersonalAI.Core.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace PersonalAI.Tests.Controllers;

public class SkillsControllerTests
{
    private sealed class FakeSkillExecutionService : ISkillExecutionService
    {
        public async IAsyncEnumerable<AgentStreamEvent> RunAsync(Guid skillId, string? input, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new DoneEvent();
        }
    }

    private static (SkillsController Controller, AppDbContext Db) CreateController()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var repo = new SkillsRepository(db);
        var tools = new ToolRegistry([new PersonalAI.Tests.Agent.FakeTool("web_search", (_, _) => new ToolCompleted("ok"))]);
        return (new SkillsController(repo, new FakeSkillExecutionService(), tools, Mock.Of<ILogger<SkillsController>>()), db);
    }

    private static CreateSkillRequest ValidCreateRequest(string name = "My skill") =>
        new(name, "does a thing", "You do the thing.", "ollama", "llama3.1:8b", null, ["web_search"], []);

    [Fact]
    public async Task Create_ValidRequest_ReturnsDto()
    {
        var (controller, db) = CreateController();
        await using var _1 = db;

        var result = await controller.Create(ValidCreateRequest(), CancellationToken.None);
        var dto = ((CreatedAtActionResult)result.Result!).Value as SkillDto;

        dto!.Name.Should().Be("My skill");
        dto.EnabledTools.Should().Contain("web_search");
    }

    [Theory]
    [InlineData("", "prompt", "model")]
    [InlineData("name", "", "model")]
    [InlineData("name", "prompt", "")]
    public async Task Create_MissingRequiredField_ReturnsBadRequest(string name, string prompt, string model)
    {
        var (controller, db) = CreateController();
        await using var _1 = db;

        var result = await controller.Create(new CreateSkillRequest(name, null, prompt, "ollama", model, null, [], []), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Get_UnknownId_ReturnsNotFound()
    {
        var (controller, db) = CreateController();
        await using var _1 = db;

        var result = await controller.Get(Guid.NewGuid(), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Update_ExistingSkill_ReturnsUpdatedDto()
    {
        var (controller, db) = CreateController();
        await using var _1 = db;
        var created = ((CreatedAtActionResult)(await controller.Create(ValidCreateRequest(), CancellationToken.None)).Result!).Value as SkillDto;

        var result = await controller.Update(
            created!.Id,
            new UpdateSkillRequest("Renamed", "new desc", "new prompt", "ollama", "llama3.1:8b", 4096, ["read_file"], ["API_KEY"]),
            CancellationToken.None);

        var dto = ((OkObjectResult)result.Result!).Value as SkillDto;
        dto!.Name.Should().Be("Renamed");
        dto.NumCtx.Should().Be(4096);
        dto.SecretKeys.Should().Contain("API_KEY");
    }

    [Fact]
    public async Task Update_UnknownId_ReturnsNotFound()
    {
        var (controller, db) = CreateController();
        await using var _1 = db;

        var result = await controller.Update(
            Guid.NewGuid(),
            new UpdateSkillRequest("name", null, "prompt", "ollama", "model", null, [], []),
            CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Delete_ExistingSkill_ReturnsNoContent()
    {
        var (controller, db) = CreateController();
        await using var _1 = db;
        var created = ((CreatedAtActionResult)(await controller.Create(ValidCreateRequest(), CancellationToken.None)).Result!).Value as SkillDto;

        var result = await controller.Delete(created!.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Delete_UnknownId_ReturnsNotFound()
    {
        var (controller, db) = CreateController();
        await using var _1 = db;

        var result = await controller.Delete(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void GetAvailableTools_ReturnsRegisteredToolDefinitions()
    {
        var (controller, db) = CreateController();

        var result = controller.GetAvailableTools();
        var tools = ((OkObjectResult)result.Result!).Value as IReadOnlyList<AvailableToolDto>;

        tools!.Should().ContainSingle(t => t.Name == "web_search");
        db.Dispose();
    }
}
