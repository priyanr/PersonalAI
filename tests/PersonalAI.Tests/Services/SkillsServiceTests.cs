using FluentAssertions;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.Skills;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Tests.Services;

public class SkillsServiceTests
{
    private static (SkillsService Service, AppDbContext Db) CreateService()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        return (new SkillsService(db, new SkillsRepository(db)), db);
    }

    private static SaveSkillRequest ValidRequest(string name = "My skill") =>
        new(name, "does a thing", "You do the thing.", null, "llama3.1:8b", null, ["web_search"], []);

    [Fact]
    public async Task Create_ValidRequest_StoresTheSkill()
    {
        var (service, db) = CreateService();
        await using var _ = db;

        var created = await service.Create(ValidRequest(" My skill "), CancellationToken.None);

        var dto = await service.Get(created.Id, CancellationToken.None);
        dto.Name.Should().Be("My skill");
        dto.ProviderKey.Should().Be("ollama");
        dto.EnabledTools.Should().Equal("web_search");
    }

    [Theory]
    [InlineData("", "prompt", "model", "Name is required.")]
    [InlineData("name", "", "model", "Prompt is required.")]
    [InlineData("name", "prompt", "", "ModelName is required.")]
    public async Task Create_MissingRequiredField_IsInvalid(string name, string prompt, string model, string message)
    {
        var (service, db) = CreateService();
        await using var _ = db;

        var act = () => service.Create(new SaveSkillRequest(name, null, prompt, "ollama", model, null, [], []), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>().WithMessage(message);
        (await service.GetAll(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Update_ExistingSkill_ChangesFieldsAndTimestamp()
    {
        var (service, db) = CreateService();
        await using var _ = db;
        var created = await service.Create(ValidRequest(), CancellationToken.None);

        var updated = await service.Update(
            created.Id, new SaveSkillRequest("Renamed", "new desc", "new prompt", "ollama", "llama3.1:8b", 4096, ["read_file"], ["API_KEY"]),
            CancellationToken.None);

        updated.Name.Should().Be("Renamed");
        updated.NumCtx.Should().Be(4096);
        updated.SecretKeys.Should().Equal("API_KEY");
        updated.UpdatedAt.Should().BeOnOrAfter(created.UpdatedAt);
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
    public async Task Delete_ExistingSkill_RemovesIt()
    {
        var (service, db) = CreateService();
        await using var _ = db;
        var created = await service.Create(ValidRequest(), CancellationToken.None);

        await service.Delete(created.Id, CancellationToken.None);

        (await service.GetAll(CancellationToken.None)).Should().BeEmpty();
    }
}
