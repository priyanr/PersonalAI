using FluentAssertions;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.Secrets;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Tests.Services;

public class SecretsServiceTests
{
    private static (SecretsService Service, AppDbContext Db) CreateService()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        return (new SecretsService(db, new SecretsRepository(db)), db);
    }

    [Fact]
    public async Task Create_ValidRequest_StoresValueAndReturnsDtoWithoutIt()
    {
        var (service, db) = CreateService();
        await using var _ = db;

        var dto = await service.Create(new CreateSecretRequest(" GITHUB_TOKEN ", "ghp_abc"), CancellationToken.None);

        dto.Should().Be(new SecretDto("GITHUB_TOKEN", true, dto.UpdatedAt));
        (await new SecretsRepository(db).GetValueAsync("GITHUB_TOKEN", CancellationToken.None)).Should().Be("ghp_abc");
    }

    [Theory]
    [InlineData("  ", "value", "Key is required.")]
    [InlineData("KEY", "", "Value is required.")]
    public async Task Create_MissingField_IsInvalid(string key, string value, string message)
    {
        var (service, db) = CreateService();
        await using var _ = db;

        var act = () => service.Create(new CreateSecretRequest(key, value), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>().WithMessage(message);
    }

    [Fact]
    public async Task Create_DuplicateKey_Conflicts()
    {
        var (service, db) = CreateService();
        await using var _ = db;
        await service.Create(new CreateSecretRequest("KEY", "value"), CancellationToken.None);

        var act = () => service.Create(new CreateSecretRequest("KEY", "value2"), CancellationToken.None);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Update_ExistingKey_ChangesTheValue()
    {
        var (service, db) = CreateService();
        await using var _ = db;
        await service.Create(new CreateSecretRequest("KEY", "old"), CancellationToken.None);

        await service.Update("KEY", new UpdateSecretRequest("new"), CancellationToken.None);

        (await new SecretsRepository(db).GetValueAsync("KEY", CancellationToken.None)).Should().Be("new");
    }

    [Fact]
    public async Task Update_UnknownKey_IsNotFound()
    {
        var (service, db) = CreateService();
        await using var _ = db;

        var act = () => service.Update("does-not-exist", new UpdateSecretRequest("value"), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Delete_ExistingKey_RemovesIt()
    {
        var (service, db) = CreateService();
        await using var _ = db;
        await service.Create(new CreateSecretRequest("KEY", "value"), CancellationToken.None);

        await service.Delete("KEY", CancellationToken.None);

        (await service.GetAll(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_UnknownKey_IsNotFound()
    {
        var (service, db) = CreateService();
        await using var _ = db;

        var act = () => service.Delete("does-not-exist", CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task GetAll_NeverReturnsTheValue()
    {
        var (service, db) = CreateService();
        await using var _ = db;
        await service.Create(new CreateSecretRequest("KEY", "super-secret"), CancellationToken.None);

        var secrets = await service.GetAll(CancellationToken.None);

        secrets.Should().ContainSingle().Which.HasValue.Should().BeTrue();
        System.Text.Json.JsonSerializer.Serialize(secrets).Should().NotContain("super-secret");
    }
}
