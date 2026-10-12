using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using PersonalAI.Api.Controllers;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.Secrets;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Tests.Controllers;

public class SecretsControllerTests
{
    private static (SecretsController Controller, AppDbContext Db, SecretsCache Cache) CreateController()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var repo = new SecretsRepository(db);
        var cache = new SecretsCache();
        return (new SecretsController(repo, cache), db, cache);
    }

    [Fact]
    public async Task Create_ValidRequest_ReturnsDtoWithoutValueAndUpdatesCache()
    {
        var (controller, db, cache) = CreateController();
        await using var _1 = db;

        var result = await controller.Create(new CreateSecretRequest("GITHUB_TOKEN", "ghp_abc"), CancellationToken.None);
        var dto = ((OkObjectResult)result.Result!).Value as SecretDto;

        dto!.Key.Should().Be("GITHUB_TOKEN");
        dto.HasValue.Should().BeTrue();
        cache.GetValue("GITHUB_TOKEN").Should().Be("ghp_abc");
    }

    [Fact]
    public async Task Create_BlankKey_ReturnsBadRequest()
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;

        var result = await controller.Create(new CreateSecretRequest("  ", "value"), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_BlankValue_ReturnsBadRequest()
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;

        var result = await controller.Create(new CreateSecretRequest("KEY", ""), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_DuplicateKey_ReturnsConflict()
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;
        await controller.Create(new CreateSecretRequest("KEY", "value"), CancellationToken.None);

        var result = await controller.Create(new CreateSecretRequest("KEY", "value2"), CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Update_ExistingKey_UpdatesValueAndCache()
    {
        var (controller, db, cache) = CreateController();
        await using var _1 = db;
        await controller.Create(new CreateSecretRequest("KEY", "old"), CancellationToken.None);

        var result = await controller.Update("KEY", new UpdateSecretRequest("new"), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        cache.GetValue("KEY").Should().Be("new");
    }

    [Fact]
    public async Task Update_UnknownKey_ReturnsNotFound()
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;

        var result = await controller.Update("does-not-exist", new UpdateSecretRequest("value"), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Delete_ExistingKey_RemovesFromCache()
    {
        var (controller, db, cache) = CreateController();
        await using var _1 = db;
        await controller.Create(new CreateSecretRequest("KEY", "value"), CancellationToken.None);

        var result = await controller.Delete("KEY", CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        cache.GetValue("KEY").Should().BeNull();
    }

    [Fact]
    public async Task Delete_UnknownKey_ReturnsNotFound()
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;

        var result = await controller.Delete("does-not-exist", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetAll_NeverReturnsRawValue()
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;
        await controller.Create(new CreateSecretRequest("KEY", "super-secret"), CancellationToken.None);

        var result = await controller.GetAll(CancellationToken.None);
        var secrets = ((OkObjectResult)result.Result!).Value as IReadOnlyList<SecretDto>;

        secrets!.Single().HasValue.Should().BeTrue();
    }
}
